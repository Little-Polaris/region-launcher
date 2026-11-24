# Sources

Windows inherited proxy interfaces:
https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpgetieproxyconfigforcurrentuser
https://learn.microsoft.com/en-us/windows/win32/api/wininet/nf-wininet-internetopena
Chromium system proxy reader (uses the public WinHTTP user proxy query):
https://raw.githubusercontent.com/chromium/chromium/main/net/proxy_resolution/win/proxy_config_service_win.cc

Windows DNS API contracts used by the unified prototype:
https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsqueryex
https://learn.microsoft.com/en-us/windows/win32/api/windns/ns-windns-dns_query_request
https://learn.microsoft.com/en-us/windows/win32/api/windns/ns-windns-dns_query_request3
https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsqueryraw
https://learn.microsoft.com/en-us/windows/win32/api/windns/ns-windns-dns_query_raw_request
Reference for DNS_ADDR_ARRAY construction and asynchronous completion lifetime:
https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/DNSAsyncQuery/cpp/DnsQueryEx.cpp

Mihomo independent executable: https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.33
Official archive: mihomo-windows-amd64-compatible-v1.19.33.zip.
SHA-256: 7aaf636a4f2172dcd8ec34ac4f52797b059195e5afd6409508c963e545f8864d (verified against the official release asset digest).
Unmodified executable, GPL-3.0; license included in app/proxy/LICENSE.
Corresponding source: https://github.com/MetaCubeX/mihomo/tree/v1.19.33
DNS forwarding documentation: https://wiki.metacubex.one/config/dns/

ProxyBridge: https://github.com/InterceptSuite/ProxyBridge
Commit: 2eb007671c82c972821e8e9bd404bc82499ebb4f. Windows core, MIT license.

Microsoft Detours: https://github.com/microsoft/Detours
Commit: 1dbd4ea281d73d478aa8d09a8b83fbd265f715f7. Instrumentation library, MIT license.

WinDivert: https://github.com/basil00/WinDivert/releases/tag/v2.2.2
Package: WinDivert-2.2.2-A.zip. Distributed unchanged with its licenses.
SHA-256: 63cb41763bb4b20f600b6de04e991a9c2be73279e317d4d82f237b150c5f3f15.
Corresponding source: https://github.com/basil00/WinDivert/tree/v2.2.2

The launcher and its modifications are experimental. Upstream compatibility claims do not constitute validation of this launcher or of Claude Desktop.


## Windows 后台服务与参数枚举

- UI controls: AntdUI 2.4.20, https://www.nuget.org/packages/AntdUI/2.4.20 ; Apache-2.0. Source: https://gitee.com/AntdUI/AntdUI , commit 81732455f15345e4d919eb412e1bd7a721bac674 (from NuGet metadata). License text retained in vendor/antdui/LICENSE.txt and packaged licenses/AntdUI-Apache-2.0.txt.
- YAML parser: YamlDotNet 18.1.0, https://www.nuget.org/packages/YamlDotNet/18.1.0 ; MIT. NuGet metadata source commit: 748334a8fa7c227740018b284b71ad95cc6b7fc7. License retained in vendor/yamldotnet/LICENSE.txt and packaged licenses/YamlDotNet-MIT.txt.
- Subscription formats: https://github.com/MetaCubeX/Meta-Docs/blob/main/docs/config/proxy-providers/content.en.md . Parsing is local; no online conversion service is used.

- Service entry point: https://learn.microsoft.com/en-us/windows/win32/services/service-entry-point
- Service security: https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights
- Explicit handle inheritance: https://learn.microsoft.com/en-us/windows/win32/procthread/creating-processes
- Windows ICU mapping: https://devblogs.microsoft.com/oldnewthing/?p=105255
- Windows geographical information: https://learn.microsoft.com/en-us/windows/win32/api/winnls/ne-winnls-sysgeotype
- Google 官方 Chrome for Testing: https://googlechromelabs.github.io/chrome-for-testing/ 。本轮测试工具为 157.0.8103.38/win64，归档 SHA256 AE0DEEF27D449E1D7529DF7705D609BAB102DE48464DA13567ED3DD2EE1A0586；文件未带 Authenticode 签名，来自官方 HTTPS 下载地址。仅作为独立测试浏览器，不包含在源码包。
