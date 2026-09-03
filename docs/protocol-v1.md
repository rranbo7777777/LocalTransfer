# LocalTransfer 协议 v1

## 连接与身份

- Windows 是 HTTPS 服务端和协调端，默认端口 `53317`，手机发起所有 TCP/TLS 连接。
- 配对二维码包含 HTTPS 地址、协议版本、证书 SHA-256 指纹、一次性票据和过期时间。
- 手机必须先按二维码固定证书指纹，再提交一次性票据；Windows 用户批准后返回只展示一次的设备凭据。
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
- `chunkSize`，当前默认 4 MiB
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

## 状态机

主要状态为：`WaitingForApproval → Queued → Transferring → Verifying → Completed`。

旁路状态包括 `Paused`、`WaitingForConnection`、`Failed`、`Rejected` 和 `Canceled`。服务端只允许显式定义的转换，终态不能继续写入数据。

## 恢复语义

- 接收端在目标目录的 `.localtransfer` 子目录保存 `{transferId}.part` 和 JSON 检查点。
- 成功写入并刷新一个分块后，才原子更新检查点。
- 重连后发送方查询 `missingChunks`，只重传缺失分块。
- 手机完成下载但完成响应丢失时，会保留本地完成回执；下次只重试确认，不重复下载。
