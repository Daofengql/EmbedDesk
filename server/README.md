# EmbedDesk 中继服务器与控制台

Go / Gin / GORM 中继，配套 React / TypeScript / Ant Design 控制台。Go 项目在 `server/`，React 源码在 `server/www/`；HTML、JS、CSS、图标和许可通过 `go:embed` 嵌入服务端。Linux 产品交付为单个二进制，运行时不需要 Node、前端目录或桌面 .NET 环境。

生产推荐 MySQL 和 Redis，保留 SQLite 与数据库会话存储用于单机开发及桌面回归。服务只监听 HTTP，公网 HTTPS / WSS 由 Nginx 等反向代理提供。桌面协议和连接代码保持版本 1。

## 准入、归属与带宽

每个节点只有一份管理员维护的公共准入 Token，所有客户端共用。它仅用于简单过滤扫描请求，不是用户登录凭据、设备归属证明或对其他设备的访问授权。留空关闭过滤。更新 Token 立即用于后续设备注册，不主动断开现有连接；客户端再次注册前应导入新的连接代码。

首次接入的无主设备为匿名设备，每台独立使用节点默认带宽。注册用户输入自己客户端的设备 ID，可以将无主设备绑定到当前账号；别人已持有的设备不可直接领取，同属自己的设备重复绑定不重复计数。知道未绑定设备的 ID 即可领取，该流程不证明真实设备持有，不应将设备 ID 当作强所有权凭据。管理员可分配、转移或清空归属。绑定和更改归属会断开该设备，重连后应用新的权限和配额。

**同一账号下全部设备共享该账号的双向合计总带宽配额。** 同账号两台设备互传时，按两端各自的 RX/TX 计入同一预算。管理员可单独提高或降低用户配额；更改用户带宽会断开关联设备，重连后生效。节点默认带宽作为新注册用户总配额的初始值，不覆盖已有用户；匿名设备每台使用该默认值，已在线匿名设备下次重连后应用变更。范围为 8 Kbit/s–100 Gbit/s，1 Mbps 仅为初始示例。

## 三级权限与注册

| 角色 | 权限 |
| --- | --- |
| 系统管理员 `admin` | 管理全部用户、角色、设备数量和账号总带宽；绑定、分配和管理设备；查看、结束全部会话；修改节点设置；查看审计 |
| 注册用户 `user` | 输入自己的设备 ID 绑定无主设备；管理自己的设备与相关会话；不能修改节点设置或其他账号的归属 |
| 访客 `guest` | 只读查看关联概览、设备与会话；不能自行绑定或修改资源，可由管理员分配设备用于查看 |

所有账号可查看“中继接入”、复制同一份节点连接代码和公共 Token、修改自己的密码并退出。访客是控制台只读账号，与匿名设备不同。控制台 `/console/` 包含概览、设备、中继会话、中继接入、用户和审计；没有个人 Token 创建、撤销或额度制度。危险动作二次确认，后端逐请求校验角色、归属和会话版本。

管理员在“中继接入”点击“刷新令牌”并确认后，由服务器随机生成并保存新的节点公共令牌，同时更新连接代码；控制台不提供手动输入令牌。刷新只影响后续设备注册，不断开现有连接。中继设置保留过滤开关，首次启用或重新启用时自动生成令牌；普通保存不会轮换现有令牌。

自助注册默认关闭。开启后，新账号固定为 `user`、设备上限 5，总带宽初值取当前节点默认值。注册必须使用一次性图片验证码，有效期 3 分钟，验证码校验后即消费，提交失败前端获取新图；登录不需要验证码。密码为 12–72 UTF-8 字节，汉字通常占 3 字节；用户名为 3–48 位小写 ASCII 字母、数字、点、下划线或短横线，首位为字母或数字。

禁用用户或设备会断开相关设备并拒绝重新认证。“断开设备”和“关闭会话”只结束当前连接，客户端可能自动重连。删除登记释放设备额度并断开设备，符合准入条件的设备仍可作为匿名设备重新注册；阻止接入应使用禁用。最后一个可用管理员不能被禁用或降级。修改密码、角色或禁用状态会撤销该账号原有控制台会话。

