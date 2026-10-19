# LocalTransfer 协议 v1

## 连接与身份

- Windows 是 HTTPS 服务端和协调端，默认端口 `53317`，手机发起所有 TCP/TLS 连接。
- 配对二维码包含 HTTPS 地址、协议版本、证书 SHA-256 指纹、一次性票据和过期时间。
- 手机必须先按二维码固定证书指纹，再提交一次性票据；Windows 用户批准后返回只展示一次的设备凭据。
- 配对提交的设备名不超过 64 字符且不含控制字符，`platform` 不超过 32 字符，超限提交被拒绝。
- 后续接口必须携带：
  - `X-LocalTransfer-Device: {device-guid}`
  - `Authorization: Bearer {device-credential}`
- 服务端不保存凭据明文，只保存 SHA-256；比较使用固定时间比较。

## 文件清单

`FileManifest` 包含：

- `transferId`
- 单层 `fileName`，禁止路径分隔符
- `length`
- `lastModifiedUtc`
- `chunkSize`，当前默认 4 MiB；协议限制 `chunkSize ≤ 4 MiB`、`length ≤ 1 TiB`、分块数 ≤ 262,144，超限清单在提交时被拒绝
- 整文件 `sha256Hex`

每个分块还携带独立的 `X-Chunk-SHA256`。接收端只有在全部分块和整文件哈希验证成功后才把 `.part` 文件原子移动为最终文件；同名文件自动增加序号，不覆盖已有文件。

## 主要接口

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/api/v1/health` | 服务与协议版本检查（同时充当续传前的连通性探测） |
| `POST` | `/api/v1/pairing` | 提交一次性配对票据 |
| `GET` | `/api/v1/pairing/{requestId}` | 轮询电脑批准结果 |
| `POST` | `/api/v1/transfers` | 手机提交**单个**上传文件清单 |
| `POST` | `/api/v1/batches` | 手机提交**整批**上传清单，一次确认（见下节） |
| `GET` | `/api/v1/transfers/{transferId}` | 查询批准状态和缺失分块 |
| `PUT` | `/api/v1/transfers/{transferId}/chunks/{index}` | 手机上传一个分块 |
| `POST` | `/api/v1/transfers/{transferId}/complete` | 请求整文件校验和落盘 |
| `DELETE` | `/api/v1/transfers/{transferId}` | 取消手机上传 |
| `GET` | `/api/v1/outbound` | 手机查询电脑为本设备排队的文件 |
| `GET` | `/api/v1/outbound/{transferId}` | 查询单个出站任务状态 |
| `GET` | `/api/v1/outbound/{transferId}/chunks/{index}` | 手机下载一个分块 |
| `POST` | `/api/v1/outbound/{transferId}/complete` | 手机确认校验和保存完成 |
| `DELETE` | `/api/v1/outbound/{transferId}` | 取消电脑到手机任务 |

完成接口是幂等的：响应丢失后重复提交不会生成重复文件，也不会把已完成任务误判为失败。

## 整批批准（`POST /api/v1/batches`）

单个 `POST /api/v1/transfers` 需要人工确认一次，批量场景下不成立。批量入口把 N 次确认收敛成一次：

```jsonc
// 请求
{ "batchId": "guid", "files": [ FileManifest, ... ] }

// 响应（HTTP 200）
{ "batchId": "guid", "accepted": ["transferId", ...], "rejected": ["transferId", ...] }
```

**这是一个挂起请求，不是轮询接口。** 服务端收到后立即把整批放进待批准队列，
然后一直挂到电脑前的人点下按钮才返回（最长 `OfferLifetime` = 11 分钟）。

因此客户端有三条硬性要求：

1. **必须使用长超时 `HttpClient`**（`TransferTuning.BatchApprovalTimeout` = 12 分钟）。沿用默认 2 分钟的超时会在
   电脑还在等人确认时先把请求掐断。
2. **结果按索引对齐**：响应里的 `accepted` / `rejected` 是 `transferId` 列表，客户端把它们映射回自己提交的
   文件列表（`BatchUploadResult.CompletedIndexes` / `RejectedIndexes` / `Failed[Index]`），不需要额外的映射表。
3. **被拒绝的文件不要重投**：同一批里被人工取消勾选的文件视为最终拒绝，重投只会再问一遍同样的问题；
   客户端应当把它汇报给用户。

约束与语义：

- 单批最多 `BatchLimits.MaxFiles = 500` 个文件，超出返回 `400`。
- 整批只产生**一次**桌面弹窗；桌面取消勾选某个文件 = 只拒绝那个 `transferId`，其余照常进行。
- 批量提交**不占用** `MaxPendingOffersPerDevice = 10` 的名额——那个上限是防弹窗风暴的，一批只算一个提示。
- 一致性问题：超时清扫与用户点击可能同时发生，`PendingBatch` 用 `Interlocked.Exchange` 保证两者只有一个能驱动状态。
- 若需要逐个确认，仍可对单文件使用 `POST /api/v1/transfers`。

## 自动发现

电脑端周期性向 UDP 组播组公告自己；手机只收听、不广播。

| 项 | 值 |
| --- | --- |
| 组播组 | `224.0.0.167`（刻意留在 `224.0.0.0/24` 内，部分 Android 机型只投递该段） |
| 端口 | **`53318`**（HTTP 仍为 53317） |
| 周期 | 每 10 秒一次；启动时额外在 100 ms、500 ms、2 s 连发三次 |
| TTL | 1（不跨路由器） |
| 报文 | JSON，≤ 4096 字节 |

```jsonc
{ "service": "LocalTransfer", "protocolVersion": 1, "alias": "DESKTOP-XXXX",
  "fingerprint": "sha256-hex", "endpoint": "https://192.168.1.10:53317" }
