# EmbedDesk 中继服务

独立 Go 服务，使用 Gin、GORM 和纯 Go SQLite 驱动；没有 MySQL、账号系统、会员等级或管理面板。服务本体只监听 HTTP，生产 HTTPS 由 Nginx 等反向代理终止；客户端仍可用 `https` / `wss` 地址访问代理。每设备双向中继额度来自配置文件。客户端操作与安全边界见 [设备连接说明](../../docs/DeviceConnections.md)。

## 构建与本机验证

需要 Go 1.26.9 或更新版本；当前构建与依赖安全检查使用 Go 1.26.9。较旧 Go 可通过自动工具链下载满足 `go.mod` 要求。在仓库根目录运行：

```powershell
Set-Location server/relay
go test ./...
go vet ./...
go build -o ../../artifacts/relay-test.exe .
```

默认运行地址 `127.0.0.1:7080`，数据库 `data/relay.sqlite` 相对于进程工作目录：

```powershell
./../../artifacts/relay-test.exe
```

客户端设置中填写 `http://127.0.0.1:7080` 可进行本机或内网测试；通过反向代理时填写代理提供的 `https://` 地址。`GET /health` 返回健康状态和协议版本，不记录请求正文或认证头。应用没有预置免费公共服务节点；构建服务端并不代表它已部署上线。

## 部署选项

| 环境变量 | 默认 / 含义 |
| --- | --- |
| `EMBEDDESK_LISTEN` | `127.0.0.1:7080`，HTTP 监听端点 |
| `EMBEDDESK_DATABASE` | `data/relay.sqlite`，可写数据库文件路径 |
| `EMBEDDESK_ADMISSION_KEY` | 空；非空时注册需提供相同 bearer 准入密钥 |
| `EMBEDDESK_CONFIG` | 可选 JSON 配置文件；字段见下方，环境变量可覆盖对应字段 |
| `EMBEDDESK_BANDWIDTH_BITS_PER_SECOND` | 可选环境变量覆盖，默认 `1000000`；配置优先用于部署，范围 8 Kbit/s–100 Gbit/s |

可以复制 [config.example.json](config.example.json) 为本机 `relay.json`，修改后运行；配置在服务重启后生效，不会在线热更新：

```powershell
$env:EMBEDDESK_CONFIG = 'relay.json'
./../../artifacts/relay-test.exe
```

`relay.json` 示例（带宽是每个在线设备双向合计额度）：

```json
{
  "listen": "127.0.0.1:7080",
  "database": "data/relay.sqlite",
  "admissionKey": "ADMISSION_KEY",
  "bandwidthBitsPerSecond": 1000000
}
```

HTTP 监听由已有反向代理提供 HTTPS。代理须转发 `Authorization`、`X-Device-ID` 和 WebSocket Upgrade，关闭响应缓存，为 `/v1/channel` 保留长连接，空闲超时应大于 75 秒；不要将内部 HTTP 监听直接暴露到公网。客户端以同一个 HTTPS 根地址访问注册与 WebSocket，不需要单独的数据端口。当前客户端相对 URL 也支持代理部署的基础路径。

在已有 Nginx HTTPS `server` 内可以加入如下转发；证书仍由 Nginx 配置管理。客户端地址为该 HTTPS 根地址：

```nginx
location / {
    proxy_pass http://127.0.0.1:7080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 120s;
    proxy_send_timeout 120s;
    proxy_buffering off;
}
```

Linux 原生构建用 `go build -o embeddesk-relay .`；Windows 可交叉编译：

```powershell
$env:GOOS = 'linux'
$env:GOARCH = 'amd64'
$env:CGO_ENABLED = '0'
go build -o ../../artifacts/relay-linux-amd64 .
Remove-Item Env:GOOS, Env:GOARCH, Env:CGO_ENABLED
```