## 管理员运行监控

管理员首页以服务器 CPU 利用率、系统内存和当前中继数据带宽为主，同时显示 1 / 5 / 15 分钟平均负载、累计接收／发出数据、系统与进程运行时间、Go 堆内存及 Goroutine 数。在线设备、活动会话和账号数量作为次要统计。页面每轮请求完成后等待 2 秒刷新，离开页面取消轮询；失败显示错误和最后采样时间，未知系统指标显示“暂不可用”，不以 0 代替。

带宽统一从中继视角显示：RX 是客户端发往中继，TX 是中继成功发往客户端，合计为 RX + TX。速率和累计字节只统计加密数据 `body`，不包含网卡其他流量或 WebSocket / TLS 包头；一次转发分别计入接收和成功发送，不能当作单向业务净吞吐。空闲时速率归零，累计字节从进程启动计算、重启归零。平均负载表示系统运行队列负载，不是 CPU 百分比。

`GET /api/console/overview` 仅对管理员附加 `server` 指标，普通用户和访客仍只看自身概览，不返回主机负载、内存或全局流量。监控是当前状态采样，没有历史时序数据库、告警或跨实例汇总。

## 构建

开发需要 Go 1.26.9+、Node 22.12+（建议 24）和 npm。在仓库根运行：

```powershell
./server/build.ps1
./server/build.ps1 -TargetOS windows
```

默认成品为 `artifacts/server/linux-amd64/embeddesk-server`；显式指定 Windows 时为 `artifacts/server/windows-amd64/embeddesk-server.exe`。脚本执行 `npm ci`、TypeScript / Vite 构建、许可收集，再生成包含前端的单二进制。Linux / macOS 使用 `sh server/build.sh`，支持 `GOOS` / `GOARCH`。

Linux 虚拟机内手动执行顺序（仓库根目录）：

```sh
cd server/www
npm ci
npm run build
cd ..
go test ./...
go vet ./...
mkdir -p ../artifacts/server/linux-amd64
CGO_ENABLED=0 go build -trimpath -ldflags '-s -w' -o ../artifacts/server/linux-amd64/embeddesk-server .
```

`www/dist/.gitkeep` 只保证干净检出的协议测试可以编译。普通 `go build` 不代表前端已构建，缺少前端时 `/console/` 返回 503；发布使用构建脚本。前端开发运行 `npm run dev`，Vite 将 `/api/console` 代理到本机 7080。

## 首次启动与配置优先级

复制 `config.example.json` 为不提交仓库的 `relay.json`，通过私有环境或服务管理器设置 `EMBEDDESK_BOOTSTRAP_USERNAME` 和 12–72 UTF-8 字节的随机 `EMBEDDESK_BOOTSTRAP_PASSWORD`，设置 `EMBEDDESK_CONFIG=relay.json` 后启动二进制。不要将实际密码写入仓库或命令历史。

示例仅监听 `127.0.0.1:7080`，使用 SQLite、本机 HTTP Cookie，关闭公共 Token 过滤和自助注册。访问 `http://127.0.0.1:7080/console/`；开放注册需设 `registrationEnabled: true`。公网使用 HTTPS 反向代理与 Secure Cookie。公开地址是 HTTP(S) 根地址，不含账号、查询参数或片段，仅允许空路径或 `/`。

首次启用控制台时，`relayName`、`publicUrl`、`admissionKey`、`bandwidthBitsPerSecond` 从启动配置初始化数据库 `NodeSettings`。**随后以数据库中的节点设置为准**，在控制台修改并持久化，重启不会用旧环境变量覆盖。监听地址、数据库、Redis、Cookie 和注册开关仍来自启动配置。未启用控制台时，使用启动配置中的公共 Token 与匿名设备默认带宽。

