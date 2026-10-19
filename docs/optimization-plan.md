# LocalTransfer 优化方案（对照 LocalSend 最新实现）

> 生成时间：2026-10-16　最后更新：2026-10-19
> 参照版本：`localsend/localsend` @ `d01fdb1`（2026-10-15，Apache-2.0）
> 参照代码位置：项目外的临时克隆（**仅作设计对照，未复制任何代码**；设计要点已吸收进本文档，
> 本地克隆已于 2026-10-19 清理，需要时重新 clone 即可）
>
> **状态：已实施完毕。** 三批改造（续传闭环 / 自动发现 + 整批批准 / 双向历史 + 限流）全部落地并验证，
> 65 个测试通过。本文档因此转为**实施记录**保留；后续计划见 [实施路线](roadmap.md)，
> 协议细节见 [协议说明](protocol-v1.md)。

---

## 0. 一句话结论

**"传输中断/失败"不是某一处 bug，而是协议形态决定的：**

本项目把一次传输拆成「每文件一次人工批准 → 串行 4 MiB 分块 PUT → 完成校验」，
而**客户端没有任何"失败后接着传"的外层恢复逻辑**——重试只有 3 次、总窗口不到 1 秒。
于是任何一次 Wi-Fi 抖动、路由器重连、屏幕熄灭，都会把一个**本来能续传**的传输变成
永久失败，并且 `MainPage.OnSendClicked` 的 `foreach` 会连带**放弃后面所有文件**。

LocalSend 走的是另一条路：**整批一次批准 + 整文件流式 POST + 多文件并行 + 服务端允许有限次重传**。
本方案不推翻现有架构，而是把上述差异里**收益最高的 9 处**补进现有协议。

---

## 1. 参照项目解析：LocalSend 到底怎么做的

LocalSend 最新代码已把核心重写为 Rust（`packages/core`），Flutter 只做 UI。关键事实：

### 1.1 设备发现（你要的"自动发现"）

| 项 | 值 | 说明 |
| --- | --- | --- |
| IPv4 组播组 | `224.0.0.167` | 特意选在 `224.0.0.0/24`，因为**部分 Android 设备只有这个网段能收组播** |
| IPv6 组播组 | `ff12::fd3a:e420` | 与 IPv4 并行公告，链路本地 scope |
| 端口 | `53317` | 与 HTTP 服务端口相同 |
| 公告节奏 | `100ms / 500ms / 2000ms` **连发 3 次** | 单包易丢、刚入网设备可能没就绪 |
| 收到公告后 | **不用 UDP 回复**，改用 HTTP `POST /register` 反查 | 去重、认证、拿证书指纹都走 HTTP |
| 自己的报文 | 按 `fingerprint` 过滤（回环会收到自己的包） | fingerprint = **证书 SHA-256**（HTTPS 模式） |
| 兜底 | 子网 HTTP 扫描，并发 50，单次超时 500ms | 组播被禁时仍能发现 |
| iOS 挂起 | socket 被系统回收 → 发 `SocketsFailed` 事件，应用重启发现 | 必须实现的恢复路径 |

> 对本项目最重要的两点：**证书 SHA-256 已经是我们的配对凭据的一部分**，可以直接当 fingerprint 用；
> **Android 必须持有 `WifiManager.MulticastLock`**，否则组播包收不到（这是最常见的"自动发现不工作"原因）。

### 1.2 传输模型（可靠性的根）

```
POST /prepare-upload   → 一次提交整批文件清单，用户只批准一次
                       ← { sessionId, files: { fileId: token } }
POST /upload?sessionId&fileId&token   → 流式传整个文件（不是分块）
GET  /download?sessionId&fileId       → 反向下载
POST /cancel?sessionId
```

- **批准粒度 = 整个 session**，不是每个文件。
- **传输单位 = 整个文件**，一个 HTTP 流；断点粒度是**文件级**（session 里跳过已完成的文件）。
- 官方注释明确写着 "**This method can be called in parallel for multiple files**"，实际并行度约 2。
- 校验失败返回 `422`，服务端把该文件**重置为 Pending 并允许有限次重试**（`attempts` 计数），而不是直接判死。
- 连接保护：总并发 64，**每 IP 8**，防止单设备占满。

