# Single-executable feasibility — issue #27

## Decision and scope

On 2026-09-27 the owner accepted ADR-0003, including one native application executable per supported platform/CPU target, no separately shipped application libraries, and trimming where safely supported. The accepted ADR and specification are recorded in `c041155`.

The disposable `prototypes/SingleExecutableProbe` isolates the native distribution question before production scaffolding. It opens a small Avalonia window with synthetic text and exits after its native `Opened` callback reaches application idle. It has no financial state, importer or storage. The callback is launch evidence, not a screenshot, accessibility review or full rendering test.

## Initial findings

A scratch probe on macOS 27 / Apple Silicon with SDK 10.0.401, runtime 10.0.12 and Avalonia 12.1.3 produced a native Mach-O executable plus `libAvaloniaNative.dylib`, `libHarfBuzzSharp.dylib` and `libSkiaSharp.dylib`. It launched with those libraries. Copying only the executable to an isolated directory failed with `DllNotFoundException` for `libSkiaSharp`. `otool -L` alone did not reveal that dependency because the library is loaded dynamically.

The same scratch source published with Native AOT disabled, self-contained single-file publishing, trimming and native-library extraction produced one distributable executable. That file launched in an isolated directory and extracted the three native libraries into a controlled local directory. This proves a concrete alternative for the tested macOS environment, but it is not a fully native AOT binary. The owner subsequently approved this distribution approach.

The checked-in source for reproducing these probes is commit `016d078`. The initial scratch output was 14,822,632 bytes for the AOT executable (plus three sidecars), and 38,550,918 bytes for the single-file bundle. These are scratch-probe measurements, not production release sizes. No binary or extracted library is checked in or uploaded as an Actions artifact.

## Static-link route

The [StaticLink.Avalonia maintainer's compatibility table](https://github.com/greepar/StaticLink.Avalonia) describes static Avalonia/SkiaSharp/HarfBuzz integration for Windows, Linux and macOS. This establishes a plausible investigation route, not production compatibility or upstream Avalonia support for those third-party binaries.

Live NuGet indexes checked on 2026-09-27 contained `StaticLink.Avalonia 3.119.4-7922.1`, but the macOS `StaticLink.Avalonia.Native` index ended at `12.1.2.1`; no matching 12.1.3 package was present. Its maintainer requires the native package version to match Avalonia. Do not silently downgrade Avalonia or mix those versions. The graphics packages also use prerelease-form version strings and have not passed this repository's stable/support acceptance policy.

Independent source review found moving graphics-source/tool references, package-injected macOS font/application initialisation, Linux duplicate-symbol allowances, and deletion of dynamic files after publishing. Adopting the integration requires source/toolchain pins, provenance and behaviour review; an empty sidecar list does not prove the executable works. See the [macOS graphics build](https://github.com/greepar/StaticLink.Avalonia/blob/main/scripts/build-macos-static-graphics.sh), [native build](https://github.com/greepar/StaticLink.Avalonia/blob/main/scripts/build-macos-avalonia-native-static.sh), and [graphics targets](https://github.com/greepar/StaticLink.Avalonia/blob/main/NuGet/StaticGraphics/buildTransitive/StaticLink.Avalonia.targets).

The local machine has Command Line Tools but not an active full Xcode installation: `xcodebuild -version` reports that Xcode is required. The inspected macOS native static-build script uses `xcodebuild`. A matching-source build needs an appropriate local or hosted toolchain; this does not establish that static linking is impossible.

SQLite3MC's desktop NuGet binaries are shared libraries. Its upstream source provides a static-build route. Independent review identified that upstream 2.5.0 fixed exported AEGIS/Ascon symbol collisions, potentially relevant to static linkage; 2.5.1 was the latest source release checked. Reassess native/source pins and binding compatibility under #29 instead of assuming the prototype's 2.4.0 package settles the static-build choice. See [SQLite3MC releases](https://github.com/utelle/SQLite3MultipleCiphers/releases) and [build guidance](https://utelle.github.io/SQLite3MultipleCiphers/docs/installation/install_overview/).

## Reproduction

Run from the repository root on macOS arm64. These commands build a disposable probe, not the production application. The two modes run sequentially because they share intermediate output.

```sh
dotnet publish prototypes/SingleExecutableProbe/Probe.csproj -c Release -r osx-arm64 -p:RestoreLockedMode=true -o artifacts/single-executable/aot
artifacts/single-executable/aot/Probe

dotnet publish prototypes/SingleExecutableProbe/Probe.csproj -c Release -r osx-arm64 --no-restore -p:PublishAot=false -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o artifacts/single-executable/bundled
DOTNET_BUNDLE_EXTRACT_BASE_DIR="$PWD/artifacts/single-executable/extracted" artifacts/single-executable/bundled/Probe
```

For each mode, also copy only `Probe` into its own empty directory and run it there. Bound the process lifetime; record exit status, launch marker, file inventory and extracted files. Do not infer standalone operation by running beside the native dependencies.

## Remaining gates

The owner chose “Allow single-file bundling with private runtime extraction” on 2026-09-27. ADR-0003 and the specification now record that decision. Proceed with the production foundation using the standard supported packages; do not adopt the reviewed third-party static-link packages.

Independent feasibility review was performed by a separate agent. It identified the limitations above and found a plausible investigation path; it did not accept an implementation. Full transitive dependency/support/security verification, Windows/Linux results, runtime network monitoring, complete production foundation checks and final packaging/signing remain pending. Issue #27 is not complete.

The disposable launch probe does not alter the reconciliation/storage architecture. The subsequent production foundation is modelled separately in `docs/architecture/production.c4`, with future adapters clearly marked planned.
