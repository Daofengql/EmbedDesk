# 发布约定

- 用户要求 Release 默认交付为精简的单 EXE，假定目标机器已安装 .NET 8 Desktop Runtime（x64）。默认使用 `dotnet publish src/EmbeddedDebugger.App/EmbeddedDebugger.App.csproj -c Release`，发布配置为 `Compact`，成品在 `artifacts/release/win-x64/EmbeddedDebugger.App.exe`。
- 默认包不携带 .NET 运行时。只有明确需要自带运行时版本时，才指定 `-p:PublishProfile=SingleFile`，输出到 `artifacts/release-self-contained/win-x64/`。SDK 的单文件压缩仅支持自包含发布，默认精简版不要开启该选项。
- 终端资源应嵌入程序并从用户缓存加载，不依赖 EXE 旁边的 Assets 文件夹。不要以普通 build 目录作为默认 Release 交付。目标系统仍需 WebView2 Runtime。
- 发布前验证单文件输出和终端初始化。不要覆盖正在运行的程序或中断用户连接；文件占用时发布到新的输出目录。
- Release 默认通过 `build/PackedDependencies.targets` 将完整的 BouncyCastle 依赖压缩嵌入基础设施程序集，首次使用 SSH / VNC 时按需加载；不裁剪密码算法。压缩结果按版本缓存到 obj，增量构建复用。排查加载问题时可用 `-p:PackManagedDependencies=false` 生成常规版本。此机制与 SDK 的单文件压缩选项独立。