### 1.3 与本地项目的关键差异

| 维度 | 本项目（v1） | LocalSend（v2.2） |
| --- | --- | --- |
| 发现 | 无（必须扫码） | UDP 组播 + HTTP 扫描兜底 |
| 批准粒度 | **每文件一次**（10 条待批上限） | 整批一次 |
| 传输单位 | 4 MiB 分块，每块一次 PUT | 整个文件一个流 |
| 断点粒度 | 块级（服务端 checkpoint） | 文件级 |
| 并发 | **全串行** | 多文件并行 |
| 失败后 | **无恢复循环**，重试 3 次 / <1s | 有，且允许 422 重传 |
| 取消 | **不支持**（未传 CancellationToken） | 有 |
| 连接限制 | 无 | 64 / 8 |

---

## 2. 根因清单（按"最可能造成中断/失败"排序）

### P0 — 直接导致传输失败

**P0-1　客户端没有"续传闭环"，块级断点形同虚设**
`src/LocalTransfer.Client/LocalTransferClient.cs:71-92`
分块循环里任一块连续失败 3 次即抛异常，整个 `UploadAsync` 退出。
服务端其实已经把 checkpoint 落盘（`ResumableFileReceiver`），`GET /transfers/{id}` 也会返回 `missingChunks`，
**但没有任何代码在失败后重新查询并继续**。所以"能续传"的能力被浪费掉了。

**P0-2　重试策略远短于真实网络抖动**
`TransferTuning.TransferMaxAttempts = 3`、`TransferRetryBaseDelay = 250ms`
→ 总窗口 `250 + 500 = 750ms`。
而 Wi-Fi 切频段、路由器 DHCP 续租、Android 息屏省电，恢复时间普遍是**秒级到十几秒**。
后果：短暂断连必然被判死。

**P0-3　一个文件失败，后面全部放弃**
`src/LocalTransfer.Mobile/MainPage.xaml.cs:202-209`
`foreach (var file in _selectedFiles)` 无 try/catch 隔离，异常直接跳出循环。
用户看到"发送失败"，但**不知道哪几个成功了**，剩余文件也不再传。

**P0-4　每文件一次人工批准，且窗口只有 10 分钟**
`TransferTuning.ClientApprovalWait = 10min`，`InboundTransferCoordinator.MaxPendingOffersPerDevice = 10`。
100 个文件 = 100 次弹窗确认；用户离开电脑 >10 分钟即整批失败。
（协议本身允许每文件独立，但**批量场景下这个粒度的体验和成功率都不成立**。）

### P1 — 放大失败概率 / 拖慢速度

**P1-1　Android 上源文件被读两遍，且 content URI 被打开两次**
`UploadAsync:41` 先 `HashAndMeasureAsync` 完整读一遍算 SHA-256，
`UploadAsync:70` 再 `SequentialChunkSource` 开第二个流读一遍上传。
- 10 GB 文件 = 先读 10 GB 再传 10 GB，耗时接近翻倍。
- 微信/QQ `ACTION_SEND` 给的 `content://` 授权通常是**临时**的，第二次打开可能直接失败。

**P1-2　非 seek 流的断点续传会退化成"海量重读"**
`LocalTransferClient.cs:390-415`：流不可 seek 且 `offset < _position` 时，重新打开并 `SkipAsync`。
若 `missingChunks` 分散（如 `[0, 5000, 12000]`），每块都要从 0 重新 skip，**I/O 放大百倍**。

**P1-3　服务端把同一个 transfer 的分块写串行化了**
`InboundTransferCoordinator.WriteChunkAsync` 用 `transfer.Gate` 包住整个写盘过程。
即使客户端将来并发上传，服务端也会排队；且 `ResumableFileReceiver` 已用 `FileOptions.RandomAccess` + offset 写，
本身是并发安全的，这把锁过粗。

**P1-4　Kestrel 请求体上限卡得太死**
`CoordinatorHost.ConfigureKestrel`：`MaxRequestBodySize = DefaultChunkSize + 64KiB`。
一旦以后把 chunk 调大（或客户端多传一点），会变成静默 `413`，且客户端不会重试 413。

