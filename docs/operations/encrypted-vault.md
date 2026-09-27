# Encrypted vault operation and recovery

The production vault is a SQLite3MC database. Working state and portable backups use the same password and format. There is no recovery service, key escrow or password reset: losing the password loses access to both files.

## Password boundary

Supply the password at unlock and retain it only for that application session. It is never a setting, filename, command-line value, log field or evidence value. The adapter keeps a mutable character copy only while the vault is open and clears that copy on disposal. Microsoft.Data.Sqlite requires an immutable managed connection string while the connection is open, so the runtime cannot promise immediate physical erasure from managed memory, swap or hibernation. Close the application when the session ends and use normal OS account, disk-encryption and screen-lock protections.

Wrong passwords and damaged or unauthenticated database pages return the same message. This avoids exposing an authentication oracle. Migration and filesystem replacement failures remain separate operational failures because retrying a password cannot fix them.

## Format and durability

Schema version 1 contains strict metadata and keyed entity tables. Configuration accounts and rules, imported records, settlement/effective views, corrections and conflicts, overrides, history, audit entries, used identities, current adjustments and baseline allocations use independently keyed rows. Dates, categories, funding sources, effective GBP minor-unit values, account roles and correction identities have relational columns and focused indexes; a trim-safe row payload preserves the full storage-neutral domain value. Saves upsert changed rows and delete stale keys in one transaction rather than rewriting a complete state document. Restore validates SQLite integrity, the schema version, authenticated decryption and every domain-state invariant before accepting a file. Version 1 does not claim full-text search or expose a relational query API; those belong to a later consumer-owned workflow and schema migration.

The file URI pins `chacha20`, non-legacy mode, authenticated-page checking, no plaintext header and 600,000 PBKDF2-HMAC-SHA256 iterations. Working saves use full-synchronous SQLite transactions and delete-mode journals. A cancelled or terminated save rolls back when the vault next opens.

## Backup and restore

Backup writes `<chosen-name>.partial` in the destination directory, uses SQLite's online backup API into a separately keyed destination connection, validates integrity and complete state, then replaces the chosen backup name. Cancellation, termination or a filesystem failure leaves the previous complete backup unchanged. A partial suffix is never presented as a valid backup.

The application completion use case completes a restored copy of the live draft, commits that copy to the working vault, then refreshes the configured encrypted backup. A cancelled or failed save leaves the caller's draft unchanged. If backup replacement fails, the error carries the completed aggregate that is already durable, the previous valid backup remains available, and the explicit backup retry does not repeat the financial transition.

Restore copies the selected backup to `<working-name>.restore-partial`, unlocks and validates it, then replaces the closed working database in the same directory. If a valid working database already exists, restore first opens it to complete any SQLite crash recovery. A wrong password, damaged backup, cancelled validation or failed replacement leaves that working database unchanged. After abrupt termination, startup can open the prior working database first and then discard the recognisably suffixed restore candidate; the partial file is never treated as the working vault.

Keep backups on storage controlled by the same OS user. Moving an encrypted backup between macOS, Windows and Linux does not change its password or format. Transport and cloud synchronisation remain outside the application.

## Native extraction and updates

The distributed application is one self-contained executable. SQLite3MC is bundled and extracted by the .NET host into an owner-controlled per-user cache. Repeated launches reuse complete files and recover a missing extracted library. A changed application bundle uses a different content-derived directory, so a binary update does not overwrite the previous bundle's native library. Do not configure `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a shared writable location.

Final installer paths, signing, upgrade policy and release recovery drills remain issue #32 work.
