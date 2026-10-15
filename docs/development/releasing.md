# 发布 Windows EXE

用户默认交付使用 `Compact`：不携带 .NET 运行时的 Windows x64 精简单 EXE。目标系统仍需 .NET 8 Desktop Runtime x64 与 WebView2 Runtime。

## 发布配置

从仓库根目录执行：

```powershell
dotnet publish src/EmbedDesk.App/EmbedDesk.App.csproj -c Release
```

成品位于 `artifacts/release/win-x64/EmbedDesk.App.exe`。终端、图标及许可已嵌入，无需旁置 `Assets`。成品被占用时发布到新的输出目录，不结束用户程序。

明确需要自带 .NET 运行时才追加 `-p:PublishProfile=SingleFile`，输出到 `artifacts/release-self-contained/win-x64/`；该版本仍依赖 WebView2。

两种配置均不裁剪。SDK 单文件压缩仅用于自包含版本。Release 另通过 [PackedDependencies.targets](../../build/PackedDependencies.targets) 压缩嵌入完整 BouncyCastle，首次使用 SSH / VNC 时按需加载，增量构建复用版本缓存。排查依赖加载时可以使用 `-p:PackManagedDependencies=false`。

## 验证单文件

先单独发布烟雾程序：

```powershell
dotnet publish tests/EmbedDesk.App.SmokeTests/EmbedDesk.App.SmokeTests.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o artifacts/smoke-publish
```

将烟雾 EXE 单独复制到新建的隔离目录，在只有这个 EXE 的目录执行：

```powershell
./EmbedDesk.App.SmokeTests.exe --packaging-check
```

这会检查 WebView2、本地 Shell 和终端嵌入资源。还需要启动实际产品 EXE，确认终端初始化；普通 build 不能代表单文件通过。校验和使用 `Get-FileHash` 生成。

## GitHub Release

[Release 工作流](../../.github/workflows/release.yml) 在推送 `vMAJOR.MINOR.PATCH` 标签时运行，可带预发布后缀；也可手动指定已经存在的标签。它在 Windows x64 执行核心测试并发布精简单 EXE、SHA-256 与许可证说明。

工作流不创建或推送标签，不包含桌面、Go 或硬件验收。版本标签属于发布操作，准备时应核实版本、提交与验证记录。文档站由独立的 [Pages 工作流](../../.github/workflows/docs.yml)发布，不触发 EXE Release。
