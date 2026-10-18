import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitepress'

const repository = 'https://github.com/Daofengql/EmbedDesk'
const base = process.env.DOCS_BASE ?? '/EmbedDesk/'
const docsRoot = fileURLToPath(new URL('..', import.meta.url))

if (!base.startsWith('/') || !base.endsWith('/')) {
  throw new Error('DOCS_BASE must start and end with /')
}

export default defineConfig({
  lang: 'zh-CN',
  title: 'EmbedDesk',
  description: 'EmbedDesk 中文使用指南：串口与 TCP/UDP 调试、PID、摄像头、远程终端、网络诊断和嵌入式工具。',
  base,
  cleanUrls: true,
  lastUpdated: true,
  head: [['link', { rel: 'icon', type: 'image/svg+xml', href: `${base}embeddesk.svg` }]],
  sitemap: { hostname: `https://daofengql.github.io${base}` },
  themeConfig: {
    logo: '/embeddesk.svg',
    siteTitle: 'EmbedDesk 文档',
    nav: [
      { text: '使用指南', link: '/guide/', activeMatch: '/guide/' },
      { text: '技术参考', link: '/reference/', activeMatch: '/(reference/|Architecture|DebugAssistantDesign|DebugTools|DeviceConnections|LanFiles|VirtualSerialPorts|ch347-spi-support|security-crypto|performance|SerialBridgeProtocol|third-party/)' },
      { text: '开发与贡献', link: '/development/', activeMatch: '/(development/|ProductRoadmap)' },
      { text: '下载', link: `${repository}/releases` }
    ],
    sidebar: [
      {
        text: '开始使用',
        items: [
          { text: '指南总览', link: '/guide/' },
          { text: '安装与快速开始', link: '/guide/getting-started' }
        ]
      },
      {
        text: '日常操作',
        items: [
          { text: '调试助手与会话', link: '/guide/debug-assistant' },
          { text: '串口调试', link: '/guide/serial' },
          { text: 'TCP / UDP 与 MQTT', link: '/guide/network' },
          { text: 'PID 与摄像头', link: '/guide/pid-camera' },
          { text: '连接中心与共享', link: '/guide/connection-center' },
          { text: '远程终端与文件', link: '/guide/remote' },
          { text: '网络诊断与测速', link: '/guide/network-tools' },
          { text: '局域网文件传输', link: '/guide/lan-files' },
          { text: '烧录、镜像与数据工具', link: '/guide/embedded-tools' },
          { text: '设置与配置管理', link: '/guide/settings' },
          { text: '常见问题', link: '/guide/faq' }
        ]
      },
      {
        text: '技术参考',
        collapsed: true,
        items: [
          { text: '参考索引与阅读说明', link: '/reference/' },
          { text: 'PID / 摄像头公共帧', link: '/reference/debug-protocol' },
          { text: 'PID 协议与报文示例', link: '/reference/pid-protocol' },
          { text: '摄像头协议与寄存器', link: '/reference/camera-protocol' },
          { text: '应用架构', link: '/Architecture' },
          { text: '调试助手设计', link: '/DebugAssistantDesign' },
          { text: 'PID / 图像协议与二进制编辑', link: '/DebugTools' },
          { text: '设备连接与安全边界', link: '/DeviceConnections' },
          { text: '局域网文件协议', link: '/LanFiles' },
          { text: '系统虚拟串口（实验性）', link: '/VirtualSerialPorts' },
          { text: 'CH347T 芯片支持', link: '/ch347-spi-support' },
          { text: '证书与密码算法', link: '/security-crypto' },
          { text: '资源与历史性能记录', link: '/performance' },
          { text: 'iperf3 协议与历史实测', link: '/third-party/iperf3-protocol' },
          { text: 'com0com 来源与许可', link: '/third-party/com0com' },
          { text: '旧 EDBG 协议（兼容）', link: '/SerialBridgeProtocol' },
          { text: '许可证与第三方声明', link: '/reference/licenses' }
        ]
      },
      {
        text: '开发与维护',
        collapsed: true,
        items: [
          { text: '构建与验证', link: '/development/' },
          { text: '发布 Windows EXE', link: '/development/releasing' },
          { text: '维护文档与 GitHub Pages', link: '/development/docs-site' },
          { text: '产品方向与待验收事项', link: '/ProductRoadmap' },
          { text: '自部署中继', link: 'https://daofengql.github.io/EmbedDesk.Relay/deployment/' }
        ]
      }
    ],
    outline: { level: [2, 3], label: '本页内容' },
    socialLinks: [{ icon: 'github', link: repository }],
    editLink: { pattern: `${repository}/edit/main/docs/:path`, text: '在 GitHub 上编辑本页' },
    lastUpdated: { text: '最后更新', formatOptions: { dateStyle: 'medium' } },
    docFooter: { prev: '上一篇', next: '下一篇' },
    darkModeSwitchLabel: '切换主题',
    lightModeSwitchTitle: '切换到浅色主题',
    darkModeSwitchTitle: '切换到深色主题',
    sidebarMenuLabel: '文档目录',
    returnToTopLabel: '回到顶部',
    skipToContentLabel: '跳到正文',
    externalLinkIcon: true,
    notFound: { title: '页面未找到', quote: '可以从使用指南或搜索继续查找。', linkLabel: '返回文档首页', linkText: '返回文档首页' },
    search: {
      provider: 'local',
      options: {
        miniSearch: {
          options: {
            // Keep this function self-contained: VitePress also serializes it for the browser.
            tokenize: (text) => Array.from(new Intl.Segmenter('zh-CN', { granularity: 'word' }).segment(text))
              .filter((part) => part.isWordLike)
              .map((part) => part.segment)
          },
          searchOptions: { prefix: true, fuzzy: 0.15 }
        },
        translations: {
          button: { buttonText: '搜索文档', buttonAriaLabel: '搜索文档' },
          modal: {
            displayDetails: '显示详细内容',
            resetButtonTitle: '清空搜索',
            backButtonTitle: '返回',
            noResultsText: '没有找到相关内容',
            footer: { selectText: '选择', navigateText: '切换', closeText: '关闭' }
          }
        }
      }
    },
    footer: { message: `原创内容采用 <a href="${repository}/blob/main/LICENSE">GPL-3.0-only</a>，第三方内容保留各自许可。` }
  },
  markdown: {
    config(md) {
      const renderImage = md.renderer.rules.image!
      md.renderer.rules.image = (tokens, index, options, env, self) => {
        const rendered = renderImage(tokens, index, options, env, self)
        const src = tokens[index].attrGet('src')
        if (!src?.startsWith('/images/')) return rendered
        const target = md.utils.escapeHtml(`${base.slice(0, -1)}${src}`)
        return `<a class="screenshot-link" href="${target}" target="_blank" rel="noopener" title="查看原图">${rendered}</a>`
      }
      const fence = md.renderer.rules.fence!
      md.renderer.rules.fence = (tokens, index, options, env, self) => {
        if (tokens[index].info.trim() === 'mermaid') {
          return `<MermaidDiagram :source="${md.utils.escapeHtml(JSON.stringify(tokens[index].content))}" />`
        }
        return fence(tokens, index, options, env, self)
      }

      // Technical Markdown keeps working on GitHub. Source files and license texts
      // outside the site's page set point to their canonical repository locations.
      md.core.ruler.after('inline', 'repository-links', (state) => {
        const currentFile = state.env.path ?? state.env.filePath
        if (!currentFile) return
        const visit = (tokens) => {
          for (const token of tokens) {
            if (token.type === 'link_open') {
              const href = token.attrGet('href')
              if (href && !/^(?:[a-z][a-z\d+.-]*:|\/|#)/i.test(href)) {
                const [pathname, suffix = ''] = href.split(/(?=[?#])/s, 2)
                const resolved = path.resolve(path.dirname(currentFile), decodeURI(pathname))
                const relative = path.relative(docsRoot, resolved).replaceAll('\\', '/')
                if (relative.startsWith('../') || pathname.endsWith('.txt')) {
                  const repoPath = path.relative(path.dirname(docsRoot), resolved).replaceAll('\\', '/')
                  token.attrSet('href', `${repository}/blob/main/${repoPath}${suffix}`)
                }
              }
            }
            if (token.children) visit(token.children)
          }
        }
        visit(state.tokens)
      })
    }
  }
})
