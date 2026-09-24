# Disposable authenticated snapshot experiment — issue #13

This prototype tests whether a complete immutable encrypted snapshot is practical and safe enough to remain a storage candidate. It is separate from the desktop shell's deprecated SQLCipher experiment and is **not selected production storage**.

## Public seam

`VaultSession` is the agreed public test seam:

- create or unlock once per app session;
- save and load complete state;
- search the in-memory transaction index;
- create and restore a portable authenticated backup;
- rotate the working-vault password while old backups retain their old password;
- dispose the session, clearing its mutable derived-key buffer and releasing the exclusive writer lock.

Only one process may open a vault for writing. The lock file and active-generation pointer contain no financial data. The pointer is not a trusted anti-rollback anchor: replacing it and a generation with an older valid pair can roll the vault back. Malicious rollback detection remains out of scope without separate trusted monotonic state.

## Envelope version 1

All integers are canonical unsigned big-endian values. The fixed 72-byte header is authenticated as AES-GCM associated data:

| Offset | Length | Meaning |
| ---: | ---: | --- |
| 0 | 8 | ASCII magic `DFSNAP01` |
| 8 | 2 | format version `1` |
| 10 | 1 | KDF ID `1`: PBKDF2-HMAC-SHA-256 |
| 11 | 1 | AEAD ID `1`: AES-256-GCM |
| 12 | 4 | PBKDF2 iterations, currently provisional `200000` |
| 16 | 16 | random per-key salt |
| 32 | 16 | RFC 4122 vault identifier in big-endian byte order |
| 48 | 4 | generation; maximum `2^32 - 1` invocations per key |
| 52 | 12 | random nonce from `RandomNumberGenerator` |
| 64 | 8 | ciphertext length, bounded to 512 MiB |

Ciphertext follows the header, followed by a 16-byte GCM authentication tag. The plaintext is UTF-8 JSON containing schema version, all synthetic transaction fields, configuration and reconciliation records. Parsing happens only after authentication. Header identifiers, KDF parameters, vault lineage, generation, nonce and length are therefore authenticated.

The prototype uses .NET's static PBKDF2 API and `AesGcm`; it does not implement a cipher, KDF or MAC. The 200,000-iteration value is an experiment input, not a production decision. It must be calibrated on the slowest supported machine and independently reviewed. Password characters are encoded directly as UTF-8 with no Unicode normalization; empty or whitespace-only passwords are rejected. The caller's original managed password storage cannot be cleared, but temporary UTF-8 bytes and session key arrays are zeroed.

Each save uses a random 96-bit nonce and checks it against retained generations in the local lineage. The generation cap follows the NIST SP 800-38D random-IV invocation limit. A copied vault can be forked outside this single-writer process, so the security review must decide whether random-IV probability and the total cross-copy invocation bound are acceptable or require a different key/nonce design. Password rotation creates a new random salt/key before any further generation and resets the per-key count in principle; the envelope does not currently persist a separate per-key invocation counter beyond retained generations.

## Persistence and password rotation

A save writes a unique same-directory partial file with write-through enabled, flushes it to disk, closes, reopens, authenticates and deserializes it, then publishes a unique immutable generation. It subsequently replaces the small `active-generation` pointer. Interruption before pointer replacement leaves the previous generation active; interruption afterward selects the new generation. Old generations remain available for forensic/recovery investigation.

Password rotation publishes the same logical state as the next generation under a new salt/key and only then switches the active pointer. The old password no longer opens the working vault through its active pointer, while existing backup files deliberately retain their old password. An attacker capable of rolling back local files can still reactivate an older valid generation; this is the declared rollback limitation.

`Flush(true)` and same-filesystem rename reduce interruption risk but do not prove directory-entry durability across power loss on every filesystem. The trial must report actual macOS, Windows and Linux behavior and cannot claim universal power-loss atomicity.

## Search and scale trial

Run:

```sh
npm run snapshots:trial
```

It generates 100,000 fictional transactions spanning 20 years, then records save, unlock, backup, retained managed memory, file size, and p95 searches across the approved fields. Gates on the slowest supported machine are:

- search under 1 second;
- unlock under 3 seconds;
- save under 3 seconds;
- automatic backup under 15 seconds.

Memory and disk/write amplification are reported for owner assessment; passing latency alone cannot select this design. Failure of cryptographic review, malformed-input handling, single-writer correctness, recovery, migration, portability or maintainability rejects the candidate.

## Fault and recovery trial

Run:

```sh
npm run snapshots:fault-smoke
```

An environment-gated crash hook (`DFSNAP_CRASH_POINT` / `DFSNAP_CRASH_MARKER`, inactive in production and confined to the trial executable) kills a child process at the covered persistence phases — before the generation rename, before the pointer update, at backup staging, at restore staging, and before the password-change pointer update — and the smoke script proves the vault recovers to a complete old-or-new state, never mixed. Interruption is not yet fault-injected for journal publication or restore-after-rename. `FilesystemFaultTests` additionally covers stale partial files, single-writer lock contention, restore into a non-empty directory, backup over a different generation, a missing backup parent directory, and a read-only vault directory (Linux; skipped under a privileged account because root bypasses permission checks).

## Manual cross-platform backup cycle

CI runs the macOS → Linux → macOS (+ one-direction Windows) cycle automatically, but the same evidence can be produced by hand on your own machines (requires the .NET 10 SDK). Build the trial on each machine, then use a fresh, empty directory per stage.

macOS (create vault + portable backup):

```sh
dotnet run --project prototypes/AuthenticatedSnapshots.Trial --configuration Release -- \
  --cycle initialize cycle-init cycle-init/portable.dfsnap
```

Copy `cycle-init/portable.dfsnap` to the Linux machine as `incoming/portable.dfsnap`, then:

```sh
dotnet run --project prototypes/AuthenticatedSnapshots.Trial --configuration Release -- \
  --cycle continue cycle-linux incoming/portable.dfsnap cycle-linux/portable.dfsnap
```

Copy `cycle-linux/portable.dfsnap` back to macOS as `incoming/portable.dfsnap`, then:

```sh
dotnet run --project prototypes/AuthenticatedSnapshots.Trial --configuration Release -- \
  --cycle verify cycle-final incoming/portable.dfsnap
```

Each command prints `PASS: no plaintext markers in N files` and exits 0 on a correct authenticated round-trip; a document mismatch or a plaintext leak exits non-zero. Windows: run `--cycle verify` against the Linux backup (one direction).

## Evidence status

Completed on Linux ARM64 and macOS ARM64 (latency numbers recorded on the issue; all latency gates pass):

- published PBKDF2 and AES-GCM known-answer vectors;
- bounded hostile KDF/length, malformed-payload and password-policy tests;
- schema-version validation with downgrade refusal;
- abrupt termination at the save/pointer/backup/restore/password-change phases (journal publication and restore-after-rename are not yet fault-injected);
- read-only, stale-partial, lock-contention and missing-parent backup trials;
- durable automatic-backup pending/retry state tied to the saved generation.

Remaining (these still keep the recommendation at **rework / no storage selection**):

- disk-full and full directory-removal fault matrix (not deterministically coverable in the current environment);
- pairwise/cycle backup-restoration evidence on macOS, Windows and Linux (requires the platform matrix);
- plaintext/log/temporary-file inspection;
- executing the blocked-network trace in CI (the Linux step is wired but has not run here);
- calibrating the provisional 200,000-iteration count on the slowest supported machine;
- independent cryptographic, persistence, test and architecture review.
