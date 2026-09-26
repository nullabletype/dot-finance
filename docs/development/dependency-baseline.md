# Supported dependency baseline

Checked 2026-09-26 for the issue #3 prototype. This records selection evidence; the lockfiles remain the reproducible dependency authority.

## Runtime and build tooling

| Component | Selected version | Evidence and support conclusion |
| --- | --- | --- |
| .NET SDK | 10.0.401 | `global.json` pins the installed stable SDK feature band. [.NET 10 is active LTS, latest runtime patch 10.0.12, supported through 2028-11-14](https://dotnet.microsoft.com/en-us/platform/support/policy). Local runtime is 10.0.12. |
| Node.js in CI | 24.21.0 | Exact LTS patch pinned in the workflow. [Node 24 is LTS and supported through April 2028](https://nodejs.org/en/about/previous-releases). `package.json` excludes EOL Node 25. |
| LikeC4 | 1.59.4 | Exact current dev-dependency and lockfile pin. `npm outdated --long` returned no available update after refresh. This is build tooling only, not shipped runtime code. |
| GitHub runner images | macOS 15, Windows 2025, Ubuntu 24.04 | Explicit GA labels replace drifting `*-latest` labels. All three are listed by the [official runner-images inventory and support policy](https://github.com/actions/runner-images). |

## Application and test packages

| Package | Selected version | Evidence and conclusion |
| --- | --- | --- |
| Avalonia Desktop / Fluent | 12.1.3 | Current stable release on the [official Avalonia releases page](https://github.com/AvaloniaUI/Avalonia/releases) and [NuGet](https://www.nuget.org/packages/Avalonia.Desktop/12.1.3). |
| CsvHelper | 33.1.0 | Current according to the NuGet outdated query. |
| Microsoft.Data.Sqlite.Core | 10.0.12 | Matches the current .NET 10 patch line. |
| SQLite3MC.PCLRaw.bundle | 2.4.0 | Current according to the NuGet outdated query; selected and documented by ADR-0001. |
| Microsoft.NET.Test.Sdk | 18.10.1 | Current stable release shown by [NuGet](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.10.1). |
| xUnit v3 | 4.0.1 | Current stable release shown by [NuGet](https://www.nuget.org/packages/xunit.v3/4.0.1). |
| xunit.runner.visualstudio | 4.0.0 | Current according to the NuGet outdated query. |

The .NET 10 native Microsoft Testing Platform runner is selected in `global.json`. This is required by the current test stack; the former VSTest command mode is rejected by Microsoft.Testing.Platform 2.4 on .NET 10.

The current Avalonia and xUnit dependency graphs intentionally resolve some transitives below unrelated newer major releases, including SkiaSharp, HarfBuzzSharp, and Microsoft.ApplicationInsights. Those versions are constrained by the current top-level packages rather than independently selected. NuGet reports none of the complete direct/transitive graph as deprecated or vulnerable. Overriding those transitives independently would leave the tested upstream dependency graph and is not justified without an applicable advisory.

## GitHub Actions

The workflow pins immutable commit SHAs and records their release tags:

- `actions/checkout` v7.0.1 (`3d3c42e5aac5ba805825da76410c181273ba90b1`)
- `actions/setup-dotnet` v6.0.0 (`a98b56852c35b8e3190ac28c8c2271da59106c68`)
- `actions/setup-node` v7.0.0 (`820762786026740c76f36085b0efc47a31fe5020`)
- `actions/upload-artifact` v7.0.1 (`043fb46d1a93c77aae656e7c1c64a875d1fc6a0a`)
- `actions/download-artifact` v8.0.1 (`3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c`)

These are the current official releases as checked against the projects' release/tag references. Current action lines use the supported Node 24 action runtime; for example, [setup-node v7 documents its current runtime and migration](https://github.com/actions/setup-node) and [upload-artifact v7.0.1 is the latest release](https://github.com/actions/upload-artifact/releases).

## Commands run

```text
dotnet package list --project prototypes/DesktopShell/DesktopShell.csproj --outdated --include-transitive
dotnet package list --project prototypes/DesktopShell.Tests/DesktopShell.Tests.csproj --outdated --include-transitive
dotnet package list --project prototypes/DesktopShell.Tests/DesktopShell.Tests.csproj --deprecated --include-transitive
dotnet package list --project prototypes/DesktopShell.Tests/DesktopShell.Tests.csproj --vulnerable --include-transitive
npm outdated --long
```

Results: no outdated top-level NuGet or npm dependencies; no deprecated NuGet packages; no known vulnerable NuGet packages. Re-run these checks before acceptance because registry and support status can change independently of the source commit.
