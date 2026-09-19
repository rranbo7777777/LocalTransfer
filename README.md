# 局域传输（LocalTransfer）

面向 Windows、Android 和 iPhone 的局域网双向文件传输工具。文件不经过云端；手机始终主动连接 Windows 协调端，适用于同一 Wi-Fi 或手机热点。

当前代码以 `net8.0` 为目标框架。本机没有 .NET 10 SDK，因此按需求回退到 .NET 8；`global.json` 使用已安装的 .NET 9.0.312 SDK 作为编译器，同时引用 .NET 8 目标包。

## 已实现

- Windows WPF 主程序、托盘常驻、文件拖放和多选。
- Windows 在局域网监听 HTTPS，自动选择可用 IPv4 地址。
- 二维码首次配对、一次性票据、电脑端人工批准。
- 自签名 ECDSA 证书 SHA-256 固定，手机凭据存入系统 `SecureStorage`，电脑只保存凭据哈希。
- 手机到电脑：逐文件批准、4 MiB 分块、断点续传、分块及整文件 SHA-256、`.part` 临时文件、校验后原子落盘、重名不覆盖。
- 电脑到手机：电脑排队，手机主动查询和下载；支持断点、哈希校验和完成回执重试。
- Android 文件选择器，以及微信/QQ 等应用的 `ACTION_SEND` / `ACTION_SEND_MULTIPLE` 系统分享入口。
- Android/iOS 二维码扫描页面；扫码不可用时可粘贴配对 JSON。
- 接收到手机应用目录后，可打开系统分享/保存菜单。
- 协议、核心逻辑和真实 HTTPS 双向流程的自动化测试。

## 快速开始

1. 用 Visual Studio 2022 打开 `LocalTransfer.sln`。
2. 将 `LocalTransfer.Windows` 设为启动项目并运行。Windows 防火墙询问时，只允许“专用网络”。
3. 点击“创建配对信息”，在 Android 客户端点击“配对”并扫描二维码。
4. 在电脑确认手机名称和设备 ID。
5. 手机可从文件选择器选文件，或在微信/QQ 中使用“用其他应用打开/分享”选择“局域传输”；电脑逐文件确认后开始上传。
6. 电脑选择可信手机和文件后点击“发送”，手机点击“接收电脑文件”主动下载。

调试 APK：

`src/LocalTransfer.Mobile/bin/Debug/net8.0-android/com.localtransfer.mobile-Signed.apk`

## 常用命令

所有命令建议在 Git Bash 中执行：

```bash
dotnet restore src/LocalTransfer.Mobile/LocalTransfer.Mobile.csproj -p:CheckEolWorkloads=false
dotnet build src/LocalTransfer.Windows/LocalTransfer.Windows.csproj --no-restore
dotnet build src/LocalTransfer.Mobile/LocalTransfer.Mobile.csproj -f net8.0-android --no-restore -p:CheckEolWorkloads=false
dotnet test tests/LocalTransfer.Core.Tests/LocalTransfer.Core.Tests.csproj --no-restore
dotnet test tests/LocalTransfer.Protocol.Tests/LocalTransfer.Protocol.Tests.csproj --no-restore
dotnet test tests/LocalTransfer.IntegrationTests/LocalTransfer.IntegrationTests.csproj --no-restore
```

Android 原生工具在含中文的工作区路径下无法稳定读取中间资源，因此 `Directory.Build.props` 只把 MAUI 项目的 `obj` 重定向到系统临时目录中的纯英文路径；源码和最终 APK 仍在工作区内。

### 发布 Release APK

发布命令需用单数 `RuntimeIdentifier`（复数会触发引用项目的 MSB3030），且本机的 MSBuild 签名任务不会真正写入签名，发布后需用 build-tools 手动 zipalign + apksigner 签名（密钥库见 `signing/`，密码见 `signing/keystore-info.txt`）：

```bash
dotnet publish src/LocalTransfer.Mobile/LocalTransfer.Mobile.csproj -f net8.0-android -c Release \
  -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="signing/localtransfer.keystore" \
  -p:AndroidSigningKeyAlias=localtransfer -p:AndroidSigningStorePass=<密码> -p:AndroidSigningKeyPass=<密码> \
  -p:AndroidPackageFormat=apk -p:RuntimeIdentifier=android-arm64 -p:CheckEolWorkloads=false

BT="C:/Program Files (x86)/Android/android-sdk/build-tools/35.0.0"
P=src/LocalTransfer.Mobile/bin/Release/net8.0-android/android-arm64/publish
"$BT/zipalign.exe" -f 4 "$P/com.localtransfer.mobile.apk" "$P/aligned.apk"
java -jar "$BT/lib/apksigner.jar" sign \
  --ks signing/localtransfer.keystore --ks-key-alias localtransfer \
  --ks-pass pass:<密码> --key-pass pass:<密码> \
  --out dist/局域传输-1.0-arm64.apk "$P/aligned.apk"
```

## 当前限制

- 尚未在真实 Android 手机、微信、QQ 或真实局域网中联调；当前仅完成代码、APK 构建和本机 HTTPS 仿真验证。
- iOS 主应用代码和权限声明已准备，但本机只有 .NET 9 iOS 目标包，缺少 `net8.0-ios` 目标包，也没有配对 Mac，因此尚未完成 iOS 构建与真机验证。
- iOS Share Extension 尚未创建；目前 iPhone 路径是应用内文件选择器。
- Windows 到手机的发送队列目前保存在内存中，Windows 应用重启后需重新排队。
- 尚未实现历史记录、自动发现、多网卡手动选择、安装器和自动配置防火墙规则。
- 尚未执行需求基线中的 10 GB 单文件、100 文件批量和断网恢复真机验收。
- MAUI 8 已结束官方支持；这是遵循本次指定的 .NET 8 回退目标，后续应单独评估升级。

## iOS 打包

iOS 的原生链接与 .app 组装要求 `IsMacEnabled=true`（即真实的 Mac 环境），Windows 本机只能编译托管程序集、无法产出 ipa。仓库提供 GitHub Actions 工作流 `.github/workflows/ios-package.yml`（macOS runner、手动触发），产出未签名 ipa，再用 Sideloadly/AltStore 以个人 Apple ID 签名安装。本机需临时把 Mobile 项目切到 `net9.0-ios`（本机只装有 .NET 9 iOS 目标包），工作流中已包含同样处理。

协议和后续计划见 [协议说明](docs/protocol-v1.md) 与 [实施路线](docs/roadmap.md)。
