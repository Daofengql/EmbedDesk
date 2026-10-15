# 指南图片素材

`docs/public/images/` 仅保存公开指南使用的产品截图。截图来自当前 WPF 界面与隔离的演示 / Smoke 环境，使用模拟设备、回环地址或占位目标，不包含真实用户连接资料。图中的演示状态不代表硬件或生产部署已验收。

| 素材 | 演示内容 |
| --- | --- |
| `new-session.png` | 内部新建弹窗分类，裁切弹窗区域 |
| `serial-connection.png` | 模拟 COM99、115200 / 8N1 / 无流控，裁切弹窗 |
| `tcp-client.png`、`tcp-server.png` | 本机 TCP/UDP 连接表单，裁切弹窗 |
| `pid-server.png` | PID 的服务端角色、协议和本机监听，裁切弹窗 |
| `pid-workspace.png`、`camera-workspace.png` | 尚未连接设备时的工具内容区 |
| `connection-center.png` | 纯演示 ID / 密码、占位目标与模拟串口 |
| `settings-relays.png` | 回环测试中继未启动时的配置与错误状态 |
| `remote-profile.png` | 回环地址的 VNC 新建连接表单 |
| `network-tools.png`、`embedded-tools.png` | 当前正常产品的分类工具入口 |
| `lan-server.png`、`lan-client.png` | 尚未启动服务 / 连接时的文件工具 |

图片只裁切了界面局部或截图末尾的纯黑空白，没有合成状态、改写字段或遮盖错误。当前操作和预期结果在对应指南中说明。更新素材时先核对正常产品入口与当前界面一致，逐张进行隐私和可读性检查。

生成方式与临时截图位置见[文档维护](../development/docs-site.md#截图素材)。不要将临时回归截图、日志或真实用户缓存批量复制到图片目录。
