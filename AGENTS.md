# 发布约定

- 用户要求 Release 尽量交付为单 EXE。默认使用 `dotnet publish src/EmbeddedDebugger.App/EmbeddedDebugger.App.csproj -c Release`，发布配置为 `SingleFile`，成品在 `artifacts/release/win-x64/EmbeddedDebugger.App.exe`。
- 默认包自带 .NET 运行时；终端资源应嵌入程序并从用户缓存加载，不依赖 EXE 旁边的 Assets 文件夹。不要以普通 build 目录作为默认 Release 交付。
- 发布前验证单文件输出和终端初始化。不要覆盖正在运行的程序或中断用户连接；文件占用时发布到新的输出目录。
