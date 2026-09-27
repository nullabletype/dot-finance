# ADR-0003: Production project boundaries

- **Status:** Accepted — owner approved on 2026-09-27
- **Date:** 2026-09-27
- **Issue:** #27

## Context and recommendation

Build a single-process, offline desktop application with inward-facing dependencies. Separate the financial domain, application coordination, and external adapters so financial rules can be verified without Avalonia, a database, or operating-system services. Keep the disposable prototype independent.

## Project responsibilities

| Project | Responsibility | Allowed production project references |
| --- | --- | --- |
| `DotFinance.Domain` | Financial concepts, calculations, reconciliation transitions and invariants; no I/O | None |
| `DotFinance.Application` | Coordinates use cases; owns the interfaces it needs for imports, vault access, settings and platform services | Domain |
| `DotFinance.Desktop` | Avalonia views/presentation, native lifecycle and composition root; desktop dialogs and UI dispatch | Application; concrete adapters at composition only when introduced |
| `DotFinance.Import` (issue #30) | Validates the verified CSV contract and returns import records; does not decide allocations or settlement | Application, Domain |
| `DotFinance.Storage` (issue #29) | Encrypted SQLite3MC persistence, migration and atomic backup/restore | Application, Domain |
| `DotFinance.Settings` (issue #31) | Local non-sensitive preferences such as the theme; never financial state or passwords | Application |

Use `src/` for production and `tests/` for production checks, with a root `DotFinance.slnx`. Issue #27 creates only Domain, Application, Desktop and focused architecture checks. Adapter projects are added with their implementations in the owning issues. Do not add placeholder persistence operations, speculative financial models, or interfaces without a consumer.

Application-owned ports describe use-case needs, not database tables, Avalonia controls or CSV-library types. Domain-owned types describe financial meaning. CSV syntax belongs to Import; reconciliation eligibility and correction decisions belong to Domain. Application coordinates those boundaries. Versioned persistence contracts and recovery semantics are decided under #29 rather than inferred from prototype JSON.

The Desktop composition root constructs concrete services and supplies them to application/presentation consumers through interfaces. Keep platform-specific UI operations in Desktop; introduce an application port only when an application use case needs one. Avoid a catch-all platform project or a dependency-injection framework until there is a concrete need.

## Distribution constraint

The owner requires one self-contained distributed executable for each supported platform/CPU target, without separately shipped managed DLLs or application-native library sidecars. Separate source projects enforce responsibilities; they do not imply separate distribution files. Remove unused code where safely supported (trimming/tree shaking), and avoid a prerequisite .NET installation.

On 2026-09-27 the owner explicitly approved **single-file bundling with private runtime extraction** after reviewing the Native AOT feasibility results. Use .NET self-contained single-file publishing, trimming and `IncludeNativeLibrariesForSelfExtract`. This replaces the initial preference for fully statically linked Native AOT: the executable contains managed code and runtime components, and extracts bundled native libraries into the runtime's per-user cache. It is not described as a fully native AOT binary. See [the feasibility record](../development/single-executable-feasibility.md).

Keep production code compatible with trimming: prefer compiled bindings, embedded assets and explicit service composition. Investigate trimming warnings rather than suppressing them broadly or rooting whole assemblies by default. Measure published size and exercise the published application, not only the development build. Native AOT may be reconsidered later if a supported, reproducible static dependency route justifies its maintenance cost.

Build checks require exactly one application file in the publish directory. Native smoke checks copy that executable alone into a fresh directory, launch it with an isolated extraction cache, and bind launch evidence to the tested commit and executable hash. No financial data or password belongs in the extraction cache. Normal runtime extraction relies on an owner-controlled, writable per-user location; do not point it at a shared writable directory. Read-only/denied extraction locations and final macOS application-bundle/signing choices remain packaging checks under #32. A Finder-visible `.app` must not be misrepresented as one physical file.

Issue #27 proves the minimal shell on supported native runners. SQLite3MC integration and its single-file runtime behaviour still require validation under #29; final installers, signing and release-candidate validation remain under #32.

Sources checked on 2026-09-27:

- [Avalonia Native AOT guidance](https://docs.avaloniaui.net/docs/deployment/native-aot)
- [.NET single-file deployment and native-library extraction](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [.NET Native AOT native interop and static linking](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)

## Enforcement and offline boundary

Architecture checks enforce the allowed reference graph, reject production references to `prototypes/`, and reject Avalonia/SQLite dependencies in Domain and Application. When adapters are introduced, extend checks to keep concrete adapter usage in Desktop confined to composition. Project-graph checks alone cannot establish that restriction inside an assembly.

The shipped app has no HTTP services, telemetry, updater, financial-institution integration or hosted component. Build tooling may access registries and GitHub. Runtime network monitoring remains a separate validation gate: a reference check cannot prove no outbound attempts.

Keep experimental LikeC4 views. Add clearly labelled production foundation views as code is implemented; mark future adapters as planned rather than implemented. Record native launch evidence for macOS, Windows and Linux against the actual tested commit. Missing platform checks remain pending.

## Alternatives and trade-offs

A single production assembly would be simpler initially but would leave financial isolation dependent on convention. A project for every interface or platform capability would add ceremony before the relevant contracts exist. Three initial production projects provide enforceable inward dependencies, while feature-specific adapters can be added when their behaviour is agreed.

A separate bootstrap executable could prevent all Desktop references to concrete adapters, but adds another assembly for a restriction that focused composition checks can enforce. Keep composition in Desktop unless later evidence justifies that split.

## Acceptance boundary

Acceptance approves these responsibilities and dependency rules. The single-executable distribution constraint above is required. Acceptance does not otherwise approve production financial, importer, persistence, password, signing, installer or release policies. Those retain their issue-specific gates. Issue #27 must verify supported dependency versions, build/format/architecture checks, native launch across the supported platforms, and independent review before acceptance of its implementation.