把二进制以普通服务账号运行，固定工作目录并给予数据库目录写权限；公网只开放反向代理 HTTPS 端口，内部 HTTP 限制为代理可达。中继不承担客户端 COM / TCP / UDP 目标的监听；UDP 映射也由桌面客户端通过已有加密会话转发。生产服务由 systemd 或现有进程管理器维护。SIGTERM / Ctrl+C 会停止准入、关闭 WebSocket、结束工作协程和关闭数据库。

## 数据与资源

SQLite 使用 WAL 和 busy timeout，仅持久化设备 ID、设备凭据 SHA-256 与创建时间。随机设备凭据由客户端保存；密码、临时授权、会话密钥、地址候选、队列和业务数据不写入数据库。重启后设备凭据可再次登记同一 ID，活动连接和授权不恢复。当前无自动过期登记清理或管理员撤销工具。

备份持久化目录时应停止服务，或使用 SQLite 一致性备份，不能只复制一个正在写入的主数据库文件而遗漏 WAL。运行数据目录和准入密钥不提交到仓库；TLS 证书 / 私钥由反向代理管理。默认不输出设备 ID、访问头或业务内容；部署反向代理时也应避免记录认证头、请求正文和敏感查询。

服务端限制 1,024 个在线设备、4,096 个设备会话、每设备 16 个会话；等待授权最长 90 秒。WebSocket 报文不超过 96 KiB，加密数据 body 不超过 64 KiB；每设备控制出队列、数据出队列和数据入队列各 64 项。受限数据等待不会阻塞控制消息、心跳或 Ping/Pong。服务端每 25 秒 Ping，75 秒读 / Pong 超时、15 秒写超时；客户端每 20 秒应用心跳、75 秒无响应检测和最多 300 秒指数退避重连。控制报文和全局 HTTP 请求各受 20 次/s、40 次突发限制。超限或慢消费者会被拒绝 / 断开，有界队列不会无限增长。

配置中的带宽令牌桶按每客户端双向合计额度换算为字节速率，上传下载共用，允许 64 KiB 突发，按加密 JSON body 长度计量；客户端增加本机规则不能提高额度。默认示例为 1,000,000 bits/s，但不是协议常量。直连业务不经过中继带宽预算。首期没有总流量配额、每 IP 防滥用策略、计费或水平扩容：单实例内存管理在线设备和会话，SQLite 文件不可用作多实例路由共享。

## HTTP 与控制协议

- `POST /v1/register`：JSON `{ "credential": "BASE64_RANDOM_32_BYTES" }`；可选统一准入 bearer。返回 ID、`bitsPerSecond` 和协议版本。
- `GET /v1/devices/ID`：设备 bearer 加 `X-Device-ID`，返回对方在线状态。
- `GET /v1/channel`：相同设备认证头，升级为 WebSocket；每设备仅一个在线连接。
- `GET /health`：无认证的状态检查。

WebSocket 依次路由 `connect / pending / challenge / proof / accept`，随后选择认证直连或 `relay`，中继使用 `data`，关闭使用 `close`；服务端绑定发送者 ID，校验会话成员、握手阶段及角色，未授权不得转发数据。`heartbeat` 和 `error` 用于存活与失败反馈。首期协议版本为 1，不兼容 frps / frpc；STCP 是应用端口映射工具的名称。

人工授权配对密钥经过可信中继内存，HTTP / WS 会明文传输认证凭据和配对消息，公网须由 HTTPS 反向代理保护。密码协议也未经外部密码学审计。部署者应阅读 [安全说明](../../docs/DeviceConnections.md#身份认证与数据路径)，不要宣称服务端永远无法冒充设备或已经获得生产安全认证。

## 许可

原创服务端代码采用 GPL-3.0-only。依赖版本固定在 `go.mod` / `go.sum`；第三方原始许可和版本索引在 [licenses/NOTICE.md](licenses/NOTICE.md)，通过 `go:embed` 随二进制嵌入，运行 `embeddesk-relay --licenses` 查看。默认构建不依赖外部数据库、CGO 或 SQLite DLL。
