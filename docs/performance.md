# 性能与发布体积优化

基线为 `918e4d7`，在同一台 Windows 开发机上用相同数据测试。以下是局部工作负载结果，不代表任意网络或整机速度。

| 项目 | 优化前 | 优化后 |
| --- | ---: | ---: |
| 精简单 EXE | 8,986,391 B（8.57 MiB） | 约 6.2 MiB |
| 串口 300 包 × 202 B，接收区 TextChanged 次数 | 300 | 1 |
| 串口上述突发数据，主线程注入耗时 | 9,397.86 ms | 1.74 ms |
| 串口上述突发数据，注入阶段托管分配 | 380,722,104 B | 242,352 B |
| 重复读取同一 16 KiB 数据的 HEX 文本 100 次 | 53.17 ms / 62,267,520 B 分配 | 0.08 ms / 1,120 B 分配 |
| 1000 包 × 64 KiB 后保留的原始历史 | 62.5 MiB | 4 MiB |
| 800 个终端输出包的 WebView2 调用 | 800（原代码逐包调用） | 1（实测，逐行核对） |

注入阶段衡量接收数据占用 UI 线程的时间。优化后文本由 50 ms 批次刷新；上述串口测试从注入到 UI 空闲共约 136 ms，而不是 1.74 ms。HEX 测试先生成一次文本，再重复访问，验证显示刷新不会反复分配格式化字符串。

## 运行时变化

- 串口和网络接收区共用增量呈现器，追加新数据；切换会话、显示方式或清空时才重新构建。隐藏面板不做格式化和布局。收发方向分别维护 UTF-8 解码状态，支持字符跨包拆分。
- 原始历史同时受条数和字节数限制，收发计数独立累计。连续日志显示采用字符上限，超限时批量移除较早文本；历史不是完整抓包存档。
- 本地 Shell、SSH 和串口 Shell 的输出按最多 64 KiB 合批，保留清空、回放与实时数据的顺序，减少进程间调用。
- 网络收发不再重复触发整组连接控件的状态通知。
- VNC 解码移到后台；Raw 全宽区域直接写入帧缓冲，局部矩形使用池化缓冲，渲染时只复制变化区域到 WPF。回调任务结束前帧像素保持稳定；保存快照的调用者需自行复制。

## 发布与构建

最大依赖 BouncyCastle 约 4.9 MiB，SSH.NET 和 VNC 均需要它。Release 使用 Deflate 压缩嵌入完整程序集，在首次实际使用时解压到内存并加载，后续复用。没有修改算法、升级或降级依赖，也没有开启 WPF 不支持的裁剪。运行后的算法库内存占用不会因此缩小，首次使用会承担一次解压开销。

本机发布版的首次算法库加载实测约 16 ms（含解压及程序集加载）；串口和本地 Shell 不会触发该加载。

压缩目标具有版本隔离和输入 / 输出时间戳，普通增量编译无需重新压缩。一次本机增量发布约 1.2 秒，已验证压缩文件时间戳保持不变；这不是跨机器的构建时长保证。Debug 沿用标准依赖加载；Release 也可用 `-p:PackManagedDependencies=false` 对照排查。

## 复现与验证

```powershell
dotnet run --project tests/EmbedDesk.App.SmokeTests -- --performance-check
dotnet run --project tests/EmbedDesk.App.SmokeTests -- --desktop-network-check
dotnet run --project tests/EmbedDesk.App.SmokeTests -- --network-quality-check
dotnet run --project tests/EmbedDesk.App.SmokeTests
dotnet publish src/EmbedDesk.App -c Release
```

额外以只含测试 EXE 的隔离目录验证 WebView2、嵌入资源、本地 Shell、全屏、原生 RDP 握手及 VNC 认证与局部刷新。`loopback_ssh.py` 设置 `EDBG_SSH_TEST_EXE` 后会运行发布版协议检查：真实 SSH 认证、600 行 MOTD、拆包 UTF-8、性能采集和 TTY 尺寸同步；测试使用临时主机密钥和本机固定测试凭据。

完整桌面测试会模拟物理键盘，要求交互桌面焦点。Debug 全套通过；在由 Python 启动的发布版全套中曾出现物理快捷键因失焦超时，因此发布版还使用独立协议检查和单文件启动检查验证，不把该次超时计为通过。
