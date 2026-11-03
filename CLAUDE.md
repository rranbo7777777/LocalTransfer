# CLAUDE.md — LocalTransfer 项目约定

局域网双向文件传输工具：Windows WPF 协调端 + MAUI 手机端（Android 已可用，iOS 待构建）。文件不过云端，手机始终主动连接电脑。整体功能与限制以 [README](README.md) 为准，协议细节见 [docs/protocol-v1.md](docs/protocol-v1.md)。

## 构建与测试

- **不要直接编译 `LocalTransfer.sln`**：本机缺 `net8.0-ios` 目标包，Mobile 的 iOS 目标会让整个解决方案报 `NETSDK1135`。按需逐项目构建：
  ```bash
  dotnet restore src/LocalTransfer.Mobile/LocalTransfer.Mobile.csproj
  dotnet build src/LocalTransfer.Windows/LocalTransfer.Windows.csproj --no-restore
  dotnet build src/LocalTransfer.Mobile/LocalTransfer.Mobile.csproj -f net8.0-android --no-restore
  dotnet test tests/LocalTransfer.Core.Tests/LocalTransfer.Core.Tests.csproj --no-restore
  dotnet test tests/LocalTransfer.Protocol.Tests/LocalTransfer.Protocol.Tests.csproj --no-restore
  dotnet test tests/LocalTransfer.IntegrationTests/LocalTransfer.IntegrationTests.csproj --no-restore
  ```
- **Agent shell 里编译/测试有两个坑**：① PATH 上的 `dotnet` 可能是 AutoClaw 自带运行时（缺 SDK，只会报 "Download a .NET SDK"），需用完整路径 `C:\Program Files\dotnet\dotnet.exe`；② shell 缺 `APPDATA` / `LOCALAPPDATA`，任何触发 restore 的构建/测试都会报 `Value cannot be null. (Parameter 'path1')`，需先 `$env:APPDATA="$env:USERPROFILE\AppData\Roaming"; $env:LOCALAPPDATA="$env:USERPROFILE\AppData\Local"` 再调 dotnet。正常用户终端不受影响。
- MAUI 项目的 `obj` 被重定向到 `%TEMP%/LocalTransferBuild`（含中文的工作区路径下 Android 原生工具不稳定），`CheckEolWorkloads=false` 也在同一文件 `Directory.Build.props`。
- 改完必须验证：动了哪个项目就跑哪套测试；改协议/协调端跑三套全套。测试数会随回归用例增长，以实际输出为准。

## 结构

- `src/LocalTransfer.Contracts` — 共享协议：清单、调参（TransferTuning）、组播报文、批量提交。**改协议先改 [docs/protocol-v1.md](docs/protocol-v1.md) 再改代码。**
- `src/LocalTransfer.Core` — 状态机、分块计划、文件名净化
- `src/LocalTransfer.Client` — 手机侧客户端库（续传闭环、批量上传、证书固定）；`tools/PhoneSim` 也复用它
- `src/LocalTransfer.Coordinator` — ASP.NET Core 协调端（端点、配对、入站/出站、历史、组播公告）
- `src/LocalTransfer.Infrastructure` — 证书、断点文件接收、检查点存储
- `src/LocalTransfer.Windows` — WPF 桌面端（托盘常驻）
- `src/LocalTransfer.Mobile` — MAUI 手机端（android/ios 目标）
- `tools/` — PhoneSim（无真机联调模拟器）、ui-check（桌面 UIA 自检库）、collect-phone-log.bat（抓手机日志）、icon-source（图标生成脚本）

## 约定

- 目标框架 `net8.0` 是本机无 .NET 10 SDK 的回退决定；MAUI 8 已 EOL，靠 `CheckEolWorkloads=false` 维持构建，升级需单独评估，不要顺手改。
- 网络与传输逻辑必须考虑超时、重试、异常恢复路径；对外 HTTP 端点要有限流与输入净化意识（现状基线见 README「服务端防护」，新增端点不得低于该基线）。
- 密钥不进代码不进 commit：`signing/`（含 keystore 与密码说明）、`*.keystore`、`*.jks` 已 gitignore；`dist/`、`自检截图/`、`.zwork/` 等本地产物不入库。
- 图标与品牌：两端图标同源（蓝 #376BEA 渐变底 + 白描手机/笔记本双向箭头），改图标从 `tools/icon-source/make_icon.py` 改起再重新生成，不要直接手改 `App.ico`。
- 文档与实现对齐是纪律：改了行为同步 README 对应小节；阶段性结论写 `docs/`（自检报告、roadmap）。
- 提交信息用中文，`feat:/fix:/docs:/ci:` 前缀，沿用现有历史风格。