**P1-5　Kestrel 默认速率保护可能误杀弱网**
未显式设置 `MinRequestBodyDataRate`（默认 240 B/s、宽限 5 s）。
手机切后台被限流时，上传请求可能被服务端直接掐断。

### P2 — 缺失能力（对应你的另两个诉求）

**P2-1　无自动发现**——完全依赖二维码，每次换网络/换设备都要走一遍配对。
**P2-2　无发送侧历史**——`历史记录` 页只扫接收目录，发出去的文件不落库。
**P2-3　无任务管理**——没有暂停 / 继续 / 取消 / 单条重试；`UploadAsync` 连 CancellationToken 都没接。
**P2-4　服务端传输状态只在内存**——`_transfers` 字典一重启就没了，手机若在电脑重启后继续上传会拿到 404/409。

---

## 3. 改造方案

分三个阶段，每阶段可独立验证、独立交付。**建议按阶段 1 → 2 → 3 顺序做**，
其中**阶段 2 直接解决你反馈的"传输中断/失败"**，优先级最高。

### 阶段 0（前置，1 天）：先让它"看得见"

> 没有日志，后面所有修复都无法证明有效——本项目的 README 明确写了"尚未真机联调"。

1. 客户端（`LocalTransfer.Mobile`）把每次请求的 `方法 / 路径 / 耗时 / 状态码 / 异常类型` 写入
   `GetExternalFilesDir(null)` 下的滚动日志（数据线可直接拷出；`AppDataDirectory` 拷不出）。
2. Windows 端把 `InboundTransferCoordinator` 的状态迁移、approve/reject、chunk 写入失败原因写入 `%LocalAppData%\LocalTransfer\logs`。
3. 加一个隐藏的"诊断"入口，可一键导出两端日志。

### 阶段 1（3–4 天）：自动发现

**目标**：同一 Wi-Fi 下，手机打开 App 就能看到电脑，点击即用；二维码降级为兜底。

- 新增 `src/LocalTransfer.Coordinator/Discovery/MulticastAnnouncer.cs`
  - 绑定 `224.0.0.167:53317`（IPv4）+ `ff12::fd3a:e420`（IPv6），**每个可用网卡一个 socket**。
  - 公告体沿用 LocalSend `MulticastMessageV2` 字段：`alias / version / deviceType / fingerprint / port / protocol`。
  - `fingerprint` 直接用现有 `LocalCertificateProvider.GetSha256Fingerprint()`。
  - 公告连发 3 次（100 ms / 500 ms / 2 s），启动时、网络变化时、以及电脑端手动点"刷新"时各发一轮。
  - 回环过滤：按本机 fingerprint 丢弃自己的包。
- 新增 `src/LocalTransfer.Mobile/Services/MulticastDiscovery.cs`
  - ⚠️ **必须在打开发现前持有 `WifiManager.CreateMulticastLock()`**（Android 不收组播的头号原因）。
  - 生活周期：`OnAppearing` 开、`OnDisappearing` 关；失败要能自动重试。
- **信任策略（关键设计取舍）**：
  - 已配对的设备：公告里带**已存凭据的哈希**，两端比对通过即静默可用，**不再要求扫码**。
  - 未配对的设备：列表里显示但置灰，点击走"一键配对"——用公告里的证书指纹完成证书固定，
    仍需用户在电脑上确认一次（保留现有安全模型，不做"无确认自动信任"）。
- 保留二维码路径：组播被路由器禁掉（部分企业 AP）时仍可用。

### 阶段 2（4–6 天）：传输可靠性 —— **本次核心**

1. **引入"整批一次批准"**（对齐 LocalSend `prepare-upload`）
   - 新增 `POST /api/v1/batches`：一次提交多文件清单，返回 `batchId` + 每文件凭证。
   - 电脑端**一次弹窗**批准整批；保留"逐文件拒绝"的能力（在列表里取消勾选）。
   - 现有单文件接口保留，作为兼容路径。
   - 这一步同时解掉 P0-4（100 次确认）和 P0-3（一个失败全停）的一半。

