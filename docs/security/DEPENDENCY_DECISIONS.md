# Dependency decisions (supply chain)

| Date | Package | Decision | Reason |
|---|---|---|---|
| 2026-11-09 | PDFsharp-MigraDoc **6.2.4** (pinned) | Use the last MIT-licensed release; **do not upgrade to 6.3+** without owner/counsel decision | 6.5.0 nuspec declares `AGPL-3.0-only` and enforces a runtime license check (`PdfSharpLicenseException`). AGPL is incompatible with a proprietary product without a commercial license. Guarded by `DependencyLicenseTests`. |
| 2026-11-09 | System.Security.Cryptography.Pkcs **10.0.14** (transitive pin) | Override PDFsharp's transitive 8.0.1 | GHSA-846f-f764-76mx (high), GHSA-2fmv-rv2c-59x7 (moderate) reported by NuGet audit. `CentralPackageTransitivePinningEnabled`. |
| 2026-11-09 | minimatch **11.0.5** (npm override) | Override dev-only transitive 10.2.6 | GHSA-g3hv-75wg-m249 (moderate, ReDoS) in ESLint toolchain; not shipped to browsers. |
| 2026-11-09 | Noto Sans / Noto Sans Ethiopic (fonts, OFL-1.1) | Vendored from Ubuntu `fonts-noto-core` 20201225-2 | Reproducible PDF rendering incl. Ge'ez script; OFL permits embedding. |
| 2026-11-09 | QRCoder 1.8.1 (MIT) | Use | QR generation without System.Drawing. |
| 2026-11-09 | .NET SDK 10.0.114 (Ubuntu noble-updates) | Use; `global.json` pins 10.0.114 roll-forward latestFeature | Microsoft download host blocked in build sandbox; distro build is source-built .NET. CI uses actions/setup-dotnet. |

Full resolved NuGet inventory: `THIRD_PARTY_NUGET.md` (generated). npm inventory: `src/web/package-lock.json`.
