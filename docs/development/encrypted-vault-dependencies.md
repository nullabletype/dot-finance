# Encrypted vault dependency review

Checked 2026-09-27 for issue #29. This record covers the production encrypted-SQLite dependency choice, provenance, cipher configuration and single-file interaction. It does not replace exact-commit native tests on macOS, Windows and Linux. The lockfiles remain the reproducible dependency authority.

## Selected package graph

| Component | Selection and evidence |
| --- | --- |
| .NET and `Microsoft.Data.Sqlite.Core` | Retain .NET 10.0.12 and `Microsoft.Data.Sqlite.Core` 10.0.12. .NET 10 is active LTS through 2028-11-14 and 10.0.12 is the current servicing patch. Microsoft documents the `Core` package as the route for a custom native SQLite binary and requires that binary to be installed and initialised separately. [`Microsoft.Data.Sqlite.Core` package](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.12), [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions) |
| SQLite3MC .NET bundle | Retain `SQLite3MC.PCLRaw.bundle` 2.4.0. It is the current wrapper package on NuGet, contains SQLite3MC 2.4.0 based on SQLite 3.53.4, and explicitly supports `Microsoft.Data.Sqlite.Core`. The bundle depends on its matching `provider` and `lib` packages; using another SQLitePCLRaw bundle in the same process is prohibited by the maintainer. [`SQLite3MC.PCLRaw.bundle` 2.4.0](https://www.nuget.org/packages/SQLite3MC.PCLRaw.bundle/2.4.0), [2.4.0 package release](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/releases/tag/v2.4.0) |
| SQLitePCLRaw | Retain the explicit `SQLitePCLRaw.core` 3.0.5 pin. `Microsoft.Data.Sqlite.Core` 10.0.12 declares a lower 2.1.12 minimum, while the SQLite3MC 2.4.0 provider requires 3.0.2 or later. The resolved 3.0.5 package therefore satisfies both consumers and records upstream commit `ed046114d5a30534e13294d94d78eb73de896ad4`. [`SQLitePCLRaw.core` source commit](https://github.com/ericsink/SQLitePCL.raw/commit/ed046114d5a30534e13294d94d78eb73de896ad4), [`SQLite3MC.PCLRaw.provider` 2.4.0](https://www.nuget.org/packages/SQLite3MC.PCLRaw.provider/2.4.0) |

The live NuGet catalogue checks below reported no newer package in this graph and no published deprecation or vulnerability metadata. This is catalogue evidence, not proof that no undisclosed vulnerability exists. SQLite3MC's security policy accepts private vulnerability reports on a best-effort basis and gives no response-time or remediation guarantee, so dependency status must be rechecked for every release. [SQLite3MC security policy](https://github.com/utelle/SQLite3MultipleCiphers/blob/main/SECURITY.md)

The 2.4.0 native package inspected from NuGet contains shared libraries for the four production RIDs: `osx-arm64`, `osx-x64`, `win-x64` and `linux-x64`. It also includes Windows arm64/x86 and several additional Linux targets. The files are dynamic `.dylib`, `.dll` and `.so` assets, so they fit the accepted private-extraction route rather than Native AOT static linkage. [`SQLite3MC.PCLRaw.lib` 2.4.0 package](https://api.nuget.org/v3-flatcontainer/sqlite3mc.pclraw.lib/2.4.0/sqlite3mc.pclraw.lib.2.4.0.nupkg)

## Native source and provenance

SQLite3MC source 2.5.1 is newer than the current .NET bundle, but should not be mixed into this graph. Source 2.5.0 changes linker visibility and adds dispatch-table options for applications that cannot avoid multiple SQLite instances; 2.5.1 fixes the precompiled WebAssembly build. Both remain based on SQLite 3.53.4. Those changes do not correct a desktop shared-library encryption defect in the selected single-instance route, and no matching 2.5.x .NET bundle is published. Reassess when the maintainer publishes a matching wrapper, provider and native-library set. [2.5.0 release](https://github.com/utelle/SQLite3MultipleCiphers/releases/tag/v2.5.0), [2.5.1 release](https://github.com/utelle/SQLite3MultipleCiphers/releases/tag/v2.5.1), [version mapping](https://github.com/utelle/SQLite3MultipleCiphers/blob/main/VERSIONMAPPING.md)

The 2.4.0 NuGet release is tied to signed source commit `78ba505f8e52e05fb819bac7f52780784382d5d9`. The maintainer states that native binaries are checksum-validated, packages are built and published only by GitHub Actions in ephemeral environments, and GitHub build provenance is cryptographically attested. Verify downloaded release packages with `gh attestation verify` before a release build rather than treating the NuGet owner name alone as provenance. [2.4.0 build and integrity record](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/releases/tag/v2.4.0)

## Cipher and password handling

Pin the database URI configuration before supplying the password:

```text
file:<escaped-path>?cipher=chacha20&legacy=0&kdf_iter=600000&plaintext_header_size=0&hmac_check=1
```

Use that complete `file:` URI as `SqliteConnectionStringBuilder.DataSource`, then set `Password` on the builder; do not put `key` or `hexkey` in the URI. Microsoft.Data.Sqlite 10.0.12 detects the `file:` prefix, passes the URI to `sqlite3_open_v2` with `SQLITE_OPEN_URI`, and only then sends `PRAGMA key`. SQLite3MC applies the URI cipher parameters while opening the connection, before the key is applied. Query-parameter order does not matter, but `cipher` must remain explicit because unknown parameters are silently ignored and otherwise fall back to the compiled default. SQLite3MC also warns that a URI key remains visible for the connection lifetime. [Microsoft.Data.Sqlite 10.0.12 open sequence](https://github.com/dotnet/efcore/blob/v10.0.12/src/Microsoft.Data.Sqlite.Core/SqliteConnectionInternal.cs), [Microsoft connection-string URI support](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings), [SQLite3MC URI parameters](https://utelle.github.io/SQLite3MultipleCiphers/docs/configuration/config_uri/)

For `chacha20`, SQLite3MC uses the sqleet format: ChaCha20-Poly1305, a random 16-byte database salt, PBKDF2-HMAC-SHA256 password derivation, per-page one-time keys and nonces, and a 16-byte Poly1305 authentication tag per page. SQLite3MC's compatibility default is 64,007 PBKDF2 iterations, non-legacy mode and no plaintext header. Pin **600,000 iterations** for the first production format: this matches OWASP's current PBKDF2-HMAC-SHA256 recommendation and avoids treating the lower vendor interoperability default as a security target. Use the same value for every create/open/backup/restore path and record it in the schema/format metadata. Measure create and unlock on the slowest supported native runner; OWASP's general ceiling is under one second per derivation, so a result above that blocks acceptance and requires an explicitly reviewed adjustment rather than a silent platform-specific value. [sqleet cipher parameters](https://utelle.github.io/SQLite3MultipleCiphers/docs/ciphers/cipher_chacha20/), [OWASP PBKDF2 work factor](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#pbkdf2), [OWASP work-factor performance guidance](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#using-work-factors)

Keep HMAC checking enabled. `PRAGMA memory_security = 1` asks SQLite3MC to clear allocations before freeing them, but it is a no-op if the native library was not compiled with that feature and it does not lock memory against swapping or hibernation. Production tests must query the applied cipher parameters and native version rather than assuming that a connection opened with encryption. Password strings must remain scoped to the open vault and must never be logged, persisted in settings, included in errors, or passed in command-line arguments. [SQLite3MC HMAC and memory-security behaviour](https://utelle.github.io/SQLite3MultipleCiphers/docs/configuration/config_sql_pragmas/)

## Single-file extraction

The SQLite3MC desktop libraries are native assets. A one-file publish therefore requires `IncludeNativeLibrariesForSelfExtract=true`; the runtime extracts native libraries before startup into its per-user cache or a configured `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. Microsoft warns that the directory must not be writable by users or services with different privileges. [.NET single-file native-library guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

The host derives a 12-character bundle ID from SHA-256 hashes of the bundled file contents, then extracts under `<base>/<executable-name>/<bundle-id>`. A changed executable therefore uses a separate directory, which prevents an in-place binary update from colliding with the previous bundle's native libraries. The same bundle reuses an existing extraction, recovers missing files individually, and stages a new extraction in a process-specific directory before an atomic rename. The reuse path checks whether expected files exist; it does not hash an existing file again. Keep the cache owner-only, and verify first launch, repeated launch, missing-file recovery and launch after replacing the executable in native CI. [bundle ID generation](https://github.com/dotnet/runtime/blob/main/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs), [extraction and recovery](https://github.com/dotnet/runtime/blob/main/src/native/corehost/bundle/extractor.cpp)

## Catalogue commands

Run on 2026-09-27 against `https://api.nuget.org/v3/index.json`:

```text
dotnet package list --project src/DotFinance.Storage/DotFinance.Storage.csproj --outdated --include-transitive
dotnet package list --project src/DotFinance.Storage/DotFinance.Storage.csproj --deprecated --include-transitive
dotnet package list --project src/DotFinance.Storage/DotFinance.Storage.csproj --vulnerable --include-transitive
```

All three completed successfully: no updates, deprecations or known vulnerabilities were reported for the resolved graph.