2. **客户端加"续传闭环"**（解 P0-1，收益最高、改动最小）
   ```
   总预算 = 30 分钟（可配）
   while 未完成且未超预算:
       状态 = GET /api/v1/transfers/{id}     // 拿 missingChunks
       并发 PUT 缺失分块（SemaphoreSlim(4)）
       若某块失败 → 指数退避后重进 while 重新拉状态，而不是放弃
   ```
   - 断网时先 `GET /api/v1/health` 探测恢复，再继续，避免空转。

3. **重试策略改为指数退避**（解 P0-2）
   `250ms → 750ms → 2s → 5s → 10s`，最多 5 次；区分"可重试"（网络/超时/5xx/429）与"不可重试"（401/400/409）。

4. **并发分块上传**（解 P1-3、提速）
   - 客户端 `SemaphoreSlim(4)` 并发 PUT 不同分块。
   - 服务端把 `InboundTransferCoordinator.WriteChunkAsync` 的 `transfer.Gate` 收窄为
     **只保护 checkpoint 更新**，写盘靠 `RandomAccess` 按 offset 天然并发安全。

5. **Android 源文件先落缓存**（解 P1-1、P1-2）
   - 对不可 seek 的 `content://` 流：先复制到 `FileSystem.CacheDirectory`，
     再做哈希与分块读取。慢一点，但**只读一遍、可随机访问、不受临时授权影响**。
   - 可 seek 的文件保持直接读取，不做无谓复制。

6. **打通取消链路**（解 P2-3）
   `UploadAsync` / `DownloadAsync` 全链路传 `CancellationToken`；手机端加"取消"按钮，
   取消时调 `DELETE /api/v1/transfers/{id}` 让服务端清理 `.part`。

7. **服务端加固**
   - `MaxRequestBodySize` 改为按 `MaxChunkSize + 1MiB` 计算，并显式设置
     `MinRequestBodyDataRate` / `KeepAliveTimeout`。
   - 加连接限制（总 64 / 每 IP 8，与 LocalSend 对齐）。
   - `_transfers` 状态落盘（`inbound-state.json`），电脑重启后手机能继续传（解 P2-4）。

### 阶段 3（3–4 天）：历史与任务管理

1. **传输历史库**：`%LocalAppData%\LocalTransfer\history.json`（后续可换 SQLite）
   记录 `方向 / 文件名 / 大小 / 起止时间 / 结果 / 失败原因 / SHA-256`，**发送与接收都记**。
   两端的"历史记录"页统一从这个库读。
2. **任务管理**：进行中任务支持**暂停 / 继续 / 取消 / 重试**；批量失败提供"重试全部失败项"。
   利用阶段 2 的续传闭环，重试即"重新进入 while"。
3. **可信设备自动接收开关**：对齐 LocalSend 的 auto-accept，免去每次点确认（默认关闭）。
4. **Windows 传输页增强**：显示实时速率、剩余时间、失败原因原文。

---

## 4. 验收方式（每阶段都要过）

| 场景 | 期望 |
| --- | --- |
| 传输中关闭再打开手机 Wi-Fi | 自动续传，最终 SHA-256 校验通过，无人工干预 |
| 传输中电脑端重启 | 客户端按阶段 2 的重连逻辑恢复（阶段 2 第 7 条落地后） |
| 10 GB 单文件 | 完成且校验通过；耗时不应出现"先读一遍再传一遍"的翻倍特征 |
| 100 个文件批量 | 电脑端**只弹一次**确认；单个失败不影响其余 |
| 息屏 / 切后台 5 分钟 | 传输不中断，或中断后自动恢复 |
| 路由器开启 AP 隔离 | 明确提示"设备不在同一可互访网络"，而不是超时无响应 |
| 未配对设备同网 | 阶段 1 后能在列表中看到（置灰），点击可一键配对 |

---

## 5. 需要你拍板的取舍

1. **是否改协议版本号？** 阶段 1、2 会新增 `POST /api/v1/batches` 并改变上传并发语义。
   建议：**新增接口、保留旧接口**，版本号仍为 1，旧 APK 仍可工作。
2. **一键配对是否允许"零确认"？** 建议**不允许**：保留电脑端一次确认，安全性不打折，
   只是把"每次传输都要确认"降级为"每次配对只确认一次"。
