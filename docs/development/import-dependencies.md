# Production import dependency support review

Checked 2026-09-29 for the production Emma XLSX adapter introduced under issue #30.

## Runtime and SDK

- The repository pins .NET SDK 10.0.401 and targets .NET 10. The installed runtime/package patch is 10.0.12.
- The official [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) lists .NET 10 as an active LTS release, with 10.0.12 as the current patch and support through 2028-11-14.
- `System.IO.Packaging` is pinned directly to 10.0.12 so the adapter does not resolve the older 10.0.2 minimum requested transitively by the Open XML SDK.

## Open XML SDK

- `DocumentFormat.OpenXml` 3.5.1 is pinned in the production importer, the test fixture project, the development inspector, and their lockfiles.
- The official [`dotnet/Open-XML-SDK` release list](https://github.com/dotnet/Open-XML-SDK/releases) identifies 3.5.1 as the latest stable release.
- The project has a community support route rather than a formal LTS lifecycle; see its [support policy](https://github.com/dotnet/Open-XML-SDK/blob/main/SUPPORT.md). Acceptance therefore depends on the pinned version remaining current, maintained, non-deprecated, and free of applicable known vulnerabilities.
- The adapter opens an owner-supplied in-memory package read-only, rejects active/external content and formulas, and performs no Office automation, formula evaluation, networking, or native execution.

## Live package inventories

On 2026-09-29:

- `DotFinance.Import` and `DotFinance.Import.Tests` had no outdated direct dependencies.
- No production importer direct or transitive package was reported deprecated or vulnerable.
- The repository-wide inventory reported unrelated newer SkiaSharp/HarfBuzzSharp releases and newer transitive test-platform packages. Those packages are outside the importer boundary; none was reported deprecated or vulnerable. They are retained for a separately scoped compatibility update rather than silently changed in issue #30.
