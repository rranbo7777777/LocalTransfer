# 局域传输（LocalTransfer）

面向 Windows、Android 和 iPhone 的局域网双向文件传输工具。文件不经过云端；手机始终主动连接 Windows 协调端，适用于同一 Wi-Fi 或手机热点。

当前代码以 `net8.0` 为目标框架。本机没有 .NET 10 SDK，因此按需求回退到 .NET 8；`global.json` 使用已安装的 .NET 9.0.312 SDK 作为编译器，同时引用 .NET 8 目标包。

## 已实现

### 桌面端与配对

- Windows WPF 主程序、托盘常驻、文件拖放和多选；关闭窗口只收进托盘，退出请用托盘菜单。
- 左侧导航：`发送文件`、`设备`（查看并移除已配对设备）、`传输任务`（进行中的收发队列，2 秒自动刷新，传输/校验行显示实时百分比）、`历史记录`（**双向**终态记录）、`设置`（服务地址、目录、证书指纹、日志位置）。
- Windows 在局域网监听 HTTPS（默认 53317），自动选择可用 IPv4 地址；Kestrel 的请求体上限、最小数据速率、空闲连接与并发连接数均已按"手机端在弱网下慢慢传"的场景放宽。
- 二维码首次配对、一次性票据（2 分钟）、电脑端人工批准。
- 自签名 ECDSA 证书 SHA-256 固定，手机凭据存入系统 `SecureStorage`，电脑只保存凭据哈希。

### 手机 → 电脑

- **整批一次批准**：一次选 N 个文件只弹一个确认窗口（单批上限 500 个），可全选/全不选，也可取消勾选只拒绝其中某几个。
- 4 MiB 分块上传，分块与整文件双重 SHA-256，`.part` 临时文件校验后原子落盘，重名不覆盖。
- **真正的断点续传**：任一分块失败后不会丢弃已传内容，而是重新查询 `missingChunks` 接着传；网络中断时每轮先探 `/api/v1/health`，恢复后自动续上，总预算 30 分钟。
- **指数退避重试**：250 ms → 750 ms → 2.25 s → … 封顶 10 s，最多 5 次；短暂断网不再直接判死。
- **单个文件失败不拖垮整批**：失败项留在列表里可单独重试，并逐条显示原因。
- 传输过程中可随时取消，取消会向电脑端发出清理请求。

### 电脑 → 手机

- 电脑排队，手机主动查询和下载；支持断点、哈希校验和完成回执重试。
- 入队时按 1 MiB 块计算整文件 SHA-256 并在发送按钮上显示百分比（大文件不再"假死"）；发送侧以已请求分块数估算传输百分比。
- 发送队列持久化：队列写入数据目录 `outbound-queue.json`，重启后自动恢复；恢复前校验源文件存在且大小/修改时间与清单一致，不满足的条目静默丢弃，损坏的队列文件不会阻止启动。

### 自动发现（免扫码换网络）

- 电脑端周期性 UDP 组播公告自己（组 `224.0.0.167:53318`，10 秒一次，启动时 100 ms / 500 ms / 2 s 连发三次），公告只含服务名、协议版本、机器名、证书指纹和 HTTPS 地址。
- 手机进前台时监听 8 秒，**只有指纹命中已配对的那台电脑、且地址确实变了**才更新并持久化；传输进行中不切换。
- 发现不等于授权：公告不含任何票据或凭据，能连上的前提仍是配对时存下的那份凭据，**首次配对依旧需要扫码**。
- 组播端口刻意不用 53317（LocalSend 的组播端口，同一台 Android 上两个 App 无法绑定同一 UDP 端口），HTTP 服务仍在 53317。

### 历史与诊断

- `TransferHistoryStore`：数据目录 `history.jsonl`（JSON Lines 追加写，崩溃最多丢最后一行；超 512 KB 自动裁到最近 500 条；读取跳过残缺末行）。入站与出站都记，含设备、方向、大小、结果与失败原因。
- 记录点在 `TransferStateMachine.StateChanged` 的终态分支统一订阅，新增任何结束路径都不会漏记。
- 桌面日志 `logs/coordinator.log`（4 MiB 轮转，设置页可一键打开）；手机端 `MobileDiagnostics` 记录异常与续传轮次，主页可**导出/分享**给开发者。诊断记录的是 `exception.ToString()`（含堆栈），不是光秃秃的 `Message`。

### 服务端防护

