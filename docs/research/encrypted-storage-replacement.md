# Maintained encrypted-storage replacement

- **Status:** Snapshot feasibility experiment approved; storage selection pending evidence
- **Issue:** [#12](https://github.com/nullabletype/dot-finance/issues/12)
- **Checked:** 2026-09-21
Constraints confirmed by owner: maintained open-source dependencies only; alternatives may replace SQLite entirely.

## Current conclusion

No candidate is ready for selection yet.

The next step should be a bounded comparative feasibility experiment between:

1. a small **immutable authenticated snapshot store using only supported .NET 10 cryptographic and filesystem APIs**; and
2. **SQLite3MC**, but only after its official .NET/native package contains the current supported native release. Its current 2.4.0 package trails native 2.5.1 and is therefore unavailable under this repository's strict support policy.

The comparison must use an agreed synthetic history/search workload and equal security, recovery, migration, concurrency and maintenance criteria. Do not select snapshots merely because they add no package, and do not select a database merely because it supplies transactions and indexes.

Do not retain `SQLitePCLRaw.bundle_e_sqlcipher`; do not replace it with another deprecated bundle. LiteDB, Realm, DBreeze, SQLCipher self-builds, DuckDB and SQLite SEE are ruled out below.

This conclusion is not permission to replace persistence. Owner approval of the workload and experiment is required, followed by a bounded prototype issue. A storage selection and ADR come only after evidence. Any snapshot experiment must use standardized primitives and a reviewed envelope; it must not implement a cipher, KDF, MAC, or novel encrypted event-log protocol.

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
| .NET authenticated immutable snapshots | No additional package; supported .NET 10 BCL and OS crypto | Explicit PBKDF2-HMAC-SHA-256 and AES-256-GCM custom protocol; requires complete specification and independent review | Whole-history rewrites/search/indexing/concurrency are unproven | **Feasibility candidate** |
| SQLite3MC official .NET bundle | Maintained MIT ecosystem and provenance-attested packages; package 2.4.0 lags supported native 2.5.1 | Default ChaCha20-Poly1305 authenticated pages; explicit cipher/KDF configuration required | Preserves queries, incremental transactions and `Microsoft.Data.Sqlite`; native/RID burden remains | **Unavailable until package catches up** |
| DuckDB.NET 1.5.5 | Current MIT engine/wrapper; additional Apache-2.0 Arrow/native chain | AES-256-GCM database, WAL and temporary-file encryption; app still owns password KDF | Analytical engine is disproportionate; extension/network surface needs hardening | Reject for now |
| SQLCipher Community 4.19.0 source build | Maintained BSD-3-Clause source | Established encrypted SQLite format | Project must build, package, sign and service every RID and selected crypto-provider chain | Reject maintenance burden |
| LiteDB 5.0.21 / v6 prerelease | MIT; stable package dates from 2024 while v6 remains prerelease | AES-ECB; obsolete PBKDF2 constructor/defaults; no full-store authentication tag | Convenient embedded model cannot compensate for failed security gate | Reject |
| Realm .NET 20.1.0 | Apache-2.0 package dates from 2025-01; on-device community branch after Device SDK EOL | AES-256 plus HMAC; caller must supply a 64-byte key | Distribution does not meet current-supported policy | Reject |
| DBreeze 1.138.2026.603 | Current BSD-3-Clause managed package | Published encryption feature is deterministic, unauthenticated text-search encryption, not whole-store encryption | Cannot meet working-store/backup requirement | Reject |
| SQLite SEE | Maintained by SQLite | Established encrypted SQLite | Closed-source and paid | Excluded by owner constraints |

Security, maintenance, licensing, or portability failures are hard gates; convenience cannot offset them.

## Snapshot feasibility requirements

The experiment must first specify a versioned binary envelope with canonical byte encoding. At minimum:

```text
magic and format version
bounded KDF identifier and parameters
vault identifier and lineage
unique random per-vault salt with explicit size
AEAD identifier
unique 96-bit nonce
bounded ciphertext length
ciphertext
128-bit authentication tag
```

- Derive a 256-bit session key with the static .NET `Rfc2898DeriveBytes.Pbkdf2` API, HMAC-SHA-256, an explicitly sized random per-vault salt, and an iteration count calibrated on the slowest supported platform.[^pbkdf2]
- Encrypt each generation with `AesGcm`, a nonce from `RandomNumberGenerator`, and a 128-bit tag. Authenticate canonical framing, vault identity/lineage, generation and all parseable metadata as associated data.[^aesgcm]
- Define nonce uniqueness across every invocation with a key, including copied/restored/forked vaults; enforce NIST's per-key invocation bound and a key-rotation strategy. Set and validate a maximum plaintext/snapshot size.[^gcm-iv]
- Persist password encoding rules and bounded KDF parameters. Reject malicious length/iteration values before expensive allocation or KDF work.
- Retain only the derived session key where practical; zero mutable password/key buffers with `CryptographicOperations.ZeroMemory`. Document managed-memory limits.[^zeromemory]
- Define password UTF-8/normalisation, minimum policy, change/rekey behavior, old-backup semantics, file permissions and secret-bearing log/crash-dump policy. Empty passwords should be rejected rather than treated as an ordinary success case.
- Return one generic authentication failure for a wrong password or modified envelope.
- Include schema version, authenticated generation number, transactions, rules, reconciliation state, history, acknowledgements and balance snapshots in the encrypted payload.
- Do not use an encrypted append-only event log: nonce allocation, record ordering, truncation detection and recovery would create a substantially more complex protocol.

Only a fully specified and reviewed envelope can claim detection of modification, truncation, extension, mixing or reordering. It cannot detect replacement by an older otherwise-valid complete snapshot without a separate trusted monotonic anchor. The threat model and UI must state that rollback limitation.

The experiment must also prove the storage model, not only encryption:

- define an agreed transaction/reconciliation retention horizon and synthetic upper-bound size;
- measure unlock, search, edit/save, backup, restore, memory use and storage amplification;
- specify in-memory search indexes and verify required searchable/inspectable history;
- enforce a single application instance or exclusive writer lock, plus compare-and-swap generation allocation;
- reject forked/equal-generation lineages deterministically rather than selecting an arbitrary filename;
- define schema/format migration, downgrade refusal and restoration of older backups.

## Atomic persistence and backups

1. Serialize and encrypt a complete generation into a unique temporary file in the same directory/filesystem.
2. Flush file contents with `FileStream.Flush(true)`, close, reopen, authenticate, deserialize and validate domain invariants.[^flush]
3. Rename to a unique immutable generation name; never overwrite the sole valid generation.
4. On startup, authenticate candidates and select the highest valid generation in the expected lineage. Reject forks or duplicate generation numbers. Keep at least the newest and previous valid generations until cleanup is safe.
5. Treat unfinished temporary files as recovery evidence, never as completed state.
6. Create a backup by copying the closed authenticated immutable generation bound to the completed reconciliation. Record pending publication and retry idempotently. Restore into staging and authenticate/validate before activating it.

Rename plus flush is not proof of power-loss durability on every filesystem. Test actual process interruption and platform/filesystem behavior. Automatic backup failure after reconciliation completion needs explicit resumable state and truthful UI because database completion and backup publication cannot be one filesystem transaction.

## SQLite3MC feasibility candidate

Evaluate this candidate only when an official package contains the current supported native release:

```text
Microsoft.Data.Sqlite.Core 10.0.12 or current servicing patch
SQLite3MC.PCLRaw.bundle 2.4.0 or newer current distribution
SQLitePCLRaw.core 3.0.5 or newer current supported release, explicitly pinned
```

`SQLite3MC.PCLRaw.bundle` is distinct from the deprecated `SQLitePCLRaw.bundle_e_sqlite3mc`. Its 2.4.0 package includes SQLite3MC 2.4.0 / SQLite 3.53.4 and macOS, Windows, Linux glibc and musl assets. Upstream documents .NET 10 `Microsoft.Data.Sqlite.Core` integration and provenance-attested builds.[^sqlite3mc-dotnet]

The current native source is 2.5.1 while the official .NET distribution is 2.4.0.[^sqlite3mc-native] Upstream's security policy generally supports its latest native line. The repository policy has no exception for an unsupported dependency, regardless of whether the intervening changelog appears security-relevant. Wait for a current package; a project-maintained source build would be a different, separately approved option with its own provenance and RID burden.

When available, compare it against snapshots on the same search workload, incremental update/write amplification, memory, backup consistency, concurrency, migration, implementation complexity and long-term format/native maintenance criteria. Explicitly pin cipher and KDF settings rather than inheriting mutable defaults; assert provider/native/SQLite versions at runtime and verify every shipped RID.

## Rejected alternatives

- **LiteDB:** stable and prerelease source uses AES-ECB and obsolete `Rfc2898DeriveBytes(password, salt)` defaults; it lacks authenticated whole-store encryption.[^litedb]
- **DuckDB:** current MIT `DuckDB.NET.Data.Full` 1.5.5 has explicit .NET 10 assets and depends on `DuckDB.NET.Bindings.Full` 1.5.5 plus Apache-2.0 Apache Arrow 23.0.0. Its encryption uses AES-256-GCM for database, WAL and temporary files, but the analytical engine, bundled native code and extension/network features add disproportionate update and hardening work.[^duckdb][^duckdb-nuget]
- **SQLCipher source build:** BSD-3-Clause Community Edition 4.19.0 is maintained and open source, but free use requires this project to build/service each native RID and its selected cryptographic provider (for example OpenSSL or platform APIs). That recreates the native maintenance responsibility this issue seeks to remove.[^sqlcipher-community]
- **Realm:** Apache-2.0 Realm 20.1.0 was published 2025-01-07. Atlas Device SDK support ended 2025-09-30 and the local database moved to community branches; its public package chain does not satisfy this repository's support policy. Its local format uses AES-256 plus HMAC with a caller-supplied 64-byte key, but that does not overcome distribution status.[^realm][^realm-encryption]
- **DBreeze:** BSD-3-Clause DBreeze 1.138.2026.603 was published 2026-06-03 and is maintained, but its documented text-search transform deliberately uses deterministic AES-CTR, reveals repeated prefixes and does not authenticate ciphertext. It is not whole-store encryption.[^dbreeze]
- **SQLite SEE:** maintained but closed-source and paid, contrary to owner constraints.[^see]

## Required bounded comparative prototype issue

After the owner defines the workload/thresholds and approves the comparison, create one disposable comparative prototype issue. It must not replace the current persistence implementation. A later issue implements the selected result.

### Format and cryptography

- Published known-answer tests for PBKDF2 and AES-GCM interoperability.
- Explicit version, password encoding, salt/nonce/tag sizes, bounded KDF parameters and authenticated metadata.
- Correct, wrong, rejected-empty, long and Unicode password cases.
- Mutate every header field, ciphertext region and tag; test truncation, appended bytes, reordered/duplicated data, hostile lengths/KDF counts and malformed payloads.
- Nonce uniqueness, per-key invocation limits and restore/fork/rekey checks across repeated save, backup and restore operations.
- Independent cryptographic-format review.

### Recovery and portability

- Kill at every write, flush, validation, rename, cleanup, backup and restore phase; recover complete old or new state, never mixed state.
- Cover disk full, read-only/removed folder, lock contention, stale temporary files and insufficient space.
- Restore every supported OS-created backup on the other supported OS families using the same password.
- Inspect every app-managed generation, temporary file, backup, log and diagnostic artifact for synthetic plaintext/password markers; state inspection limits.
- Run packaged workflows with networking blocked and trace attempted outbound calls.

### Workload and decision gates

- Benchmark an approved synthetic upper-bound history on the slowest supported platform, including required history searches and incremental edits.
- Before prototyping, agree maximum unlock, search, save and restore latency; memory; disk amplification; and backup size/time thresholds.
- Reject snapshots if performance, searchable history, format security review, concurrency, recovery, migration or long-term ownership fails—not only if performance fails.
- Reject SQLite3MC while its official package trails its supported native release; if a current package becomes available, compare both candidates using the same gates.

### Supply chain and architecture

- Upgrade/pin the current supported .NET SDK/runtime servicing release and all affected dependencies; refresh lockfiles and verify advisories separately from lifecycle support.
- Update LikeC4 to replace the experimental SQLCipher vault with the selected storage/encryption boundary and backup flow.
- Add an ADR recording the approved format, threat model, rollback limitation, password lifecycle, prototype-data discard policy, rejected alternatives and future migration/versioning obligation.
- Record tested commit and cross-platform artifacts; obtain independent implementation, security and diagram review.

Keep #3, #4 and #5 blocked until the selected replacement implementation—not this research alone—passes those gates.

## Owner-approved feasibility parameters

Recorded 2026-09-21:

1. Retain and benchmark **20 years / 100,000 synthetic transactions**.
2. Search all displayed transaction fields—date, amount, account, bank, category, type, counterparty, merchant and notes—plus reconciliation and settlement status.
3. On the slowest supported test machine: search under **1 second**, unlock under **3 seconds**, save under **3 seconds**, and automatic backup under **15 seconds**.
4. Version one may enforce **one running application instance/writer per vault** with an exclusive lock.
5. Run the snapshot feasibility experiment now. Keep storage selection open; compare SQLite3MC only after its official package contains the current supported native release.

These are experiment gates, not product defaults or a storage-selection decision. Passing them permits an owner decision; it does not itself approve the custom envelope for production.

## Architecture impact of this research

No LikeC4 source changes are made by this evaluation: it documents the currently implemented experimental SQLCipher prototype truthfully. The selected replacement will change the persistence trust boundary and on-disk format, so its implementation must update LikeC4 and add an ADR.

## Primary sources

[^sqlitepcl-v3]: SQLitePCLRaw, [3.0 release notes — encryption bundle deprecation](https://github.com/ericsink/SQLitePCL.raw/blob/master/v3.md#from-sqlitepclrawbundle_e_sqlcipher).
[^sqlcipher-419]: Zetetic, [SQLCipher 4.19.0 release](https://github.com/sqlcipher/sqlcipher/releases/tag/v4.19.0).
[^pbkdf2]: Microsoft, [`Rfc2898DeriveBytes.Pbkdf2` for .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.pbkdf2?view=net-10.0); NIST, [SP 800-132](https://csrc.nist.gov/pubs/sp/800/132/final).
[^aesgcm]: Microsoft, [`AesGcm` for .NET 10](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-10.0); NIST, [SP 800-38D](https://csrc.nist.gov/pubs/sp/800/38/d/final).
[^gcm-iv]: NIST, [SP 800-38D, sections 8–8.3](https://csrc.nist.gov/pubs/sp/800/38/d/final).
[^zeromemory]: Microsoft, [`CryptographicOperations.ZeroMemory`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory?view=net-10.0).
[^flush]: Microsoft, [`FileStream.Flush(Boolean)`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0).
[^sqlite3mc-dotnet]: SQLite3 Multiple Ciphers, [.NET packages and `Microsoft.Data.Sqlite.Core` usage](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/blob/v2.4.0/README.md), [2.4.0 release](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/releases/tag/v2.4.0), [package security/provenance](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/blob/main/SECURITY.md).
[^sqlite3mc-native]: SQLite3 Multiple Ciphers, [2.5.1 release](https://github.com/utelle/SQLite3MultipleCiphers/releases/tag/v2.5.1), [security policy](https://github.com/utelle/SQLite3MultipleCiphers/blob/v2.5.1/SECURITY.md).
[^litedb]: LiteDB, [5.0.21 encryption source](https://github.com/litedb-org/LiteDB/blob/v5.0.21/LiteDB/Engine/Disk/Streams/AesStream.cs), [v6 prerelease encryption source](https://github.com/litedb-org/LiteDB/blob/6.0.0-prerelease.114/LiteDB/Engine/Disk/Streams/AesStream.cs); Microsoft, [obsolete PBKDF2 constructors](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.-ctor?view=net-10.0).
[^duckdb]: DuckDB, [database encryption](https://duckdb.org/docs/current/sql/statements/attach.html#database-encryption), [security hardening](https://duckdb.org/docs/current/operations_manual/securing_duckdb/overview.html), [1.5.5 release](https://github.com/duckdb/duckdb/releases/tag/v1.5.5).
[^duckdb-nuget]: NuGet, [`DuckDB.NET.Data.Full` 1.5.5 metadata](https://api.nuget.org/v3/catalog0/data/2026.07.26.13.01.55/duckdb.net.data.full.1.5.5.json).
[^sqlcipher-community]: Zetetic, [SQLCipher Community Edition](https://www.zetetic.net/sqlcipher/community/), [design](https://www.zetetic.net/sqlcipher/design/).
[^realm]: MongoDB, [Atlas Device SDK deprecation and EOL](https://www.mongodb.com/docs/atlas/device-sdks/deprecation/).
[^realm-encryption]: Realm, [local encryption design](https://github.com/realm/realm-dotnet/blob/community/Guides/realm-files/encrypt-a-realm.md); NuGet, [Realm 20.1.0 metadata](https://api.nuget.org/v3/catalog0/data/2025.01.07.16.17.19/realm.20.1.0.json).
[^dbreeze]: DBreeze, [BSD-3-Clause licence](https://github.com/hhblaze/DBreeze/blob/master/LICENSE), [text-search encryption source and limitations](https://github.com/hhblaze/DBreeze/blob/master/DBreeze.Net8/TextSearch/WabiStreamCrypto.cs); NuGet, [1.138.2026.603 publication](https://api.nuget.org/v3/registration5-semver1/dbreeze/1.138.2026.603.json).
[^see]: SQLite, [SQLite Encryption Extension](https://sqlite.org/com/see.html).
