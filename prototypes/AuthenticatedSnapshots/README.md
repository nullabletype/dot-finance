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

## Missing evidence

Until completed, these keep the recommendation at **rework / no storage selection**:

- published PBKDF2 and AES-GCM known-answer vectors;
- bounded hostile KDF/length and broader malformed-payload tests;
- abrupt termination at every save/pointer/backup/restore/password-change phase;
- disk-full, permission/removal and stale-partial trials;
- explicit schema migration and downgrade behavior;
- durable automatic-backup pending/retry state tied to reconciliation completion;
- pairwise/cycle evidence on macOS, Windows and Linux;
- plaintext/log/temporary-file inspection and blocked-network trace;
- independent cryptographic, persistence, test and architecture review.
