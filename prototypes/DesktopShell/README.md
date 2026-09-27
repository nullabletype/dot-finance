# Experimental desktop, encrypted persistence, and guided reconciliation — issues #1 to #3

This disposable Avalonia/.NET 10 shell exercises local workflows with fictional data: import committed synthetic CSVs, review live allocation totals, enter balances and the card target, inspect protected-balance projections, acknowledge external transfers, settle the card payment, save encrypted state, and reopen completed history. It never connects to a bank or initiates a payment.

The values are synthetic experiment inputs, not product defaults. The persistence experiment stores synthetic transaction fields plus sample allocation and settlement state; it is not production data modelling or a complete importer.

## Run from the repository root

Install the stable .NET SDK selected by `global.json` and a supported Node release accepted by `package.json`. CI uses Node 24.21.0 LTS. Dependencies are pinned in the project/package manifests and lockfiles.

```sh
npm ci
npm run build
npm run smoke
npm run persistence:smoke
npm run workflow:smoke
npm start
```

Development restore requires package-registry access. The published app itself has no networking feature. `npm run build` validates and generates LikeC4, rejects an intentionally invalid model, restores locked dependencies, builds with warnings as errors, runs behavioral tests, checks SDK formatting, and publishes the shell.

## Guided reconciliation walkthrough

1. Start with `npm start`. Enter a temporary session password if you want to save and reopen the scenario; it is not persisted.
2. Import **base card purchases**. Re-import it to see all six IDs skipped. On Review, confirm Joint £450, Shared Savings £250, Personal £400, Holidays £150, one £25 uncategorised item requiring review, and a £475 card-target discrepancy.
3. Preview changing Groceries to Personal. The preview text changes, but the live sidebar remains on the accepted draft. Apply the rule or use an allocation override to see the live and transfer totals change immediately.
4. Correct `R-0005` to category `Groceries`. The warning clears and Joint becomes £475. Restart for the untouched canonical fixture before continuing with the issue totals.
5. Import the base fixture and the £60 Joint bill. Confirm Joint £510, Shared Savings £250, Primary £200, Personal Savings £140, projected intermediate £80, and projected primary £120. The entered balances have not been reduced by the bill a second time.
6. On Acknowledge, explicitly acknowledge the target discrepancy. Acknowledge the first transfer to lock the transaction set and agreed amounts; undo it and confirm the draft remains locked. New imports are queued for the next reconciliation.
7. After performing a confirmed transfer outside the prototype, update the affected current balances on Review. Projections apply only outstanding steps, so the confirmed transfer is not counted twice.
8. Acknowledge every funding transfer, then **Card paid**. All reserved transactions settle together and the completed amounts appear under History. Save, restart, enter the same password, and Reopen to inspect the restored history.
9. For the refund path, restart and import only **refund demo**: the £150 refund against a £100 Joint purchase produces a £50 outgoing reimbursement. For the shortfall path, lower Joint below the £450 required amount; its acknowledgement is labelled Blocked and completion remains unavailable.
10. Switch System, Light, and Dark themes. The selection persists across restarts independently of the encrypted scenario.

Unresolved review items block every transfer acknowledgement and the final card acknowledgement. After **Card paid**, queued imports become the next editable draft while the settled record remains under History. The latest completion can be reopened only before any newer reconciliation activity; reopening changes the local record and never implies that an external transfer was reversed.

Re-importing an unchanged ID remains a duplicate. A changed ID updates an unlocked draft, but a changed locked or settled ID leaves the frozen record untouched and is retained for explicit review in the next draft. Accepting it creates a reversal of the preserved original plus a replacement from the corrected import, so category and amount changes become a net adjustment without rewriting history. The fictional `synthetic-non-gbp.csv` fixture remains in review until a signed actual GBP amount is entered; that GBP value, not the foreign amount, drives the draft totals. No conversion is attempted.

`npm run workflow:smoke` repeats the calculation path and captures Light and Dark Review screenshots before and after accepting the `R-0005` correction, plus the compact-window layout in both themes. Its JSON records expected and actual base, correction, £60 bill, refund, duplicate, insufficient-funds, and responsive containment outcomes. This evidence supports the owner walkthrough; it does not replace the required owner accept/revise decision.

