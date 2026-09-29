# Import inspector dependency support review

Checked 2026-09-28 for the development-only private XLSX inspector introduced for issue #30.

## Selected dependency

- `DocumentFormat.OpenXml` 3.5.1, pinned in the inspector project and NuGet lockfiles.
- `DocumentFormat.OpenXml.Framework` 3.5.1 is resolved transitively. The SDK requests `System.IO.Packaging` 10.0.2, so the inspector pins a direct [`System.IO.Packaging` 10.0.12](https://www.nuget.org/packages/System.IO.Packaging/10.0.12) override to retain the current .NET 10 servicing patch. .NET 10 is an active LTS release under the [official .NET support policy](https://dotnet.microsoft.com/platform/support/policy/dotnet-core), and its package patch is rechecked through the live NuGet outdated/vulnerability/deprecation inventories rather than inferred from the target framework.
- The package is maintained in the official [`dotnet/Open-XML-SDK`](https://github.com/dotnet/Open-XML-SDK) repository. Version 3.5.1 is the current stable release listed by the project's [release history](https://github.com/dotnet/Open-XML-SDK/releases/tag/v3.5.1).
- The project publishes a community support route rather than a formal LTS lifecycle; see its [support policy](https://github.com/dotnet/Open-XML-SDK/blob/main/SUPPORT.md). Acceptance therefore requires confirming that the pinned release remains current, maintained, non-deprecated, and free of applicable known vulnerabilities.

The SDK is used only for local Office Open XML package parsing and generation. The inspector contains no network client APIs, does not invoke Excel automation, opens the source read-only, does not calculate formulas, and does not resolve external relationships. Formula-bearing or externally linked workbooks are rejected before structural evidence is generated.

On 2026-09-28, `dotnet package list` reported no vulnerable, deprecated, or outdated direct or transitive packages for the inspector after the packaging override was applied. This development-only dependency does not alter the production application's runtime or distribution boundary, so no LikeC4 model change is required.