- 配对端点按来源 IP 限流，**提交与轮询各自独立计数**（提交票据 30 次/分钟；状态轮询 240 次/分钟，必须显著高于手机 2 秒一次的轮询频率，否则用户回答批准稍慢就会触发 429 中断配对）。
- **批准只在配对票据有效期内受理**（票据过期后一律拒绝，否则电脑会写入手机永远拿不到的凭据，使该设备后续所有请求都返回 401 且只能重新配对恢复）。
- 每设备待批准邀约上限 10 个（超出 409），**未答复的入站邀约 11 分钟后自动作废**（长于客户端 10 分钟的批准等待）；过期后 `ApproveAsync` 自行拒绝，不依赖清扫时机。被忽略的提示必须能自动释放名额，否则攒满 10 个后该设备再也发不出文件。
- **每 IP 并发限制 12 个请求**，超出 429，回环地址不限（电脑端 UI 自己走回环）；Kestrel 全局并发 64。
- 输入与文件名净化：设备名限 64 字符且禁控制字符，文件名过滤 Unicode 双向字符与 `CON`/`NUL` 等保留名，断点检查点防目录穿越，JSON 响应限 4 MiB，错误信息脱敏。

### 测试

- 协议、核心逻辑、真实 HTTPS 双向流程与整批批准端到端共 **65 个自动化测试**，含续传退避时序、组播报文格式、批量批准一致性、历史日志的崩溃容错。

## 快速开始

1. 用 Visual Studio 2022 打开 `LocalTransfer.sln`。
2. 将 `LocalTransfer.Windows` 设为启动项目并运行。Windows 防火墙询问时，只允许"专用网络"。
3. 点击"创建配对信息"，在 Android 客户端点击"配对"并扫描二维码（扫码不可用时可复制配对信息再粘贴）。
4. 在电脑确认手机名称和设备 ID。
5. 手机可从文件选择器选文件，或在微信/QQ 中使用"用其他应用打开/分享"选择"局域传输"；电脑**一次确认整批**后开始上传。
6. 电脑选择可信手机和文件后点击"发送"，手机点击"接收电脑文件"主动下载。

配对完成后，只要在同一 Wi-Fi 下，电脑换 IP 或手机换网络都**不需要重新扫码**：手机进前台会自动按证书指纹找回电脑。

### 调试 APK

`src/LocalTransfer.Mobile/bin/Debug/net8.0-android/com.localtransfer.mobile-Signed.apk`

⚠️ Debug 包**必须显式 `-t:SignAndroidPackage`**，并且先删掉 `obj` 里的中间包，否则 MSBuild 会复用上一次的
`com.localtransfer.mobile.apk`，新代码根本进不了包——症状是**新包与旧包大小完全一致**。托管程序集嵌在
`lib/<abi>/libxamarin-app.so` 里，用它的时间戳判断是否真的重新打包，别只看 APK 的修改时间。

## 常用命令

所有命令建议在 Git Bash 中执行。**不要直接编译 `LocalTransfer.sln`**：本机缺少 `net8.0-ios` 目标包，Mobile 项目的 iOS 目标会让整个解决方案报 `NETSDK1135`（`SupportedOSPlatformVersion 16.0 不能高于 TargetPlatformVersion 1.0`）；请按下面的命令逐个编译需要的项目。iOS 由 CI 用 `net9.0-ios` 打包，见文末。

```bash
dotnet restore src/LocalTransfer.Mobile/LocalTransfer.Mobile.csproj
dotnet build src/LocalTransfer.Windows/LocalTransfer.Windows.csproj --no-restore
dotnet build src/LocalTransfer.Mobile/LocalTransfer.Mobile.csproj -f net8.0-android --no-restore
dotnet test tests/LocalTransfer.Core.Tests/LocalTransfer.Core.Tests.csproj --no-restore
dotnet test tests/LocalTransfer.Protocol.Tests/LocalTransfer.Protocol.Tests.csproj --no-restore
dotnet test tests/LocalTransfer.IntegrationTests/LocalTransfer.IntegrationTests.csproj --no-restore
```

Android 原生工具在含中文的工作区路径下无法稳定读取中间资源，因此 `Directory.Build.props` 只把 MAUI 项目的 `obj` 重定向到系统临时目录中的纯英文路径（`%TEMP%/LocalTransferBuild`）；源码和最终 APK 仍在工作区内。MAUI 8 的 EOL workload 检查（`CheckEolWorkloads=false`）也在同一文件中全局关闭，命令无需再显式传参。

首次 Release 发布后若报 `NETSDK1047`（assets 文件缺 RID 目标），先跑一次 `dotnet restore`。

### 发布 Windows 端

自包含发布（免装 .NET），把输出目录整体替换掉 `dist/win-x64`：

```bash
dotnet publish src/LocalTransfer.Windows/LocalTransfer.Windows.csproj -c Release \
  -r win-x64 --self-contained true -o /c/Users/$USER/AppData/Local/Temp/lt-win-stage
# 确认发布成功后再换入 dist/win-x64（直接 -o 到已有目录会残留上次的旧文件）
```

换入前必须**真正退出**程序（托盘右键 → 退出），否则 `mv` 报 `Device or resource busy`；退不掉时 `taskkill //PID <pid> //F`。

### 发布 Release APK

发布命令需用单数 `RuntimeIdentifier`（复数会触发引用项目的 MSB3030），且本机的 MSBuild 签名任务不会真正写入签名，发布后需用 build-tools 手动 zipalign + apksigner 签名（密钥库见 `signing/`，密码见 `signing/keystore-info.txt`）：

