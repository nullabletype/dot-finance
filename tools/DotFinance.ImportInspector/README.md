# Private XLSX export inspector

This small local tool collects the structural evidence needed by issue #30 without sharing a real export. It has no network code and no production application dependency. The source workbook is read into memory, analysed locally, and never copied by the tool.

The output directory contains only:

- `shareable-report.json` (the full inspection report, retained locally): the XLSX container type, Excel date system, allowlisted public header names or position-only replacement tokens, structural cell kinds, broad number-format classes, recognised fixed text-date syntax tokens, and stable validation codes;
- `synthetic-export.xlsx` (the deliberately minimised shareable report): eight fixed synthetic records using safe generated styles and fixed package timestamps. The row count never reveals whether the source contains transactions.

Both files are created with owner-only permissions where the platform exposes them: `0600` on macOS and Linux, and the user profile's inherited access control list on Windows.

Only header names from the tool's public specification allowlist are retained. Empty and unexpected headers become `EMPTY_COLUMN_n` or `UNEXPECTED_COLUMN_n`; their source text is discarded. The report does not include worksheet names, workbook properties, author details, custom number-format strings, transaction volume, or value-frequency summaries. The synthetic workbook uses a fixed public worksheet name and does not copy source values, styles, comments, metadata, relationships, or source row counts.

The JSON necessarily contains source-derived structural presence: for example, a reported cell kind, broad format class, or fixed text-date syntax means at least one sampled cell used that structure. Those facts contain no raw values, paths, names, counts, or aggregates, but they may still be sensitive under a strict threat model. Treat the files as potentially sensitive until you have reviewed them; the tool does not make a universal claim that every structural fact is safe to disclose.

The inspector fails closed and produces no synthetic workbook when it detects formulas, external or hyperlink relationships, macros, embedded objects or packages, external-workbook/query/connection/pivot/control parts, drawings, charts, images, comments, hidden sheets, hidden rows or columns, multiple worksheets, cells outside the declared headers, schema errors, malformed workbook relationships, or inspection limits being exceeded. Workbook-defined names produce a fixed warning and are not copied. Supported fixed text-date syntax is recorded as a format token without retaining a source value; ambiguous, inconsistent, or unsupported text dates produce a fixed warning. Text-stored amounts also produce a fixed warning because their value syntax is deliberately not captured. Password-protected, encrypted, corrupt, and non-XLSX inputs receive the same fixed unreadable-workbook diagnostic.

## Run it

Review this directory's source first. Restore the pinned dependencies before selecting the private export; restore can contact configured package sources, so keep the real workbook out of the command and working directory:

```sh
DOTNET_CLI_TELEMETRY_OPTOUT=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1 \
  dotnet restore DotFinance.slnx --locked-mode
```

Then run the repository launcher without arguments:

```sh
npm run import:inspect
```

The launcher disables .NET CLI and test-platform telemetry while building with `--no-restore`, then starts the compiled apphost directly. It accepts no command-line paths. Enter the private `.xlsx` export path and a new output directory at the hidden prompts. Direct symbolic-link paths are rejected and ancestor links are resolved before the directory-separation check. Use a local directory that is not watched by iCloud, Dropbox, OneDrive, backup software, or another synchronisation service.

The compressed input limit is 32 MiB. The package is also limited to 2,048 entries, 128 MiB total uncompressed content, 128 MiB per entry, 250,000 cells and 512 columns.

Exit code `0` means the structure was parsed without error findings, `2` means a sanitised report was produced with structural errors, `64` is invalid usage, `66` is an unreadable input file, `70` is a safely contained inspection failure, and `73` is an output failure.

Before sharing anything, inspect `shareable-report.json` in a plain-text editor and open `synthetic-export.xlsx` to confirm that it contains only obviously fictional values. Keep the full inspection report (`shareable-report.json`) local and share only the deliberately minimised report (`synthetic-export.xlsx`) through an explicitly private channel after reviewing it. Neither report may be committed, uploaded to GitHub, placed in agent context, copied into CI logs, or otherwise published. Never share the containing directory or the original export.

The JSON report is the authoritative structural observation. The synthetic workbook is a safe representative fixture, not a complete reproduction of every observed cell-kind and number-format combination and not yet the production Emma import contract.

This is an evidence-collection tool, not the production importer. It does not make reconciliation decisions, intentionally persist raw private values, or change the production LikeC4 boundaries. Private values necessarily exist transiently in managed process memory while parsing; operating-system swap, crash dumps, endpoint security tools, filesystem-level monitoring, and backups are outside the tool's control. Run it only on a trusted local machine.
