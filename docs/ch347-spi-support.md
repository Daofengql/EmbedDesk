# CH347T SPI 烧录支持与协议依据

## 连接方式

本程序直接通过 Windows 的 `hid.dll`、`setupapi.dll` 与异步 HID 文件句柄访问 CH347T，不运行 SNANDer / flashrom，不加载或分发 WCH 厂商 DLL，不需要 Python / hidapi。

当前支持 **CH347T 标准 HID 模式**，USB 标识为 `VID 1A86 / PID 55DC`，SPI 为 `MI_01`。接口以独占方式打开，SPI 模式固定为 Mode 0、MSB-first、8 位数据，可选择 CS1 / CS2 与 468.75 kHz – 60 MHz 时钟，默认 1.875 MHz。设备的模式切换由板卡硬件完成；厂商驱动的 bulk 模式 (`55DB`) 与 CH347F 不在此实现范围内。

只使用有明确几何参数和状态寄存器行为的芯片表：

| 芯片 | JEDEC | 容量 | 页 | 擦除块 |
| --- | --- | --- | --- | --- |
| W25Q16JV/BV | EF4015 | 2 MiB | 256 B | 4 KiB |
| W25Q32JV/BV | EF4016 | 4 MiB | 256 B | 4 KiB |
| W25Q64JV/BV | EF4017 | 8 MiB | 256 B | 4 KiB |
| W25Q128JV/BV | EF4018 | 16 MiB | 256 B | 4 KiB |
| W25N01GV | EFAA21 | 128 MiB | 2048 B + 64 B OOB | 128 KiB |

未知 ID 禁止擦写。已识别的 JEDEC ID 无法区分同系列的所有封装 / 电压后缀，软件不会控制或推断供电，需核对实体芯片规格和转接板电平。

## NAND 数据约定

- 普通 BIN 使用连续的物理主数据地址，不包含 OOB，不隐式跳过坏块或压缩块号。
- 写入 / 擦除前读取目标范围每块前两页的 OOB 首字节，发现标记坏块则在第一次擦除前拒绝操作。
- 普通读取、写入与回读验证启用芯片内部 ECC 和 buffer read 模式。不可纠正 ECC、编程失败、擦除失败、写使能未生效都会终止操作；不把失败块重新标记或重映射。
- 原始 `.nand` 备份按每页 2048 + 64 字节导出，包括坏块和 OOB，读取时关闭 ECC，完成 / 失败后恢复配置。该格式不能直接作为普通 BIN 写入。
- NAND 擦写临时解除块保护，操作后恢复原寄存器；断开时恢复最初的 ECC / buffer 配置。OTP 模式或 OTP 锁定状态拒绝访问。NOR 的 BP / CMP 保护不自动解除。
- 写入起始地址必须按擦除块对齐，尾部补 FF 至擦除块边界。每个写入页必须回读完全一致，单独擦除还逐页确认 FF。
- 取消在页 / 擦除块边界生效，当前已发送的芯片操作先等待完成。已擦写的数据不回滚。部分备份保留 `.partial-*` 文件，完整备份附带 JSON 元数据和 SHA-256。

此实现不等于通用 NAND 文件系统写入器，不支持任意芯片、外置 ECC 布局、UBI 逻辑卷、坏块自动重映射或 OTP 编程。

## 可核查的实现依据

1. **i2cy / CH347-HIDAPI，MIT**，参考提交 [`be30eef5aa1881d476bb63bdebfdff957655e183`](https://github.com/i2cy/CH347-HIDAPI/tree/be30eef5aa1881d476bb63bdebfdff957655e183)。
   - `ch347api/__device.py`：VID/PID、SPI HID 接口编号、HID 外层两字节长度、C2 全双工分片响应。
   - `ch347api/__spi.py`：C0 SPI 配置、C1 片选控制字段。
   - 该项目根据实际抓包记录了 HID 协议，其 README 明确说明协议来源与限制。此实现使用 C# / Windows HID API 重写，未运行或分发其 Python 包。MIT 声明保留于 [third-party/CH347-HIDAPI-MIT.txt](third-party/CH347-HIDAPI-MIT.txt)。
2. **flashrom，GPL-2.0-or-later**，参考提交 [`bcbe6a074f1b0a37e6cfe2cad041d439f8a775f5`](https://github.com/flashrom/flashrom/blob/bcbe6a074f1b0a37e6cfe2cad041d439f8a775f5/programmers/ch347_spi.c)。
   - `programmers/ch347_spi.c`：交叉核对 C0 / C1 / C2 命令、507 字节分片限制、SPI 配置字段、CS 极性与时钟分频。没有复制其 libusb 后端或捆绑可执行程序。仓库原创代码使用 GPL-3.0-only；GPL-2.0-or-later 的兼容要求可满足。
3. **Winbond W25Q 系列 / W25N01GV 数据手册**：JEDEC ID、03h 读取、02h 编程、20h / D8h 擦除、06h / 04h 写使能、NAND 0Fh / 1Fh 状态、13h page-to-cache、10h program-execute、ECC 与保护寄存器行为。
   - 官方产品入口：[Winbond Serial NAND](https://www.winbond.com/hq/product/code-storage-flash-memory/serial-nand-flash/)。
   - NAND 型号几何、ID 与 buffer-mode 行为亦和 [Linux `drivers/mtd/nand/spi/winbond.c`](https://github.com/torvalds/linux/blob/master/drivers/mtd/nand/spi/winbond.c) 的 W25N01GV 项交叉核对。未复制该文件或 SNANDer 的实现。

## 验证边界

`--ch347-check` 使用模拟 SPI Flash 验证 JEDEC 探测、NOR 页边界、邻块保护、地址越界、保护位、回读差异、取消、NAND 坏块预检、ECC 错误、原始 OOB 导出、失败状态和寄存器恢复；窗口初始化只枚举 HID 设备。

现有验证说明不包含实际 CH347T / Flash 擦写验收。Windows HID 报告、不同 CH347 固件和上述芯片的实机互操作仍需硬件回归，模拟测试不能替代它。测试前提见[首页开发与验证](../README.md#开发与验证)。