## Persistence design under test

- `PrototypeVault` is the agreed behavioral seam. It stores JSON state documents in SQLite encrypted by SQLite3MC, using the password supplied for the current process.
- The working database and backup are both SQLite3MC databases protected by the same password. The password is retained in process memory while a vault is open but is not written to app settings, evidence, logs, or files.
- SQLite transactions protect saves. Backups are written as a sibling `.partial` file, validated, and atomically renamed; restore validates a sibling candidate before replacing working state.
- Wrong passwords and SQLite3MC-detected corruption produce a generic authentication/damage failure. They do not replace an existing working database.
- `npm run persistence:smoke` performs create → abrupt save interruption → recovery → abrupt backup interruption → recovery → two backup moves. It writes only local evidence under `artifacts/persistence-local/`.

### Alternatives considered

1. **SQLite3MC-backed SQLite — selected.** Established full-database encryption (default sqleet ChaCha20-Poly1305 cipher), authenticated pages, SQLite transactions, and portable native packages let the prototype test storage and backup without designing a cryptographic envelope. SQLite3MC is the maintained successor to the deprecated SQLCipher bundle.
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

`npm run smoke` captures native launch evidence as described by issue #1. `npm run workflow:smoke` captures the issue #3 Review state in both themes before and after an accepted correction and checks the recorded expected/actual totals. The persistence tests cover exact reopen/restore equality, wrong passwords, a tampered backup, cancellation before save commit, and cancellation before backup replacement. The persistence smoke abruptly terminates child processes before a save commit and after a backup is staged, then starts a new process to verify recovery. It confirms the complete backup hash did not change and the interrupted output remains visibly suffixed `.partial` rather than being presented as complete.

CI runs the tests and local persistence smoke on all three OS families. A sequential job moves only the encrypted backup macOS → Windows → Linux → macOS through bounded job outputs and compares the restored state digest at each stage. Each restore job rebuilds the prototype from the same commit; test data, desktop builds, screenshots, traces, architecture output, and other validation evidence are not uploaded to GitHub Actions artifact storage. The Linux job also runs the persistence scenario in a network namespace with networking blocked and traces network syscalls; any socket/connect/send attempt fails the gate. Hosted evidence applies only after those jobs run for a clean commit.

Generated outputs include:

- `artifacts/desktop/`: framework-dependent application and synthetic fixture;
- `artifacts/architecture/`: interactive experimental architecture site;
- `artifacts/persistence-local/`: encrypted files and local round-trip evidence;
- `artifacts/workflow-local/`: ten responsive theme screenshots plus expected/actual guided-workflow evidence, including compact balance and rule panels;
- `artifacts/offline-network/`: Linux blocked-network trace in CI;
- `artifacts/launch-*/` and `artifacts/build.json`: native launch and tested-source metadata.

Byte-pattern inspection inventories every file in the app-managed scenario workspace—including interrupted journals and `.partial` files—and checks for fixture IDs, merchant names, and the supplied password. Encrypted database files are also checked for the SQLite plaintext header. The prototype emits no application log. This is not a forensic audit of process memory, swap, filesystem snapshots, crash dumps, OS telemetry, or deleted blocks. The plaintext development fixture sits outside the managed workspace and remains in the published prototype solely to make the disposable walkthrough repeatable.

## Current decision

Decision (#12): SQLite3MC is selected as the maintained replacement for the deprecated SQLCipher bundle. Retain this vault seam for the guided-workflow experiment; rework is still required before production use. The owner accepted the guided workflow direction on 2026-09-26. The first independent review on 2026-09-27 found four workflow gaps tracked by #25; the bounded repair and follow-up edge cases now have clean independent re-reviews, while fresh hosted exact-commit evidence remains required before the issue #5 decision gate. The prototype does not establish password recovery, production migrations, large-dataset behavior, packaging/signing, final security suitability, or production UI architecture.