`AndroidSigningKeyStore` **必须给绝对路径**：MSBuild 以**项目目录**（`src/LocalTransfer.Mobile/`）为基准解析相对路径，写 `signing/localtransfer.keystore` 会直接报 `XA4310: 找不到 '$(AndroidSigningKeyStore)' 文件`。

```bash
ROOT="$(cygpath -w "$PWD")"   # 在仓库根目录执行；Git Bash 的 /d/... 路径 MSBuild 无法识别
dotnet publish src/LocalTransfer.Mobile/LocalTransfer.Mobile.csproj -f net8.0-android -c Release \
  -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$ROOT/signing/localtransfer.keystore" \
  -p:AndroidSigningKeyAlias=localtransfer -p:AndroidSigningStorePass=<密码> -p:AndroidSigningKeyPass=<密码> \
  -p:AndroidPackageFormat=apk -p:RuntimeIdentifier=android-arm64

BT="C:/Program Files (x86)/Android/android-sdk/build-tools/35.0.0"
P=src/LocalTransfer.Mobile/bin/Release/net8.0-android/android-arm64/publish
"$BT/zipalign.exe" -f 4 "$P/com.localtransfer.mobile.apk" "$P/aligned.apk"
java -jar "$BT/lib/apksigner.jar" sign \
  --ks signing/localtransfer.keystore --ks-key-alias localtransfer \
  --ks-pass pass:<密码> --key-pass pass:<密码> \
  --out dist/局域传输-1.0-arm64.apk "$P/aligned.apk"
```

签名顺序不能颠倒：**先 zipalign 再 apksigner**，且必须对未签名的 `com.localtransfer.mobile.apk` 做，不要用 `-Signed.apk`（对齐会破坏已有签名）。build-tools 35 的 apksigner 默认写出 v4 签名，因此会一并生成 `dist/局域传输-1.0-arm64.apk.idsig`，两个文件都要一并分发。

APK 的 `minSdkVersion` 是 29（Android 10），只写 v3 签名即可安装（v2 仅 API 24–27 必需、v1 仅 API 24 以下必需），所以 `apksigner verify` 显示 `v2 scheme: false` 属正常现象。若以后下调 `SupportedOSPlatformVersion` 到 24–27，需显式加 `--v2-signing-enabled true`。

## 当前限制

- **尚未在真机完成端到端验收**：10 GB 单文件、100 文件批量、断网恢复三项需求基线未跑；微信/QQ 分享入口只在代码层验证。
- iOS 主应用代码和权限声明已准备，但本机只有 .NET 9 iOS 目标包，缺少 `net8.0-ios` 目标包，也没有配对 Mac，因此尚未完成 iOS 构建与真机验证。
- iOS Share Extension 尚未创建；目前 iPhone 路径是应用内文件选择器。
- 手机端**没有历史记录页**：历史只存在于电脑端 `history.jsonl`。
- 首次配对仍需扫码，尚未实现"零扫码"配对（自动发现只解决换网络/换 IP 的问题）。
- 未实现：暂停/恢复、并发分块上传、`_transfers` 状态持久化（电脑端重启会丢失未完成的入站任务）、Windows 安装器、自动配置防火墙规则、多网卡手动选择。
- MAUI 8 已结束官方支持；这是遵循本次指定的 .NET 8 回退目标，已在 `Directory.Build.props` 中设置 `CheckEolWorkloads=false` 维持本地构建，后续应单独评估升级。

## 目录结构

```
src/LocalTransfer.Contracts/      共享协议：清单、调参（TransferTuning）、组播报文、批量提交
src/LocalTransfer.Core/           状态机、分块计划、命名与净化策略
src/LocalTransfer.Client/         手机侧客户端库（续传闭环、批量上传、证书固定）
src/LocalTransfer.Coordinator/    ASP.NET Core 协调端：端点、配对、入站/出站、历史、组播公告
src/LocalTransfer.Infrastructure/ 证书、存储等基础设施
src/LocalTransfer.Windows/        WPF 桌面端
src/LocalTransfer.Mobile/         MAUI 手机端
docs/                             协议、路线、优化记录、安装说明
tools/                            诊断脚本（抓手机日志）
```

## iOS 打包

iOS 的原生链接与 .app 组装要求 `IsMacEnabled=true`（即真实的 Mac 环境），Windows 本机只能编译托管程序集、无法产出 ipa。仓库提供 GitHub Actions 工作流 `.github/workflows/ios-package.yml`（macOS runner、手动触发，引用的 Actions 均固定到 commit SHA），产出未签名 ipa，再用 Sideloadly/AltStore 以个人 Apple ID 签名安装。本机需临时把 Mobile 项目切到 `net9.0-ios`（本机只装有 .NET 9 iOS 目标包），工作流中已包含同样处理。

协议、路线与面向使用者的说明分别见 [协议说明](docs/protocol-v1.md)、[实施路线](docs/roadmap.md)、[安装与故障排查](docs/安装说明.md)。
