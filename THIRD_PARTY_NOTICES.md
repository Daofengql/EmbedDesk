# Third-party notices

Embedded Debugger's original code is licensed under GPL-3.0-only. Third-party code and assets retain their original licenses and copyright notices.

## Bundled terminal and platform assets

| Component | License | Local notice |
| --- | --- | --- |
| [xterm.js](https://github.com/xtermjs/xterm.js) | MIT | [LICENSE-xterm](src/EmbeddedDebugger.App/Assets/Terminal/LICENSE-xterm) |
| xterm.js fit addon | MIT | [LICENSE-addon-fit](src/EmbeddedDebugger.App/Assets/Terminal/LICENSE-addon-fit) |
| [Devicon](https://github.com/devicons/devicon) platform logos | MIT | [LICENSE.devicon](src/EmbeddedDebugger.App/Assets/Platforms/LICENSE.devicon) |
| [Simple Icons](https://github.com/simple-icons/simple-icons) platform logos | CC0-1.0 | [LICENSE.simple-icons.md](src/EmbeddedDebugger.App/Assets/Platforms/LICENSE.simple-icons.md) |

The [platform icon notice](src/EmbeddedDebugger.App/Assets/Platforms/NOTICE.md) records upstream revisions, rendering changes, and regeneration instructions. Logos identify the connected systems; trademarks belong to their respective owners.

## Application dependencies

NuGet restores these dependencies during the build. Direct versions are recorded in [Directory.Packages.props](Directory.Packages.props); restored package metadata also records transitive dependencies and their licenses.

| Component | License |
| --- | --- |
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.0) | MIT |
| [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/8.0.1) and its abstractions | MIT |
| [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/8.0.3) | MIT |
| [System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports/8.0.0), [System.Management](https://www.nuget.org/packages/System.Management/8.0.0), and System.CodeDom | MIT |
| [SSH.NET](https://www.nuget.org/packages/SSH.NET/2026.0.0) | MIT |
| [BouncyCastle.Cryptography](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0) (SSH.NET dependency) | MIT |
| [Microsoft.Web.WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47) | BSD-3-Clause; see [LICENSE](docs/licenses/WebView2-LICENSE.txt) and [NOTICE](docs/licenses/WebView2-NOTICE.txt) from the package |

The separately installed Microsoft Edge WebView2 Runtime is governed by its own distribution terms; it is not bundled in the single-EXE output. The self-contained publish profile bundles the .NET runtime; retain its applicable licenses and third-party notices when distributing binaries. This dependency overview does not replace the notices supplied by individual packages.

Test dependencies include Microsoft.NET.Test.Sdk, xUnit, and its Visual Studio runner, under their respective package licenses. The optional Python SSH regression harness requires Paramiko, which is installed separately and is not bundled with the application.
