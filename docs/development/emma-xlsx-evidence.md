# Emma XLSX export evidence

Verified 2026-09-28 from an owner-operated local inspection of a private Emma export, with the sign convention confirmed by the owner on 2026-09-29. The source workbook remained on the owner's machine and was not supplied to the repository or retained as development evidence. The generated synthetic workbook was reviewed in place and is intentionally not committed.

The sanitised schema-version-3 JSON report has SHA-256 `dbb379b2feb7056a09e5b80247bc076ec4dd51a56cc3eee9dbb267fe0d6599ee`. This document records its non-sensitive structural findings rather than copying the report into source control.

## Verified variant

- Container: unencrypted `.xlsx` using the Excel 1900 date system.
- Worksheet selection: exactly one visible worksheet.
- Header order: `ID`, `Date`, `Amount`, `Account`, `Bank`, `Currency`, `Category`, `Subcategory`, `Type`, `Tags`, `Counterparty`, `Custom Name`, `Merchant`, `Additional details`, `Notes`, `Linked transaction ID`.
- `Date`: stored as text. The owner confirmed the fixed `yyyy-MM-dd` syntax using the non-sensitive example `2026-02-28`.
- `Amount`: stored as a numeric cell with the General number-format class. Ordinary purchases are negative. Refunds and credit-card payments on the card side are positive.
- `ID`, `Account`, `Bank`, `Currency`, `Category`, `Type`, `Tags`, `Counterparty`, `Merchant`, `Additional details`, `Notes`, and `Linked transaction ID`: stored as text when populated.
- `Subcategory` and `Custom Name`: no populated value kind was observed in this export. The columns remain part of the contract and must accept text in other verified variants.
- Observed `Type` vocabulary, without frequency information: `Bill Payment`, `Credit`, `Direct Debit`, `Purchase`, `Standing Order`, `Transfer`, and `Unknown`. This is evidence of values present in the inspected export, not an exhaustive list of every value Emma may emit.

## Contract decision

- On 2026-09-29 the owner approved the conservative source-classification policy recorded in [Emma XLSX import contract v1](emma-xlsx-contract-v1.md).
- Negative `Purchase`, `Direct Debit`, `Standing Order`, and `Bill Payment` rows are purchase candidates. Positive linked `Credit` rows are refund candidates. `Transfer` rows are excluded from purchase allocation.
- Literal `Unknown`, unfamiliar source types, contradictory amount directions, and unlinked positive credits are retained and require review rather than being guessed or causing the whole workbook to be rejected.
- Non-GBP rows are retained and require an actual GBP amount before reconciliation; the importer performs no conversion.

Other Emma workbook variants remain unverified and are rejected with actionable errors rather than silently coerced. They can be added only after equivalent privacy-preserving structural evidence. The parser does not infer meaning from the generated synthetic workbook: its values are fixed fictional placeholders designed to preserve structure without reproducing source values or source frequencies.