Bootstrap 仅在账号表为空时创建管理员，重启不重置密码。初始化后移除 Bootstrap 用户名和密码；开启控制台但无可用管理员时拒绝启动。原配置若含已移除的准入模式字段，应先删除，当前只有节点公共 Token 过滤这一种准入配置。

## 部署

推荐 MySQL、Redis、HTTPS 反向代理。在 `server/` 复制 `.env.example` 为 `.env`，替换占位值，运行 `docker compose up -d --build`。MySQL、Redis 不映射宿主端口，HTTP 只绑定宿主回环；中继容器使用非 root 用户。MySQL 密码须与 DSN 一致，Redis 密码须与 URL 一致，特殊字符按对应语法编码。

也可由 Docker 直接暴露端口，无需修改宿主 Nginx：将 `.env` 中 `EMBEDDESK_BIND_ADDRESS` 设为 `0.0.0.0`、`EMBEDDESK_PORT` 设为所需端口，`EMBEDDESK_PUBLIC_URL` 填写对应的 `http://HOST:PORT`，并设 `EMBEDDESK_COOKIE_SECURE=false`。此方式为明文 HTTP，控制台登录和控制链路没有 TLS 保护；设备数据正文仍按既有协议加密。需要 HTTPS 时使用前述反向代理配置。默认注册关闭，开放注册需显式设置 `EMBEDDESK_REGISTRATION_ENABLED=true`。

也可先在开发机运行 `build.ps1` / `build.sh`，将对应架构的 Linux 二进制与 `deploy/Dockerfile.binary` 放入独立构建目录，在目标主机执行 `docker build -f Dockerfile.binary -t embeddesk-relay:local .`；随后使用同一镜像名称，执行 `docker compose up -d --no-build`。二进制已嵌入控制台和许可证，目标主机无需安装 Go、Node.js。部署文件和 `.env` 应限制访问，初始化完成后移除 Bootstrap 两个值并仅重建中继容器。

独立二进制使用 `deploy/embeddesk-server.service` 示例，先创建专用服务账号、数据目录与仅服务账号可读的环境文件。Nginx 示例为 `deploy/nginx.conf.example`，保留 Host 与 WebSocket Upgrade，空闲超时大于 75 秒。控制台部署于源站根目录下 `/console/`，不支持额外 URL 前缀。

| 环境变量 | 默认 / 含义 |
| --- | --- |
| `EMBEDDESK_CONFIG` | 可选 JSON，拒绝未知字段及超过 64 KiB 的配置 |
| `EMBEDDESK_LISTEN` | `127.0.0.1:7080` |
| `EMBEDDESK_DATABASE_DRIVER` | `sqlite` 或 `mysql`，默认 `sqlite` |
| `EMBEDDESK_DATABASE` | SQLite 文件，默认 `data/relay.sqlite` |
| `EMBEDDESK_MYSQL_DSN` | MySQL 连接串，需指定数据库 |
| `EMBEDDESK_REDIS_URL` | 可选 `redis://` / `rediss://`，用于会话、验证码和尝试限流 |
| `EMBEDDESK_REDIS_PREFIX` | 默认 `embeddesk:`，不同部署使用不同前缀 |
| `EMBEDDESK_CONSOLE_ENABLED` | 默认 `false`，示例与 Compose 为 `true` |
| `EMBEDDESK_REGISTRATION_ENABLED` | 默认 `false`，开放带图片验证码的普通用户注册 |
| `EMBEDDESK_COOKIE_SECURE` | 默认 `true`，仅可信本机 HTTP 开发设为 `false` |
| `EMBEDDESK_PUBLIC_URL` | HTTP(S) 根地址，最多 2048 字符；空值不生成连接代码；首次初始化后在控制台修改 |
| `EMBEDDESK_RELAY_NAME` | 名称 1–64 个 UTF-16 字符且无控制符；首次初始化后在控制台修改 |
| `EMBEDDESK_BOOTSTRAP_USERNAME` / `EMBEDDESK_BOOTSTRAP_PASSWORD` | 初始化空账号表，不从 JSON 读取 |
| `EMBEDDESK_ADMISSION_KEY` | 节点公共 Token，最多 2048 可见 ASCII 字符，不含空白；默认空，首次初始化后在控制台修改 |
| `EMBEDDESK_BANDWIDTH_BITS_PER_SECOND` | 默认 `1000000`，范围 8 Kbit/s–100 Gbit/s；新用户总配额初值与匿名设备每台默认值，首次初始化后在控制台修改 |

