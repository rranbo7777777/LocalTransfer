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
| `GET` | `/api/v1/health` | 服务与协议版本检查 |
| `POST` | `/api/v1/pairing` | 提交一次性配对票据 |
| `GET` | `/api/v1/pairing/{requestId}` | 轮询电脑批准结果 |
| `POST` | `/api/v1/transfers` | 手机提交上传文件清单 |
| `GET` | `/api/v1/transfers/{transferId}` | 查询批准状态和缺失分块 |
| `PUT` | `/api/v1/transfers/{transferId}/chunks/{index}` | 手机上传一个分块 |
| `POST` | `/api/v1/transfers/{transferId}/complete` | 请求整文件校验和落盘 |
| `DELETE` | `/api/v1/transfers/{transferId}` | 取消手机上传 |
| `GET` | `/api/v1/outbound` | 手机查询电脑为本设备排队的文件 |
| `GET` | `/api/v1/outbound/{transferId}/chunks/{index}` | 手机下载一个分块 |
| `POST` | `/api/v1/outbound/{transferId}/complete` | 手机确认校验和保存完成 |
| `DELETE` | `/api/v1/outbound/{transferId}` | 取消电脑到手机任务 |

完成接口是幂等的：响应丢失后重复提交不会生成重复文件，也不会把已完成任务误判为失败。

## 服务端防护

- 配对端点按来源 IP 做固定窗口限流，**提交与轮询各自独立计数**：提交票据 30 次/分钟，状态轮询 240 次/分钟，超限返回 `429`。
- 手机提交票据后按 `PairingPollInterval`（当前 2 秒）轮询批准结果。轮询配额必须显著高于该频率（60 / 2 秒 = 30 次/分钟），否则电脑端用户回答批准稍慢，客户端就会把自己的配额耗尽并中断配对。两个端点曾共用同一份 30 次/分钟的配额，实测导致配对在约 22 秒后必然失败并返回 `429`。
- 每台设备的待批准入站邀约最多 10 个，超出返回 `409`。
- **未答复的入站邀约在 11 分钟后自动作废（转为 `Rejected`）。** 该有效期必须长于客户端自己的批准等待（10 分钟），否则仍在轮询的手机会看到邀约凭空消失。缺少这条规则时，被忽略的确认提示会永久占用那 10 个名额：攒满之后该设备的所有发送都返回 `409`，只能靠重启电脑端恢复。
- **过期后不得批准。** `ApproveAsync` 自己检查有效期，不依赖清扫时机；否则"确认提示还开着时点「是」"会走到过期之后才执行，把一次已经作废的传输变成 `Queued`。
- 配对请求（含被拒绝的）保留 15 分钟后清理；传输进入终态后保留 10 分钟供对端查询最终状态，随后清扫。
- **批准只在票据有效期内有效。** 客户端随 bootstrap 过期即停止轮询，所以票据过期后再批准必须被拒绝（不写可信设备表、也不出现在待批准列表里）。否则会出现"电脑已信任、手机未持有"的永久不一致：该手机之后每个请求都返回 `401`，且只能靠重新配对恢复——实测就是这样产生了一个只能靠重装 App 才能解释的 401。
- `X-Chunk-SHA256` 必须是 64 个十六进制字符，否则分块下载被拒绝。
- 状态与错误响应不暴露服务器本地文件系统路径。

## 状态机

主要状态为：`WaitingForApproval → Queued → Transferring → Verifying → Completed`。

旁路状态包括 `Paused`、`WaitingForConnection`、`Failed`、`Rejected` 和 `Canceled`。服务端只允许显式定义的转换，终态不能继续写入数据。

## 恢复语义

- 接收端在目标目录的 `.localtransfer` 子目录保存 `{transferId}.part` 和 JSON 检查点。
- 成功写入并刷新一个分块后，才原子更新检查点。
- 重连后发送方查询 `missingChunks`，只重传缺失分块。
- 手机完成下载但完成响应丢失时，会保留本地完成回执；下次只重试确认，不重复下载。
