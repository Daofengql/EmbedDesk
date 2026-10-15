# Third-party notices

EmbedDesk's original code is licensed under GPL-3.0-only. Third-party code and assets retain their original licenses and copyright notices.

## Bundled terminal and platform assets

| Component | License | Local notice |
| --- | --- | --- |
| [xterm.js](https://github.com/xtermjs/xterm.js) | MIT | [LICENSE-xterm](src/EmbedDesk.App/Assets/Terminal/LICENSE-xterm) |
| xterm.js fit addon | MIT | [LICENSE-addon-fit](src/EmbedDesk.App/Assets/Terminal/LICENSE-addon-fit) |
| [Devicon](https://github.com/devicons/devicon) platform logos | MIT | [LICENSE.devicon](src/EmbedDesk.App/Assets/Platforms/LICENSE.devicon) |
| [Simple Icons](https://github.com/simple-icons/simple-icons) platform logos | CC0-1.0 | [LICENSE.simple-icons.md](src/EmbedDesk.App/Assets/Platforms/LICENSE.simple-icons.md) |
| [Octicons v19.0.0](https://github.com/primer/octicons/tree/v19.0.0) GitHub mark (`mark-github-16.svg`, rendered as a WPF Path) | MIT | [octicons-MIT.txt](docs/third-party/octicons-MIT.txt) |

The [platform icon notice](src/EmbedDesk.App/Assets/Platforms/NOTICE.md) records upstream revisions, rendering changes, and regeneration instructions. Logos identify the connected systems; trademarks belong to their respective owners.

## Design references

[ESPConnect](https://github.com/thelastoutpostworkshop/ESPConnect) is a separate MIT-licensed project whose information architecture inspired the ESP32 workspace grouping (device information, flashing, serial monitor, and session log). The ESP ROM transport in this repository is an independent C# implementation and does not bundle ESPConnect source, JavaScript, or assets.

## Embedded device protocol references

- Camera/PID compatibility framing is independently implemented from public protocol documentation. Only documentation was referenced; no third-party tool source, artwork, DLL or executable is included. Source links, document versions, checksum interpretation and verification limits are recorded in [debug tool implementation notes](docs/DebugTools.md).
- The ESP ROM UART implementation adapts protocol/register definitions and the ESP8266 erase-size workaround from [esptool v4.8.1](https://github.com/espressif/esptool/tree/v4.8.1), `esptool/loader.py` and `esptool/targets/*.py`: Copyright 2014–2023 Fredrik Ahlberg, Angus Gratton, Espressif Systems (Shanghai) CO LTD and other contributors. License: [GPL-2.0-or-later](docs/third-party/esptool-GPL-2.0-or-later.txt), compatible with this project's GPL-3.0 distribution. No esptool executable, Python package or binary loader stub is bundled.
- STC89 framing, baud negotiation, code-area erase, programming and option preservation are adapted from [stcgal](https://github.com/grigorig/stcgal), `stcgal/protocols.py` and `stcgal/models.py`. Copyright © 2013–2015 Grigori Goronzy; [MIT notice](docs/third-party/stcgal-MIT.txt).
- CH347 HID framing references [CH347-HIDAPI](https://github.com/i2cy/CH347-HIDAPI/tree/be30eef5aa1881d476bb63bdebfdff957655e183), copyright © 2022 Icy Cloud; [MIT notice](docs/third-party/CH347-HIDAPI-MIT.txt). SPI command fields were cross-checked against flashrom (GPL-2.0-or-later) and Winbond documentation. Exact revisions and implementation boundaries are recorded in [CH347 protocol support](docs/ch347-spi-support.md). No WCH DLL, flashrom or SNANDer executable is bundled.

These notices and their local license files are also embedded in the application assembly under `ThirdParty/` for single-EXE distribution.

The managed iperf3 TCP/UDP implementation adapts protocol, pacing, interval statistics, UDP loss/jitter and test lifecycle logic from [ESnet iperf 3.17.1](https://github.com/esnet/iperf/tree/3.17.1). Copyright (c) 2014–2024, The Regents of the University of California, through Lawrence Berkeley National Laboratory; [BSD-style upstream LICENSE](docs/third-party/iperf3-LICENSE.txt). The C# implementation uses .NET sockets and timers without an external executable or native library. Source mappings and interoperability limits are recorded in [iperf3 protocol notes](docs/third-party/iperf3-protocol.md).

## Application dependencies

The optional system virtual COM adapter uses definitions and extended IOCTL layouts adapted from com0com, Copyright (c) 2008 Vyacheslav Frolov, under [GPL-2.0-or-later](docs/third-party/com0com-GPL-2.0-or-later.txt). Exact source revision, referenced files and implementation boundaries are recorded in [com0com interface notes](docs/third-party/com0com.md). The driver and its installation utilities are installed separately and are not bundled.

The independently built Go relay in [server/relay](server/relay/README.md) uses Gin, GORM, the pure-Go glebarez/modernc SQLite implementation, Gorilla WebSocket, and golang.org/x/time. Exact default Windows/Linux runtime module versions and unchanged upstream license texts are indexed in [relay license notices](server/relay/licenses/NOTICE.md). They include MIT, Apache-2.0, BSD and SQLite public-domain notices as provided upstream. The relay embeds the notices, original project GPL-3.0 license, and Go standard library license using `go:embed`; `embeddesk-relay --licenses` prints the bundled texts. These Go libraries are part of the separate server binary, not additional desktop NuGet or runtime dependencies.

NuGet restores these dependencies during the build. Direct versions are recorded in [Directory.Packages.props](Directory.Packages.props); restored package metadata also records transitive dependencies and their licenses.

| Component | License |
| --- | --- |
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.0) | MIT |
| [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/8.0.1) and its abstractions | MIT |
| [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/8.0.3) | MIT |
| [System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports/8.0.0), [System.Management](https://www.nuget.org/packages/System.Management/8.0.0), and System.CodeDom | MIT |
| [SSH.NET](https://www.nuget.org/packages/SSH.NET/2026.0.0) | MIT |
| [BouncyCastle.Cryptography](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0) (SSH.NET, standard VNC password authentication, X.509/PKCS#12, and bundled digest, signature, and symmetric/asymmetric cipher implementations) | MIT |
| [AWS SDK for .NET](https://www.nuget.org/packages/AWSSDK.S3/4.0.104) (`AWSSDK.S3` 4.0.104 and transitive `AWSSDK.Core` 4.0.102.8) | Apache-2.0; see [license text](docs/licenses/Apache-2.0.txt) |
| [Microsoft.Web.WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47) | BSD-3-Clause; see [LICENSE](docs/licenses/WebView2-LICENSE.txt) and [NOTICE](docs/licenses/WebView2-NOTICE.txt) from the package |

The separately installed Microsoft Edge WebView2 Runtime is governed by its own distribution terms; it is not bundled in the single-EXE output. The self-contained publish profile bundles the .NET runtime; retain its applicable licenses and third-party notices when distributing binaries. This dependency overview does not replace the notices supplied by individual packages.

RDP integration uses the MSTSC ActiveX control supplied by Windows (`mstscax.dll`). This Windows component is loaded from the operating system and is not redistributed with this project. The VNC RFB protocol client is original project code; it does not bundle a third-party VNC viewer.

Test dependencies include Microsoft.NET.Test.Sdk, xUnit, and its Visual Studio runner, under their respective package licenses. The optional Python SSH regression harness requires Paramiko, which is installed separately and is not bundled with the application.
