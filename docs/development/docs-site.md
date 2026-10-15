# 维护文档与 GitHub Pages

文档使用 VitePress，Markdown 源文件位于 `docs/`。已有技术专题保留原路径，新用户步骤放在 `guide/`，构建与维护放在 `development/`，协议和索引放在 `reference/`。

## 本地预览

安装 Node.js 22.12 或更新版本，推荐与 CI 一致的 Node.js 24。在仓库根目录运行：

```powershell
npm ci
npm run docs:dev
```

打开终端打印的地址，默认项目路径为 `/EmbedDesk/`。验证发布产物时使用：

```powershell
npm run docs:build
npm run docs:preview
```

构建会检查站内页面链接，产物在 `docs/.vitepress/dist/`。`node_modules/`、缓存与产物均被 Git 忽略。Node.js、VitePress 与 Mermaid 只用于文档开发构建，不是桌面 EXE 的运行依赖。

## 发布到 GitHub Pages

1. 在仓库 Settings → Pages → Build and deployment 中将 Source 选择为 **GitHub Actions**。
2. 将文档和锁文件推送到 `main`；修改文档、依赖或工作流会自动运行 `Documentation`。
3. 等待构建和部署完成，访问 [EmbedDesk 文档](https://daofengql.github.io/EmbedDesk/)。

Pull Request 只构建，不部署；也可以在 Actions 手动运行。工作流使用官方 Pages artifact 和部署动作，不需要 `gh-pages` 分支或个人访问令牌。页面不能访问时先检查 Pages 设置及 Actions 的部署结果。

站点默认 base 为 `/EmbedDesk/`。改为用户站点、自定义域名或不同仓库名时，在本地和 CI 中一致设置 `DOCS_BASE`，且必须以 `/` 开始和结束。例如根域名部署：

```powershell
$env:DOCS_BASE = '/'
npm run docs:build
```

同时修改配置中的 sitemap hostname 及本页地址，按 Pages 设置配置自定义域名。默认工作流针对当前仓库项目路径。

## 添加或修改页面

- 新增操作页面时在 `.vitepress/config.mts` 添加侧栏入口，正文使用相对 Markdown 链接，保持 GitHub 上也可阅读。
- 图片放到 `docs/public/images/`，用 `/images/文件名.png` 引用；VitePress 会加上 base。添加有内容的替代文字和紧邻步骤的图注。
- 指南截图可点击在新标签查看原图；宽结构图保留可读字号，在图表区域内横向滚动。
- 技术文档到源码、站外 Markdown 和许可证原文的相对链接，在网站构建时转成 GitHub 源文件地址；站内专题仍保留本地导航。
- Mermaid 围栏会在浏览器按主题渲染结构图。站点使用本地搜索和中文分词，不依赖外部搜索账号、字体或统计服务。

## 截图素材

指南截图来自产品 WPF 窗口，优先使用隔离设置、模拟设备和回环通信。检查工具见 [Smoke 项目](../../tests/EmbedDesk.App.SmokeTests/Program.cs)：`--debug-connections-check`、`--debug-tools-check`、`--connection-center-window-check` 等会在系统临时目录生成 PNG。

先单独构建 Smoke，再执行入口，避免旧界面混入新指南。只选用当前产品正常入口的截图，不使用旧兼容窗口。逐张检查账号、密码、路径、设备信息和内网拓扑后再收录。素材说明见[图片索引](../reference/screenshots.md)。不要对用户正在使用的连接做破坏性操作。

## 提交前检查

运行构建并修复死链。在真实浏览器检查首页、文章导航、中文搜索、深浅主题、图表和图片；同时检查桌面与手机宽度，确认正文和操作菜单没有被遮挡。生成目录、个人配置、缓存和截图检查日志不作为源码提交。
