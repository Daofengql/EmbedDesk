# com0com 扩展接口来源

`src/EmbedDesk.Infrastructure/Serial/Com0ComEndpoint.cs` 中的扩展 IOCTL 签名、选项、快照布局和引脚值依据 com0com 的 `include/cncext.h`、`include/com0com.h`、`sys/ioctl.c` 与 `setup/params.cpp` 转写为 C#，其余扫描、生命周期、缓冲和设备通道桥接为 EmbedDesk 原创实现。

- 上游项目：[Null-modem emulator (com0com)](https://sourceforge.net/projects/com0com/)
- 所核对源码镜像：[paulakg4/com0com](https://github.com/paulakg4/com0com/tree/c488efac89f7596eafce569003be4e3ff0d2efb4)，修订 `c488efac89f7596eafce569003be4e3ff0d2efb4`。
- `cncext.h`：Copyright (c) 2008 Vyacheslav Frolov。
- 其他参考文件含原作者 2004–2012 年版权声明；仅适配上述定义及接口行为，没有复制完整驱动或安装程序实现。
- 许可：GPL-2.0-or-later，与项目的 GPL-3.0 分发兼容。[GPL v2 原文](com0com-GPL-2.0-or-later.txt) 来自 [上游源码镜像的 license.txt](https://github.com/tanvir-ahmed-m4/com0com/blob/master/license.txt)，随应用嵌入。

驱动、签名证书、setupc、hub4com 和厂商 DLL 均不包含在 EmbedDesk 中。用户独立安装的驱动遵循其分发方许可与系统兼容性要求。功能及实测边界见 [系统虚拟串口](../VirtualSerialPorts.md)。
