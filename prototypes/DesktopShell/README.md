# Experimental desktop and encrypted persistence — issues #1 and #2

This disposable Avalonia/.NET 10 shell now exercises one local path with fictional data: enter a session password, import the committed synthetic CSV, save encrypted state, reopen it, create an encrypted backup, and restore it with the same password. It never connects to a bank, calculates transfers, or initiates payments.

The summary amounts remain fictional constants. The persistence experiment stores synthetic transaction fields plus sample allocation and settlement state; it is not production data modelling or a complete importer.

## Run from the repository root

Install the stable .NET SDK selected by `global.json` and Node >=22.22.3. The workflow uses Node 24.15.0. Dependencies are pinned in the project/package manifests and lockfiles.

```sh
npm ci
npm run build
npm run smoke
npm run persistence:smoke
npm start
```

Development restore requires package-registry access. The published app itself has no networking feature. `npm run build` validates and generates LikeC4, rejects an intentionally invalid model, restores locked dependencies, builds with warnings as errors, runs behavioral tests, checks SDK formatting, and publishes the shell.

## Persistence design under test

- `PrototypeVault` is the agreed behavioral seam. It stores one JSON state document in SQLite encrypted by SQLCipher, using the password supplied for the current process.
- The working database and backup are both SQLCipher databases protected by the same password. The password is retained in process memory while a vault is open but is not written to app settings, evidence, logs, or files.
- SQLite transactions protect saves. Backups are written as a sibling `.partial` file, validated, and atomically renamed; restore validates a sibling candidate before replacing working state.
- Wrong passwords and SQLCipher-detected corruption produce a generic authentication/damage failure. They do not replace an existing working database.
- `npm run persistence:smoke` performs create → abrupt save interruption → recovery → abrupt backup interruption → recovery → two backup moves. It writes only local evidence under `artifacts/persistence-local/`.

### Alternatives considered

1. **SQLCipher-backed SQLite — selected provisionally.** Established full-database encryption, authenticated pages, SQLite transactions, and portable native packages let the prototype test storage and backup without designing a cryptographic envelope.
2. **Application-defined AES-GCM file plus a password KDF — rejected for this experiment.** The primitives are established, but choosing and versioning the envelope, KDF parameters, nonce handling, and migration protocol would create bespoke cryptographic design.
3. **Operating-system credential stores — rejected for this question.** They do not satisfy a user-password-protected backup that can move unchanged between macOS, Windows, and Linux.
4. **Plain SQLite inside an encrypted archive — rejected.** It introduces plaintext working storage and temporary-file handling that conflict with the experiment.

This selection is not a production ADR. Native dependency maintenance, licensing, package provenance, KDF tuning, migration, secure password handling, and a security review remain required before retention.

## Synthetic CSV assumptions

The committed fixture is fictional. For this bounded import only, it assumes:

- UTF-8, comma delimiter, exact headers from `spec-file.md`, and RFC-style double-quote escaping;
- invariant `yyyy-MM-dd` dates and dot-decimal signed amounts;
- GBP currency and transaction type values treated as strings;
- the first row receives sample Joint/unsettled state and the second sample Personal/settled state after import.

These assumptions are **not evidence of Emma's actual export format**. No real export was used. Delimiter, encoding, quoting, date/amount conventions, signs, and type vocabulary still require verification before a production parser.

## Validation and evidence

`npm run smoke` captures native launch evidence as described by issue #1. The persistence tests cover exact reopen/restore equality, wrong passwords, a tampered backup, cancellation before save commit, and cancellation before backup replacement. The persistence smoke abruptly terminates child processes before a save commit and after a backup is staged, then starts a new process to verify recovery. It confirms the complete backup hash did not change and the interrupted output remains visibly suffixed `.partial` rather than being presented as complete.

CI runs the tests and local persistence smoke on all three OS families. A sequential job moves the encrypted backup macOS → Windows → Linux → macOS and compares the restored state digest at each stage. The Linux job also runs the persistence scenario in a network namespace with networking blocked and traces network syscalls; any socket/connect/send attempt fails the gate. Hosted evidence applies only after those jobs run for a clean commit.

Generated outputs include:

- `artifacts/desktop/`: framework-dependent application and synthetic fixture;
- `artifacts/architecture/`: interactive experimental architecture site;
- `artifacts/persistence-local/`: encrypted files and local round-trip evidence;
- `artifacts/offline-network/`: Linux blocked-network trace in CI;
- `artifacts/launch-*/` and `artifacts/build.json`: native launch and tested-source metadata.

Byte-pattern inspection inventories every file in the app-managed scenario workspace—including interrupted journals and `.partial` files—and checks for fixture IDs, merchant names, and the supplied password. Encrypted database files are also checked for the SQLite plaintext header. The prototype emits no application log. This is not a forensic audit of process memory, swap, filesystem snapshots, crash dumps, OS telemetry, or deleted blocks. The plaintext development fixture sits outside the managed workspace and remains in the published prototype solely to make the disposable walkthrough repeatable.

## Current decision

Provisional recommendation: retain SQLCipher and this vault seam only for the guided-workflow experiment if the clean cross-platform cycle and independent review pass. Rework is required before production use. The prototype does not establish password recovery, production migrations, large-dataset behavior, packaging/signing, or final security suitability.
