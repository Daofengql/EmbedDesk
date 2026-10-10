# Platform icons

The tab icons use the original operating system logos, rendered as transparent 96 px PNGs for WPF. SVG sources are retained here.

- Apple, Arch Linux, CentOS, Debian, Fedora, Linux, openSUSE, Red Hat, Rocky Linux, and Windows: [Devicon](https://github.com/devicons/devicon/tree/7330accdbc47e2dc0c19789a48533c4a3c50fe58/icons), revision `7330accdbc47e2dc0c19789a48533c4a3c50fe58`. MIT license, reproduced in `LICENSE.devicon`.
- Alpine Linux, FreeBSD, and Ubuntu: [Simple Icons](https://github.com/simple-icons/simple-icons/tree/98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d/icons), revision `98820a4dc8c363ca72fa2c0d294ea4a0a9bba75d`. CC0 license, reproduced in `LICENSE.simple-icons.md`.
- The generic unknown-system monitor icon was drawn for Embedded Debugger.

Apple and Alpine use a light monochrome rendering for dark backgrounds. FreeBSD uses its red brand color. The logos identify the connected systems; trademarks belong to their respective owners.

To regenerate the PNG files with resvg 2.6.2, run from the repository root:

```powershell
npm.cmd install --prefix "$env:TEMP\embedded-debugger-icon-tools" --no-audit --no-fund @resvg/resvg-js@2.6.2
node scripts/Generate-PlatformIcons.cjs "$env:TEMP\embedded-debugger-icon-tools\node_modules\@resvg\resvg-js"
```
