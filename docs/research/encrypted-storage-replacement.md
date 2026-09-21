# Maintained encrypted-storage replacement

Status: Proposed for owner decision  
Issue: [#12](https://github.com/nullabletype/dot-finance/issues/12)  
Checked: 2026-09-21  
Constraints confirmed by owner: maintained open-source dependencies only; alternatives may replace SQLite entirely.

## Recommendation

Prototype a small **immutable authenticated snapshot store using only supported .NET 10 cryptographic and filesystem APIs**. Keep the complete domain state and history in each encrypted snapshot. Use the same snapshot format for working generations and portable backups.

Do not retain `SQLitePCLRaw.bundle_e_sqlcipher`; do not replace it with another deprecated bundle. Do not select LiteDB, Realm, DBreeze, SQLCipher self-builds, or DuckDB for the next experiment. Keep **SQLite3MC** as the database fallback if measured synthetic upper-bound data makes snapshots impractical.

This recommendation is not permission to implement it. Owner approval is required, followed by a bounded replacement issue and an ADR. The implementation must use standardized primitives and a reviewed envelope; it must not implement a cipher, KDF, MAC, or novel encrypted event-log protocol.

## Why change

The current prototype directly references:

```text
Microsoft.Data.Sqlite.Core 10.0.0
SQLitePCLRaw.bundle_e_sqlcipher 2.1.11
```

The resolved native chain includes `SQLitePCLRaw.lib.e_sqlcipher 2.1.11`, whose metadata describes its binaries as unofficial and unsupported. SQLitePCLRaw 3.0 explicitly deprecates `.bundle_e_sqlcipher` and no longer distributes free encryption-enabled SQLite builds.[^sqlitepcl-v3] Inspection of the packaged macOS ARM64 binary reports SQLCipher 4.5.2 and SQLite 3.39.2 (2022-07-21), while current SQLCipher 4.19.0 includes recommended security fixes.[^sqlcipher-419]

A passing build, locked restore, or absence of NuGet advisories does not establish that embedded native code is maintained.

Existing vault files and backups contain only synthetic disposable prototype data. Discard and recreate them after replacement; do not build production migration semantics from this experiment.

## Decision matrix

| Candidate | Maintenance and supply chain | Security fit | Product fit | Decision |
| --- | --- | --- | --- | --- |
| .NET authenticated immutable snapshots | No additional package; supported .NET 10 BCL and OS crypto | Explicit PBKDF2-HMAC-SHA-256 and AES-256-GCM envelope; requires design review and robust parsing | Matches the prototype's single JSON document and small single-user history | **Recommend prototype** |
| SQLite3MC official .NET bundle | Maintained MIT ecosystem and provenance-attested packages; package 2.4.0 lags native 2.5.1 | Default ChaCha20-Poly1305 authenticated pages; explicit cipher/KDF configuration required | Preserves `Microsoft.Data.Sqlite`; native/RID update burden remains | **Fallback after package catches up or policy exception** |
| DuckDB.NET 1.5.5 | Maintained MIT native engine/wrapper; additional Arrow/native chain | AES-256-GCM database, WAL and temporary-file encryption; app still owns password KDF | Analytical engine is disproportionate; extension/network surface needs hardening | Reject for now |
| SQLCipher Community source build | Maintained BSD source | Established encrypted SQLite format | Project must build, package, sign and service SQLCipher plus OpenSSL for every RID | Reject maintenance burden |
| LiteDB 5.0.21 / v6 prerelease | Active project, mostly managed | AES-ECB; obsolete PBKDF2 constructor/defaults; no full-store authentication tag | Convenient embedded model cannot compensate for failed security gate | Reject |
| Realm .NET | On-device community branch after Device SDK EOL; stale public NuGet line | AES plus HMAC | Does not meet current-supported-distribution policy | Reject |
| SQLite SEE | Maintained by SQLite | Established encrypted SQLite | Closed-source and paid | Excluded by owner constraints |

Security, maintenance, licensing, or portability failures are hard gates; convenience cannot offset them.

## Proposed snapshot contract

The next prototype should own a versioned binary envelope with:

```text
magic and format version
bounded KDF identifier and parameters
unique random salt
AEAD identifier
unique 96-bit nonce
bounded ciphertext length
ciphertext
128-bit authentication tag
```

- Derive a 256-bit session key with the static .NET `Rfc2898DeriveBytes.Pbkdf2` API, HMAC-SHA-256, a random per-vault salt, and an explicit iteration count calibrated on the slowest supported platform.[^pbkdf2]
- Encrypt each generation with `AesGcm`, a fresh random 96-bit nonce, and a 128-bit tag. Authenticate all parseable metadata as associated data.[^aesgcm]
- Persist password encoding rules and bounded KDF parameters. Reject malicious length/iteration values before expensive allocation or KDF work.
- Retain only the derived session key where practical; zero mutable password/key buffers with `CryptographicOperations.ZeroMemory`. Document managed-memory limits.[^zeromemory]
- Return one generic authentication failure for a wrong password or modified envelope.
- Include schema version, authenticated generation number, transactions, rules, reconciliation state, history, acknowledgements and balance snapshots in the encrypted payload.
- Do not use an encrypted append-only event log: nonce allocation, record ordering, truncation detection and recovery would create a substantially more complex protocol.

This detects modification, truncation, extension and reordering when framing is correctly authenticated. It cannot detect replacement by an older otherwise-valid complete snapshot without a separate trusted monotonic anchor. The threat model and UI must state that rollback limitation.

## Atomic persistence and backups

1. Serialize and encrypt a complete generation into a unique temporary file in the same directory/filesystem.
2. Flush file contents with `FileStream.Flush(true)`, close, reopen, authenticate, deserialize and validate domain invariants.[^flush]
3. Rename to a unique immutable generation name; never overwrite the sole valid generation.
4. On startup, authenticate candidates and select the highest valid generation. Keep at least the newest and previous valid generations until cleanup is safe.
5. Treat unfinished temporary files as recovery evidence, never as completed state.
6. Create a backup by copying a closed authenticated immutable generation. Restore into staging and authenticate/validate before activating it.

Rename plus flush is not proof of power-loss durability on every filesystem. Test actual process interruption and platform/filesystem behavior. Automatic backup failure after reconciliation completion needs explicit resumable state and truthful UI because database completion and backup publication cannot be one filesystem transaction.

## Maintained database fallback

If a synthetic upper-bound benchmark shows unacceptable snapshot unlock/save time or memory use, evaluate:

```text
Microsoft.Data.Sqlite.Core 10.0.12 or current servicing patch
SQLite3MC.PCLRaw.bundle 2.4.0 or newer current distribution
SQLitePCLRaw.core 3.0.5 or newer current supported release, explicitly pinned
```

`SQLite3MC.PCLRaw.bundle` is distinct from the deprecated `SQLitePCLRaw.bundle_e_sqlite3mc`. Its 2.4.0 package includes SQLite3MC 2.4.0 / SQLite 3.53.4 and macOS, Windows, Linux glibc and musl assets. Upstream documents .NET 10 `Microsoft.Data.Sqlite.Core` integration and provenance-attested builds.[^sqlite3mc-dotnet]

The current native source is 2.5.1 while the official .NET distribution is 2.4.0.[^sqlite3mc-native] The changes do not appear to contain desktop cryptographic fixes, but upstream's security policy generally supports its latest native line. Do not silently accept that lag: wait for a matching package, obtain explicit policy evidence, or bring a separately reviewed source-build proposal to the owner.

If selected later, explicitly pin cipher and KDF settings rather than inheriting mutable defaults; assert provider/native/SQLite versions at runtime and verify every shipped RID.

## Rejected alternatives

- **LiteDB:** stable and prerelease source uses AES-ECB and obsolete `Rfc2898DeriveBytes(password, salt)` defaults; it lacks authenticated whole-store encryption.[^litedb]
- **DuckDB:** current 1.5.5 encryption uses AES-256-GCM for database, WAL and temporary files, but the analytical engine, bundled native code, Arrow dependency and extension/network features add disproportionate update and hardening work.[^duckdb]
- **SQLCipher source build:** Community Edition 4.19.0 is maintained and open source, but free use requires this project to build/service each native RID and OpenSSL chain. That recreates the native maintenance responsibility this issue seeks to remove.[^sqlcipher-community]
- **Realm:** Atlas Device SDK support ended 2025-09-30 and the local database moved to community branches; its public package chain does not satisfy this repository's support policy.[^realm]
- **DBreeze:** maintained and managed, but upstream encryption is not authenticated whole-store encryption.
- **SQLite SEE:** maintained but closed-source and paid, contrary to owner constraints.[^see]

## Required bounded implementation issue

After owner approval, create one replacement prototype issue with these acceptance gates:

### Format and cryptography

- Published known-answer tests for PBKDF2 and AES-GCM interoperability.
- Explicit version, password encoding, salt/nonce/tag sizes, bounded KDF parameters and authenticated metadata.
- Correct, wrong, empty, long and Unicode password cases.
- Mutate every header field, ciphertext region and tag; test truncation, appended bytes, reordered/duplicated data, hostile lengths/KDF counts and malformed payloads.
- Nonce uniqueness checks across repeated save, backup and restore operations.
- Independent cryptographic-format review.

### Recovery and portability

- Kill at every write, flush, validation, rename, cleanup, backup and restore phase; recover complete old or new state, never mixed state.
- Cover disk full, read-only/removed folder, lock contention, stale temporary files and insufficient space.
- Restore every supported OS-created backup on the other supported OS families using the same password.
- Inspect every app-managed generation, temporary file, backup, log and diagnostic artifact for synthetic plaintext/password markers; state inspection limits.
- Run packaged workflows with networking blocked and trace attempted outbound calls.

### Limits and fallback trigger

- Benchmark an approved synthetic upper-bound history on the slowest supported platform.
- Before implementation, agree the maximum unlock/save latency and memory threshold that would trigger the SQLite3MC fallback evaluation.

### Supply chain and architecture

- Upgrade/pin the current supported .NET SDK/runtime servicing release and all affected dependencies; refresh lockfiles and verify advisories separately from lifecycle support.
- Update LikeC4 to replace the experimental SQLCipher vault with the chosen authenticated snapshot boundary and backup flow.
- Add an ADR recording the approved format, threat model, rollback limitation, password lifecycle, prototype-data discard policy, rejected alternatives and future migration/versioning obligation.
- Record tested commit and cross-platform artifacts; obtain independent implementation, security and diagram review.

Keep #3, #4 and #5 blocked until the selected replacement implementation—not this research alone—passes those gates.

## Architecture impact of this research

No LikeC4 source changes are made by this evaluation: it documents the currently implemented experimental SQLCipher prototype truthfully. The selected replacement will change the persistence trust boundary and on-disk format, so its implementation must update LikeC4 and add an ADR.

## Primary sources

[^sqlitepcl-v3]: SQLitePCLRaw, [3.0 release notes — encryption bundle deprecation](https://github.com/ericsink/SQLitePCL.raw/blob/master/v3.md#from-sqlitepclrawbundle_e_sqlcipher).
[^sqlcipher-419]: Zetetic, [SQLCipher 4.19.0 release](https://github.com/sqlcipher/sqlcipher/releases/tag/v4.19.0).
[^pbkdf2]: Microsoft, [`Rfc2898DeriveBytes.Pbkdf2` for .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.pbkdf2?view=net-10.0); NIST, [SP 800-132](https://csrc.nist.gov/pubs/sp/800/132/final).
[^aesgcm]: Microsoft, [`AesGcm` for .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-10.0); NIST, [SP 800-38D](https://csrc.nist.gov/pubs/sp/800/38/d/final).
[^zeromemory]: Microsoft, [`CryptographicOperations.ZeroMemory`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory?view=net-10.0).
[^flush]: Microsoft, [`FileStream.Flush(Boolean)`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0).
[^sqlite3mc-dotnet]: SQLite3 Multiple Ciphers, [.NET packages and `Microsoft.Data.Sqlite.Core` usage](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/blob/v2.4.0/README.md), [2.4.0 release](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/releases/tag/v2.4.0), [package security/provenance](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/blob/main/SECURITY.md).
[^sqlite3mc-native]: SQLite3 Multiple Ciphers, [2.5.1 release](https://github.com/utelle/SQLite3MultipleCiphers/releases/tag/v2.5.1), [security policy](https://github.com/utelle/SQLite3MultipleCiphers/blob/v2.5.1/SECURITY.md).
[^litedb]: LiteDB, [5.0.21 encryption source](https://github.com/litedb-org/LiteDB/blob/v5.0.21/LiteDB/Engine/Disk/Streams/AesStream.cs), [v6 prerelease encryption source](https://github.com/litedb-org/LiteDB/blob/6.0.0-prerelease.114/LiteDB/Engine/Disk/Streams/AesStream.cs); Microsoft, [obsolete PBKDF2 constructors](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.-ctor?view=net-10.0).
[^duckdb]: DuckDB, [database encryption](https://duckdb.org/docs/current/sql/statements/attach.html#database-encryption), [security hardening](https://duckdb.org/docs/current/operations_manual/securing_duckdb/overview.html), [1.5.5 release](https://github.com/duckdb/duckdb/releases/tag/v1.5.5).
[^sqlcipher-community]: Zetetic, [SQLCipher Community Edition](https://www.zetetic.net/sqlcipher/community/), [design](https://www.zetetic.net/sqlcipher/design/).
[^realm]: MongoDB, [Atlas Device SDK deprecation and EOL](https://www.mongodb.com/docs/atlas/device-sdks/deprecation/).
[^see]: SQLite, [SQLite Encryption Extension](https://sqlite.org/com/see.html).
