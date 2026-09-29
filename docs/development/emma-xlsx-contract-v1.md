# Emma XLSX import contract v1

Contract identifier: `emma-xlsx-v1`.

## Accepted workbook structure

- An unencrypted `.xlsx` package using the Excel 1900 date system.
- XLSX is a ZIP/XML package, so CSV delimiter and quoting rules do not apply. XML-part encodings must be declared and supported by the conforming Open XML package reader; UTF-8 and UTF-16 are covered explicitly. The contract does not accept a caller-selected text encoding, use the operating-system locale, or silently reinterpret workbook bytes.
- Exactly one visible worksheet, with no hidden rows or columns, formulas, macros, external relationships, embedded content, drawings, calculated chains, or defined names.
- The exact sixteen headers and order recorded in [the evidence note](emma-xlsx-evidence.md).
- `Date` is a text cell in exact `yyyy-MM-dd` form.
- `Amount` is a numeric cell using the General number format. The verified lexical form is an optional sign, decimal digits, and an optional decimal point followed by decimal digits. It must be exactly representable by .NET `decimal`; exponent notation, locale separators, non-finite values, and values requiring rounding are rejected rather than coerced.
- Populated non-amount fields are text cells. `ID`, `Account`, `Currency`, and `Type` are required. Currency is an uppercase three-letter code.
- Empty optional source fields remain empty strings. Source text and IDs are retained exactly; the importer does not trim, fold case, convert numeric identities, or deduplicate rows.

Any structural or field error rejects the import atomically with stable issue codes and no partial records. Empty rows are ignored as data but still count towards the 25,000 source-row limit. Rows are bounded to the sixteen contract cells; shared strings and cell formats are bounded and indexed once (250,000 and 65,536 items respectively). A 32 MiB input limit, 2,048-entry package limit, 128 MiB per-entry and aggregate decompression limit, and the structural limits bound hostile or accidental input.

## Conservative source classification

| Source evidence | Classification | Required review |
| --- | --- | --- |
| Negative `Purchase`, `Direct Debit`, `Standing Order`, or `Bill Payment` | Purchase candidate | Actual GBP amount when currency is not GBP |
| Positive `Credit` with a linked transaction ID | Refund candidate | Actual GBP amount when currency is not GBP |
| `Transfer` | Excluded transfer | None at the import boundary |
| Positive unlinked `Credit` | Review required | Unlinked credit; actual GBP amount when applicable |
| Recognised spending/credit type with a contradictory sign or zero amount | Review required | Contradictory amount direction; actual GBP amount when applicable |
| `Unknown` or any unfamiliar non-empty type | Review required | Source type; actual GBP amount when applicable |

Classification describes only the source row. It does not assign a configured payment-account identity, funding responsibility, reconciliation eligibility, an adjustment, or settlement state. Those decisions remain with the Application and Domain boundaries.

## Versioning and variants

The contract accepts only the verified variant. A future Emma vocabulary value is data, not a new workbook variant: it is preserved and routed to review. A different header, cell representation, date system, worksheet layout, or package feature requires new sanitised structural evidence and an explicit versioned contract change.

The test project commits a base64-encoded synthetic XLSX snapshot for the verified structure and constructs targeted fictional package mutations for malformed and limit boundaries. Real exports, values, filenames, reports, and row counts remain outside source control, GitHub, agent context, CI, and logs.