3. **是否顺手对齐 LocalSend 协议？** 你这次没选这项。若要对齐，方案要整体改成
   `prepare-upload / upload` 那套，改动量远大于本方案（约 10–15 天），收益是能和已有 LocalSend 互通。
   **本次不做**，仅在字段命名上预留兼容空间。

---

## 6. 明确不做的事

- 不引入 Rust / 不重写核心，保持 .NET 8 + WPF + MAUI 现状。
- 不复制 LocalSend 代码（Apache-2.0，仅作设计参照）。
- 不在本阶段重做 UI 视觉，只加必要的状态与操作入口。

---

## 7. 实施进度

### 第一批（2026-10-16）：阶段 0（客户端侧）+ 阶段 2 可靠性主体 + 服务端加固

| 项 | 位置 | 说明 |
| --- | --- | --- |
| 续传闭环 | `LocalTransferClient.UploadMissingChunksAsync` | 每轮重新拉 `missingChunks` 续传；预算 30 分钟；耗尽时主动 `DELETE` 清理服务端 `.part` |
| 指数退避重试 | `TransferTuning.GetTransferRetryDelay` | 250ms→750ms→2.25s→…→10s 封顶；`TransferMaxAttempts` 3→5（单请求总窗口约 10s） |
| 断网探测 | `LocalTransferClient.WaitForCoordinatorAsync` | 轮次之间轮询 `/api/v1/health`，网络恢复即继续 |
| 批量失败隔离 | `MainPage.OnSendClicked` | 逐文件 try/catch；未成功的保留在列表里可一键重发；按文件汇总结果 |
| 取消链路 | `MainPage.OnCancelTransferClicked` + 全链路 `CancellationToken` | 新增取消按钮；取消后服务端清理 |
| 源文件暂存 | `UploadStaging`（新） | 不可 seek 的 `content://` 先复制到 `CacheDirectory`，使其可重复打开、可 seek |
| 服务端加固 | `CoordinatorHost.ConfigureKestrel` | 请求体上限 +1 MiB 余量；放宽 `MinRequestBodyDataRate`（100 B/s、30s 宽限）；`KeepAliveTimeout` 3 分钟；`MaxConcurrentConnections` 64 |
| 两端日志 | `CoordinatorLog`（新）+ `MobileDiagnostics` | Windows：`<数据目录>/logs/coordinator.log`，设置页可打开；手机：`GetExternalFilesDir` 下日志，主页可导出 |

### 第二批（2026-10-18）：阶段 1 自动发现

| 项 | 位置 | 说明 |
| --- | --- | --- |
| 公告契约 | `Contracts/Discovery/MulticastAnnouncement.cs` | JSON：`service / protocolVersion / alias / fingerprint / endpoint`；组播组 `224.0.0.167:53318` |
| 电脑侧广播 | `Coordinator/Discovery/MulticastAnnouncer.cs` | 启动爆发 3 次（100ms/500ms/2s），之后每 10 秒一次；TTL=1；按 `AdvertisedAddress` 固定出口网卡（避免公告走 VPN） |
| 手机侧接收 | `Mobile/MulticastDiscovery.cs` | **必须持 `MulticastLock`**，否则 socket 绑定成功但永远收不到；按证书指纹精确匹配才采信 |
| 地址跟随 | `MainPage.RefreshEndpointFromDiscoveryAsync` | 每次页面出现时听 8 秒；指纹匹配且地址确实变了才更新并持久化，**传输进行中不切换** |
| 权限 | `AndroidManifest.xml` | `CHANGE_WIFI_MULTICAST_STATE`（普通权限，无运行时弹窗） |

> 设计取舍：**只让电脑广播、手机收听**。手机不广播，也就不必把自己变成可被发现的服务，
> 安全模型完全不变 —— "发现"不授予任何权限，能连上的唯一凭据仍是配对时存下的那一份。

#### 端口为什么是 53318

LocalSend 的组播端口是 53317。**一台 Android 手机上的两个 App 不能绑同一个 UDP 端口**，
照抄 53317 会让装了 LocalSend 的手机直接绑定失败、自动发现彻底不工作。
组播组仍用 `224.0.0.167`（部分 Android 机型只投递 `224.0.0.0/24` 的组播），HTTP 服务仍是 53317。

