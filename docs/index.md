---
layout: home
title: 使用指南
hero:
  name: EmbedDesk
  text: 嵌入式联调使用指南
  tagline: 在一个 Windows 工作台里，连接设备、收发报文、观察曲线与图像，完成远程联调。
  image:
    src: /embeddesk.svg
    alt: EmbedDesk 终端图标
  actions:
    - theme: brand
      text: 安装与快速开始
      link: /guide/getting-started
    - theme: alt
      text: 下载 Windows EXE
      link: https://github.com/Daofengql/EmbedDesk/releases
features:
  - title: 调试助手
    details: 串口、TCP/UDP、MQTT、PID 与摄像头共用一排独立会话标签。
    link: /guide/debug-assistant
    linkText: 创建调试会话
  - title: 连接中心
    details: 使用设备 ID 授权连接，共享串口与 TCP/UDP 服务，自动尝试局域网直连。
    link: /guide/connection-center
    linkText: 连接与共享
  - title: 远程调试
    details: 本地 Shell、SSH 终端、远程桌面与文件工作区，集中处理远端操作。
    link: /guide/remote
    linkText: 使用终端与文件
  - title: 网络工具
    details: DNS、Ping、端口扫描、iperf3 和局域网文件，按任务打开独立工具。
    link: /guide/network-tools
    linkText: 诊断网络
  - title: 嵌入式工具
    details: 按芯片选择烧录工具，读写原始镜像，检查二进制文件和校验值。
    link: /guide/embedded-tools
    linkText: 查看支持范围
  - title: 技术与开发
    details: 查阅架构、协议、兼容范围和构建方法，区分现有实现与待验收事项。
    link: /reference/
    linkText: 查阅技术参考
---

## 从一个小任务开始

| 想完成的事 | 从这里进入 |
| --- | --- |
| 不接设备，先试一次收发 | [本机 TCP 收发示例](guide/getting-started.md#发送第一条报文) |
| 查看开发板日志或发送 HEX | [串口调试](guide/serial.md) |
| 调 PID 参数或接收图像 | [PID 与摄像头](guide/pid-camera.md) |
| 让另一台电脑使用已发布的串口或端口 | [连接中心与共享](guide/connection-center.md) |
| 在局域网传大文件 | [局域网文件传输](guide/lan-files.md) |
| 找不到入口，或连接失败 | [常见问题](guide/faq.md) |

默认发行版是 Windows x64 精简单 EXE，需要 .NET 8 Desktop Runtime 和 WebView2 Runtime。下载、依赖和首次启动步骤见[快速开始](guide/getting-started.md)。
