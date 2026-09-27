# ADR-0001: Encrypted SQLite via SQLite3MC for local persistence

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

dot-finance is an offline desktop application that requires encrypted-at-rest persistence: a working database and its backups, protected by the same user password and restorable across macOS, Windows, and Linux.

The initial prototype used `SQLitePCLRaw.bundle_e_sqlcipher` (SQLCipher). That bundle is deprecated: the SQLitePCLRaw maintainer stopped distributing no-cost encryption-enabled SQLite builds in the 3.0 release line. The repository requires maintained, supported dependencies only, so this is a blocking issue (#12). A bespoke authenticated-snapshot store was built and validated as an alternative (#13).

## Decision

Use **SQLite3MC** (`SQLite3MC.PCLRaw.bundle` 2.4.0) with `Microsoft.Data.Sqlite.Core` for encrypted SQLite persistence. The production format pins the sqleet cipher (`chacha20`, ChaCha20-Poly1305), non-legacy mode, authenticated-page checking, no plaintext header and 600,000 PBKDF2-HMAC-SHA256 iterations. The work factor follows current OWASP guidance and must remain below one second on supported native runners; changing it is a reviewed format/security decision rather than a platform-specific fallback.

## Rationale

- The data model is relational — transactions, categories, funding rules, reconciliations and running totals, with local search planned for a later workflow. SQLite is the standard embedded database for this shape and provides SQL, indexes, transactions, incremental writes and a supported route to add indexed search through a versioned migration.
- SQLite3MC is the maintained, MIT-licensed successor to the deprecated SQLCipher bundle, with SLSA provenance-attested GitHub Actions builds.
- The selected .NET bundle and native library are both 2.4.0. Upstream SQLite3MC source 2.5.1 is newer, but both use SQLite 3.53.4 and the 2.5.x deltas are linker/visibility hygiene and a WebAssembly fix. A matching 2.5.x .NET bundle is not available, so the graphs are not mixed.

## Alternatives considered

- **Authenticated snapshot store (#13).** Validated feasible — independent crypto second opinion returned *SOUND WITH CAVEATS*, latency gates pass on all three OSes, and cross-platform restore was implemented — but rejected: it re-implements what SQLite already provides (indexing, transactions, incremental writes), pays a whole-file rewrite per save (visible on the Windows save-gate edge), and adds a bespoke format to maintain. Retained as a reviewed fallback.
- **SQLCipher community self-build.** Rejected: per-RID native build and servicing burden.
- **SQLite SEE.** Rejected: closed-source and paid.
- **LiteDB.** Rejected: AES-ECB and no full-store authentication.
- **Realm.** Rejected: distribution no longer meets the support policy.
- **DBreeze.** Rejected: deterministic, unauthenticated encryption.
- **DuckDB.** Rejected: analytical engine disproportionate to the need.

## Consequences

- A native dependency is carried across macOS/Windows/Linux (RID-specific binaries). Accepted; provenance attestation mitigates supply-chain risk.
- The implemented schema records version 1 independently of the domain-state format. Cipher and KDF settings are supplied in the SQLite file URI before the password is applied and are queried back on every open.
- Schema version 1 exposes relational projections and indexes for the issue #29 durability and reconciliation-access needs: account roles and minima, rule sources, transaction dates/categories, effective funding and GBP minor-unit values, completed dates, and correction identities. Full-text search, merchant/note projections and a consumer-owned query port are deliberately deferred until a search workflow defines their semantics; they require a reviewed schema migration rather than an unapproved issue #29 interface.
- The password is kept only for the open vault session so encrypted backups can use the same key. The adapter clears its mutable copy on disposal; the immutable strings required by Microsoft.Data.Sqlite cannot be guaranteed to be overwritten by managed code. Passwords never enter settings, paths, command lines, logs, evidence or files.
- The snapshot prototype's crypto-review findings (iteration-count calibration, nonce bound, re-key on fork) are specific to that bespoke format and do not transfer to SQLite3MC; that prototype is an unreviewed-production fallback only.
- Existing synthetic SQLCipher prototype data need not be migrated (disposable).