### 第三批（2026-10-18）：整批一次批准

| 项 | 位置 | 说明 |
| --- | --- | --- |
| 批量契约 | `Contracts/Transfers/BatchSubmission.cs` | `POST /api/v1/batches` 一次提交 N 个文件的清单；上限 500 |
| 一次决定 | `InboundTransferCoordinator.SubmitBatchAsync` | 注册整批 → 只发一次 `BatchOffered` → **挂起到用户回答**；超时或客户端断开则整批拒绝 |
| 端点 | `CoordinatorHost` | 该请求会挂到有人回答为止（最长 11 分钟），返回 `{ accepted[], rejected[] }` |
| 客户端 | `LocalTransferClient.UploadBatchAsync` | 先算完全部哈希 → 一次提交 → 只传被接收的文件；结果**按索引**返回 |
| 长请求通道 | `PinnedHttpClientFactory.Create(..., timeout)` | 批量请求单独走 12 分钟超时的 HttpClient（默认 2 分钟会中途掐断它） |
| 桌面端 | `BatchApprovalWindow` | 一次弹窗列全部文件；全选 / 全不选 / **单独取消某一个**；10 分钟倒计时 |
| 手机端 | `MainPage.OnSendClicked` | 改为先暂存全部 → 一次提交 → 按索引汇总成功/拒绝/失败 |

> 两个关键设计：
> **① 取消勾选 = 只拒绝那一个文件**，批量的便利不牺牲逐文件选择权。
> **② 整批请求在 HTTP 层是一个"挂起的请求"而不是轮询**，所以客户端必须用长超时的 HttpClient，
> 否则自己的 2 分钟超时会在电脑还在等人点确认时就把它掐断。

### 第四批（2026-10-18）：传输历史 + 每 IP 限流

| 项 | 位置 | 说明 |
| --- | --- | --- |
| 历史存储 | `Coordinator/History/TransferHistoryStore.cs`（新） | JSON Lines 追加写，崩溃只丢最后一行；超 512 KB 自动裁到最近 500 条 |
| 记录时机 | 入站 / 出站协调器 | 挂在状态机的**终态事件**上：完成、失败、拒绝、取消、过期全部自动记录，没有哪条路径会漏 |
| Windows 历史页 | `MainWindow` | 改为读这份日志：**双向**、带设备名与失败原因；不再扫接收目录（以前只看接收方向，文件被移走就从历史消失） |
| 每 IP 限流 | `CoordinatorHost.EnforcePerAddressLimit` | 单 IP 并发请求上限 12，超出 429；回环（桌面自己调自己）不限 |

### 第五批（2026-10-18 晚）：真机日志驱动的修复 + 桌面 UI 收尾

真机首次验证的结果：**批量确认成功**（日志 `accepted 1 of 1`），但文件上传抛
`Java.Lang.RuntimeException`。

| 项 | 位置 | 说明 |
| --- | --- | --- |
| 根因（第一层） | `MainPage` 调用处 | **在 UI 主线程上等异步传输**。MAUI 的 `await` 会切回主线程，于是 `using` 结束时 `HttpResponseMessage.Dispose()` 在主线程丢弃未读完的 chunked body → Android StrictMode 抛 `NetworkOnMainThreadException`。症状被包装成 `Java.Lang.RuntimeException: Exception_WasThrown`，**只有完整堆栈才看得出真身**。 |
| 修复 | `MainPage` 发送 / 接收 | 用 `Task.Run` 把整条传输流程放到线程池执行；`Progress<T>` 仍在主线程创建，进度回调照常回到 UI。 |
| 加固（保留） | `UploadStaging` | 顺手改成「总是复制到缓存 + 复制时算 SHA-256」：既避免 `content://` 被重复打开，也少读一遍源。复制失败（缓存分区满）回退到原流并记日志。 |
| 日志 | `LocalTransferClient` | 失败时记录**完整异常（含堆栈）**，不再只记 `Message` —— 之前那条 Java 异常没有任何可用信息。 |
| 容错 | `IsTransientFailure` | Java 层异常视为"本次尝试失败"，让恢复循环重开源，而不是第一次就判死。 |
| 防死循环 | `MaxStalledRecoveryRounds = 3` | 连续 3 轮一个字节都没传出去就放弃（源确实不可用），而不是空耗 30 分钟预算。 |

