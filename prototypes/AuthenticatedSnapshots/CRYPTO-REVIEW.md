# Cryptographic review brief — authenticated snapshot envelope (issue #13)

This is the input for the independent cryptographic-format review required before the snapshot storage candidate can be selected. Read `README.md` (the format spec) and `VaultSession.cs` alongside this brief.

## Scope

Review the envelope format, key lifecycle, and authentication boundary of the disposable authenticated-snapshot prototype (`prototypes/AuthenticatedSnapshots/`). The prototype uses only the .NET 10 BCL (`Rfc2898DeriveBytes.Pbkdf2`, `AesGcm`, `RandomNumberGenerator`); it implements no cipher, KDF, or MAC of its own. Assess whether the format is sound for an offline, single-user, personal-finance vault, and state any blocking findings.

## Envelope (format version 1)

Fixed 72-byte header, all unsigned integers big-endian, authenticated as AES-GCM associated data (AAD):

| Offset | Len | Field |
| ---: | ---: | --- |
| 0 | 8 | ASCII magic `DFSNAP01` |
| 8 | 2 | format version `1` |
| 10 | 1 | KDF ID `1` = PBKDF2-HMAC-SHA-256 |
| 11 | 1 | AEAD ID `1` = AES-256-GCM |
| 12 | 4 | PBKDF2 iterations (provisional `200000`) |
| 16 | 16 | per-key random salt |
| 32 | 16 | RFC 4122 vault id, big-endian byte order |
| 48 | 4 | generation (max `2^32-1` per key) |
| 52 | 12 | 96-bit nonce from `RandomNumberGenerator` |
| 64 | 8 | ciphertext length (max 512 MiB) |

Then ciphertext, then a 16-byte GCM tag. Plaintext is UTF-8 JSON (schema version, transactions, configuration, reconciliations), parsed only after authentication. The full header is AAD, so magic, version, KDF/AEAD ids, iterations, salt, vault id, generation, nonce, and length are all authenticated.

## Key lifecycle

- Key = `Rfc2898DeriveBytes.Pbkdf2(passwordUTF8, salt, 200_000, HMAC-SHA-256, 32)`.
- Password encoded directly as UTF-8, no Unicode normalization; empty/whitespace-only rejected.
- Salt: 16 random bytes, one per key (per vault creation / password rotation).
- Nonce: 12 random bytes per save; uniqueness checked against retained generations in the lineage (8 retries, then fail). The generation cap follows the NIST SP 800-38D random-IV invocation limit.
- Password rotation: new salt + key, same logical state re-encrypted as the next generation; existing backups deliberately keep their old password.
- Key, plaintext, and temporary password-byte buffers are zeroed with `CryptographicOperations.ZeroMemory`; the caller's managed `string` password cannot be cleared (documented).

## Review questions (answer each)

1. **Nonce management.** Is a random 96-bit nonce with the SP 800-38D invocation cap plus the retained-generation uniqueness check sound? Is the cross-copy/fork bound acceptable — a copied vault can be forked outside this single-writer process, and the retained-generation check does not see the other copy?
2. **KDF.** Is 200,000 PBKDF2-HMAC-SHA-256 iterations with a 16-byte salt appropriate? (The value is provisional and must be calibrated on the slowest supported machine.)
3. **Associated data.** Is the header as AAD complete — is anything that must be bound (lineage, generation, length) missing from AAD?
4. **Rollback limitation.** Authentication does not detect replacement by an older, otherwise-valid complete snapshot (no trusted monotonic anchor). Is this acceptable for the threat model (offline, single user, attacker with local file access)?
5. **Password policy.** Is UTF-8-without-normalization plus reject-empty/whitespace adequate? Any normalization or downgrade concern?
6. **Key clearing.** Are the zeroing points sufficient given managed-memory limits?
7. **Wrong-password/tamper behaviour.** Unlock returns one generic authentication failure. Confirm there is no oracle and no distinct error channel.
8. **Blocking findings.** Anything that would reject the format for its stated purpose?

## Out of scope (documented)

- Malicious rollback detection (requires external trusted monotonic state).
- A custom cipher/KDF/MAC (the prototype uses BCL primitives only).
- Nonce/key coordination across copied vaults beyond the retained-generation check.

## Supporting evidence already present

- Known-answer vectors for PBKDF2 (RFC 7914) and AES-256-GCM (NIST) — `AuthenticatedSnapshots.Tests/CryptographicKnownAnswerTests.cs`.
- Hostile-input, malformed-payload, password-policy, and schema/downgrade tests — `AuthenticatedSnapshots.Tests/`.
- Abrupt-interruption recovery and filesystem-fault tests.

Run: `dotnet test prototypes/AuthenticatedSnapshots.Tests/AuthenticatedSnapshots.Tests.csproj`