已配置 Redis 但连接失败时拒绝初始化；运行期间会话或验证码存储不可用返回 503，不绕过校验。未配置 Redis 时保存到数据库。Cookie 为 HttpOnly、SameSite=Strict，默认 Secure；会话固定 12 小时，每用户最多 20 个，退出立即撤销。写接口要求 JSON 和 CSRF Token。登录／注册共享每来源每 15 分钟 100 次、每用户名 10 次限额，并有实例全局速率保护。不信任任意 X-Forwarded-For，代理后的用户共享代理来源限额。

## 桌面接入与 API

在“中继接入”页复制公共连接代码，到桌面设置 → 中继服务器导入。代码仍为版本 1 的 `EMBEDDESK-RELAY:` + Base64 JSON，含名称、地址和节点公共 Token；Base64 不是加密。Token 不授予设备归属，访问对端仍需密码或人工授权。设备 ID 由随机设备凭据摘要生成，不读取硬件序列号。

CLI `--connection-code PUBLIC_URL NAME` 使用启动配置的 `admissionKey`，不读取控制台数据库。节点已在控制台更新后，应复制网页显示的当前代码，避免导出旧配置。

- `GET /health`：公开协议版本和进程存活，供桌面测速使用。
- `GET /ready`：数据库及已配置 Redis 就绪检查，失败 503。
- `POST /v1/register`：`{ "credential": "BASE64_RANDOM_32_BYTES" }`，Bearer 为节点公共 Token，未启用过滤可为空。返回设备 ID、适用带宽值和协议版本 1；绑定设备的带宽值代表所属账号共享总额度。
- `GET /v1/devices/ID`、`GET /v1/channel`：设备凭据 Bearer + X-Device-ID，后者升级 WebSocket，每设备仅一条在线连接。
- `/api/console/auth/options`、`auth/captcha`、`auth/register`、`auth/login`、`auth/me`、`auth/password`、`auth/logout`：控制台认证；注册提交 `captchaId` 与 `captchaCode`。
- `/api/console/overview`、`users`、`devices`、`sessions`、`audit`：控制台资源，列表支持 page / pageSize / q，每页最多 100；写入需 X-CSRF-Token。
- `GET /api/console/relay`：所有登录角色读取公共接入信息；`PATCH` 仅管理员可更新名称、公开地址、默认带宽及 `admissionEnabled` 过滤开关。开启且当前无令牌时自动生成，关闭时清空，省略开关则保留；不接受手动提交 `admissionToken`。
- `POST /api/console/relay/admission-token/refresh`（管理员，JSON `{}`）：随机生成 32 字节、Base64URL 编码的公共令牌并启用过滤。响应包含新令牌及同步更新的连接代码，操作与审计在同一事务中保存；现有设备连接保持。
- `POST /api/console/devices/bind`：管理员／注册用户提交 `{ "deviceId": "DEVICE_ID" }` 绑定无主设备；访客禁止。无此设备返回 404，别人已持有或超配额返回 409。
- `PATCH /api/console/devices/ID`：管理设备；管理员可提交 `ownerId` 转移归属，`null` 恢复匿名设备。
- `POST /api/console/sessions/ID/close` 结束会话；`POST /api/console/devices/ID/disconnect` 断开设备；`DELETE /api/console/devices/ID` 删除登记。
- `POST /api/console/users/batch`（管理员）可对当前选中的 1–100 个账号批量启用、禁用、设置账号总带宽或设备数量上限；`POST /api/console/devices/batch` 可批量启用、禁用、断开、删除或由管理员分配／清空归属；`POST /api/console/sessions/batch` 可批量关闭会话。批量请求整批校验，失败不产生部分修改；会话中已结束的 ID 返回 `skipped`。