```

- `service` 必须是 `LocalTransfer`，其余客户端（含同组的 LocalSend）发出的报文按此字段忽略。
- 端口选择 53318 而非 53317 的原因是：53317 是 LocalSend 的组播端口，同一台 Android 上两个 App 不能绑定同一个 UDP 端口。
- **公告不含任何秘密**：`fingerprint` 是证书指纹（不是凭据），公告也不含票据。因此发现**不授予任何权限**——
  能连上的唯一前提仍是配对时存下的凭据。
- 电脑端把组播出口网卡固定为 `AdvertisedAddress` 所在网卡，否则系统可能选到 VPN 隧道。
- 手机端必须持 `WifiManager.MulticastLock`（需 `CHANGE_WIFI_MULTICAST_STATE`，普通权限、无运行时弹窗），
  否则 socket 绑定成功但永远收不到组播。
- 手机端每次监听 8 秒，**指纹命中已配对设备且地址确实变化**时才更新并持久化；传输进行中不切换。

## 服务端防护

- 配对端点按来源 IP 做固定窗口限流，**提交与轮询各自独立计数**：提交票据 30 次/分钟，状态轮询 240 次/分钟，超限返回 `429`。
- 手机提交票据后按 `PairingPollInterval`（当前 2 秒）轮询批准结果。轮询配额必须显著高于该频率（60 / 2 秒 = 30 次/分钟），否则电脑端用户回答批准稍慢，客户端就会把自己的配额耗尽并中断配对。两个端点曾共用同一份 30 次/分钟的配额，实测导致配对在约 22 秒后必然失败并返回 `429`。
- **每 IP 并发上限 12 个请求**，超出直接 `429`（不排队）；回环地址（电脑端 UI 自己）不受限。Kestrel 全局并发连接上限 64。
- 每台设备的待批准入站邀约最多 10 个，超出返回 `409`（整批提交按 1 个计）。
- **未答复的入站邀约在 11 分钟后自动作废（转为 `Rejected`）。** 该有效期必须长于客户端自己的批准等待（10 分钟），否则仍在轮询的手机会看到邀约凭空消失。缺少这条规则时，被忽略的确认提示会永久占用那 10 个名额：攒满之后该设备的所有发送都返回 `409`，只能靠重启电脑端恢复。
- **过期后不得批准。** `ApproveAsync` 自己检查有效期，不依赖清扫时机；否则"确认提示还开着时点「是」"会走到过期之后才执行，把一次已经作废的传输变成 `Queued`。
- 配对请求（含被拒绝的）保留 15 分钟后清理；传输进入终态后保留 10 分钟供对端查询最终状态，随后清扫。
- **批准只在票据有效期内有效。** 客户端随 bootstrap 过期即停止轮询，所以票据过期后再批准必须被拒绝（不写可信设备表、也不出现在待批准列表里）。否则会出现"电脑已信任、手机未持有"的永久不一致：该手机之后每个请求都返回 `401`，且只能靠重新配对恢复——实测就是这样产生了一个只能靠重装 App 才能解释的 401。
- `X-Chunk-SHA256` 必须是 64 个十六进制字符，否则分块下载被拒绝。
- 请求体上限 = `MaxChunkSize + 1 MiB`（一个分块加余量）；最小数据速率放宽到 100 B/s / 30 秒宽限，空闲连接保留 3 分钟，
  以免后台运行或弱网下的手机被"速率启发式"或"空闲超时"误杀。
- 状态与错误响应不暴露服务器本地文件系统路径。

## 状态机

主要状态为：`WaitingForApproval → Queued → Transferring → Verifying → Completed`。

旁路状态包括 `Paused`、`WaitingForConnection`、`Failed`、`Rejected` 和 `Canceled`。服务端只允许显式定义的转换，终态不能继续写入数据。

进入任一终态（`Completed` / `Failed` / `Rejected` / `Canceled`）时，两端都会向 `history.jsonl` 追加一条记录；
记录点统一挂在状态机的事件上，而不是分散在各个出口。

## 恢复语义

- 接收端在目标目录的 `.localtransfer` 子目录保存 `{transferId}.part` 和 JSON 检查点。
- 成功写入并刷新一个分块后，才原子更新检查点。
- 重连后发送方查询 `missingChunks`，只重传缺失分块。
- 手机完成下载但完成响应丢失时，会保留本地完成回执；下次只重试确认，不重复下载。

### 客户端续传闭环

单个请求失败不再等于整个传输失败：

1. 一次分块上传在 `TransferMaxAttempts = 5` 次内按指数退避重试（250 ms × 3ⁿ，封顶 10 s）。
2. 仍失败则进入**恢复轮次**：重新 `GET /api/v1/transfers/{id}` 拉取 `missingChunks`，接着从缺的那一块继续。
3. 每轮之间探 `GET /api/v1/health`（5 秒超时）判断网络是否回来；预算 `TransferRecoveryBudget = 30 分钟`，
   服务端的检查点在整个窗口内都保留。
4. 连续 `MaxStalledRecoveryRounds = 3` 轮**零字节进展**即放弃——这通常意味着源本身不可用（`content://` 授权被回收、
   文件被删除），再等半小时也只会得到同一个错误。
5. 超过预算或用户取消时，客户端主动 `DELETE /api/v1/transfers/{id}` 清理服务端残留。