**桌面 UI 修复**（用户反馈界面有问题）：

| 问题 | 修复 |
| --- | --- |
| 列表右侧的大小 / 状态徽标紧贴文件名，不靠右 | 5 个 `ListBox` 补 `HorizontalContentAlignment="Stretch"` |
| 历史页把方向 / 设备 / 结果挤在一行 | 结果改为**彩色徽标**（完成绿 / 失败红 / 未接收黄），错误信息移到行悬停提示 |
| 传输任务页文案过时：「发送队列只保存在内存里」 | 改为「发送队列会保存到本地，重启后自动恢复」（队列早已持久化） |
| 设置页长路径溢出 | 三处路径补 `TextTrimming="CharacterEllipsis"` |
| 批量确认窗口按钮用裸色，悬停时白字不可读 | 改用项目统一的 `PrimaryButton` / `GhostButton` |

### 验证

- 编译：Windows / Coordinator / Mobile(Android) 全部 **0 警告 0 错误**。
- 测试：**65 个全通过**（Core 12 / Protocol 15 / Integration 38）。
  - 批量批准用例 4 个：请求挂起、部分接受、非属主拒绝、超限拒绝
  - 历史用例 4 个：读取顺序、断行容错、拒绝记录、取消记录
  - **批量端到端用例**：真实 HTTPS 下 3 个文件只触发 **1 次**确认、全部落地、历史记 3 条完成
- **组播实测**：本机启动 announcer 并加入组播组监听，实际收到 183 字节公告，内容为
  `{"service":"LocalTransfer","protocolVersion":1,"alias":"CHECK","fingerprint":"AAAA…","endpoint":"https://192.168.1.5:53317"}`。
- 产物：`dist/win-x64/`（Release 自包含）与 `dist/局域传输-1.0-arm64.apk`（Release + AOT，含 `.idsig`）。
  调试期的中间 APK（`bin/**/*.apk`）已在 2026-10-19 的清理中删除，需要时重新编译。

### 仍可做的事（都不影响当前使用）

- **手机端历史回看**：每次发送/接收的结果已在完成弹窗里即时给出（成功 N 个 / 逐条失败原因），跨次回看未做。
- **未配对设备的免扫码配对**：自动发现目前只在已配对设备间生效，首次配对仍需扫码。
  要支持它需要在服务端加一个"发现后请求配对"的票据端点，属于独立的一块。
- **暂停 / 继续**：失败与断网已由续传闭环覆盖（自动重试 + 断点续传）；
  "暂停"需要协议层新增"停止但不清理 checkpoint"的语义。
- **并发分块上传**：客户端仍串行。4 MiB 分块在局域网下已接近链路带宽，并发主要帮助高延迟链路，收益有限。
- **`_transfers` 状态落盘**：电脑重启后手机无法继续同一次传输（需重新发起）。

### 结束判断

最初的三个诉求已经闭环：

| 诉求 | 状态 |
| --- | --- |
| 传输中断 / 失败 | 续传闭环 + 指数退避 + 断网探测 + 批量失败隔离 + 取消 + 源文件暂存 |
| 自动发现设备 | UDP 组播公告 + 指纹匹配 + 地址自动跟随（换网络 / 换 IP 免扫码） |
| 历史与任务管理 | 双向历史 + 每 IP 限流；逐文件重试在 UI 上就是"重新发送"，未成功的项会留在列表里 |

剩下那几项属于新功能而非缺陷修复，可按需再决定。

> ⚠️ 打包注意：Debug APK 必须显式 `-t:SignAndroidPackage`，**并且先删掉 obj 里的中间包**
> （`<Temp>/LocalTransferBuild/<项目>/obj/.../android/bin/com.localtransfer.mobile.apk`）。
> 只删 `bin` 下的 APK 不够 —— MSBuild 会复用中间包，新代码根本进不去，
> 表现是 **APK 大小与上一次完全相同**。托管程序集嵌在 `lib/<abi>/libxamarin-app.so`，
> 打包后要用它的时间戳验证，而不是只看 APK 的 mtime。