握手、加密转发、心跳和有界队列保持现有协议。最多 1,024 在线设备、4,096 设备会话、每设备 16 会话；待授权超时 90 秒。每设备三类队列各 64 项，报文最多 96 KiB、加密 body 最多 64 KiB。账号设备共享双向总限速，匿名设备各自限速；局域网直连不受中继配额限制。详见[设备协议](../docs/DeviceConnections.md)。

## 数据与边界

数据库保存 bcrypt 密码摘要、账号角色／总配额、设备凭据摘要／归属、节点名称／地址／公共 Token／默认带宽和审计。公共 Token 为生成当前连接代码而保存在节点设置中，数据库与备份应按敏感配置保护。审计不存密码、Token、认证头、候选地址或业务正文。原始设备凭据、设备连接密码、配对密钥、会话密钥与转发内容不落盘。Redis 保存会话摘要、CSRF、期限、验证码答案摘要及限流计数，不存原始 Cookie。

单实例在内存管理设备、会话与账号共享限速。Redis 不提供跨实例路由或分布式带宽，不能用随机负载均衡组成集群。当前没有计费、邮箱验证、密码找回或跨节点集群。重启不恢复设备授权和活动会话，客户端按既有机制重连。GORM 增量迁移不自动将 SQLite 导入 MySQL。使用一致性数据库备份，SQLite WAL 不能只复制正在写入的主文件。Redis 会话丢失需重新登录。SIGTERM / Ctrl+C 等待资源清理。

## 验证与许可

在 `server/` 运行 `go test ./...`、`go vet ./...`。`TestExternalMySQLRedis` 仅在提供 `EMBEDDESK_TEST_MYSQL_DSN`、`EMBEDDESK_TEST_REDIS_URL` 时执行；目标须为本次专用空数据库，Redis 使用随机前缀，不清理其他部署。Linux 虚拟机用于实际 MySQL / Redis 与单二进制验证，仓库不记录虚拟机账号、密码或私有网络地址。

验证范围包括协议、三级权限、归属隔离、绑定、验证码一次性消费、CSRF、最后管理员保护、节点设置持久化、账号共享配额及会话关闭。桌面兼容检查先构建新的 `artifacts/relay-test.exe` 和独立 Smoke 项目，再运行 `--device-connections-check`、`--device-security-check`。编译不能代替浏览器操作和截图检查；实际执行结果、虚拟机互通、公网与硬件实测分别报告。

控制台页面在固定视口内独立滚动，Ant Design 的菜单和对话框挂载于页面滚动容器外，避免弹层测量、翻转及滚动锁定影响主布局。系统“减少动画”偏好通过 Ant Design 的 `motion` 主题设置响应，并随系统设置变化更新；不要给所有元素统一设置过渡时长，否则菜单定位期间的临时坐标也会发生过渡，导致菜单移出视口。界面回归需在普通和减少动画两种模式下，实际打开批量菜单、设备更多操作、令牌刷新确认框和包含下拉框的表单，在窗口边缘、短窗口及窄屏检查滚动位置、内容尺寸、菜单是否在视口内且能接收鼠标点击，并点击菜单项进入对应表单或确认框；不能仅检查未打开弹层的页面宽度。

原创代码 GPL-3.0-only。Go 原始许可见 [licenses/NOTICE.md](licenses/NOTICE.md)，验证码使用未修改的 `github.com/dchest/captcha v1.1.0`。前端 77 个生产依赖的版本与版权见 [www/licenses](www/licenses/DEPENDENCIES.md)，通过 `npm run licenses` 收集；少数包缺少独立许可正文的情况已注明来源与边界。发布构建嵌入全部许可，`embeddesk-server --licenses` 或 `/console/licenses/THIRD_PARTY_LICENSES.txt` 可查看。MySQL、Redis、Nginx 为独立软件，不包含在服务器二进制内。
