# dot-finance specification

## Goal and scope

Provide a modern, offline desktop application for reconciling credit-card spending and calculating contributions from multiple funding sources. The app records manual acknowledgements; the user performs every transfer and card payment outside the app. Version one supports credit-card transaction reconciliation through standard CSV exports.

Use C# and .NET 10. Support macOS, Linux, and Windows, with macOS as the primary platform. An iOS app is possible future scope. Choose the desktop framework and local database technology during planning; no hosted database is required.

Absolutely no internet calls: no financial-institution connections, payment initiation, telemetry, exchange-rate lookups, cloud-storage integration, or other external services.

## Accounts and allocation rules

Distinguish the account a purchase was paid from from the funding source responsible for it. Accounts, categories, funding rules, protected minimum balances, and personal funding priority are configurable in the app.

Funding roles are Joint, Shared Savings, and Personal. Account names and category mappings are configured by the user during setup; do not seed household-specific mappings.

Recognised categories without a Joint or Shared Savings mapping default to Personal. Uncategorised transactions still require review.

Support a separately reported category within a funding source, such as a designated subcategory within a shared funding source. Show the selected category and other spending separately, plus the combined funding total. Produce one combined transfer for that funding source; separate reporting does not create a separate funding account.

Optional subcategory rules take precedence over category rules. Allow individual purchase overrides while supporting categorisation maintained in the source export.

Rule changes recalculate unsettled transactions in an unlocked draft, with a preview. Preserve explicit purchase overrides and never recalculate locked or completed reconciliations.

## Transaction import and initial history

Import transaction CSV exports with these fields:

```text
ID, Date, Amount, Account, Bank, Currency, Category, Subcategory, Type, Tags, Counterparty, Custom Name, Merchant, Additional details, Notes, Linked transaction ID
```

Retain all fields. Display ID, Date, Amount, Account, Bank, Category, Type, Counterparty, Merchant, and Notes in the main transaction list. Verify the actual delimiter, quoting, date formats, amount signs, and transaction types before implementing the parser; support for a particular delimiter must be based on the export format, not the presentation of this field list.

Transaction ID is unique. Re-importing an ID must not create a duplicate purchase or reimbursement. Update changed unsettled transactions in unlocked drafts. Changes to settled transactions require explicit review: accepted corrections create adjustments in the next reconciliation, preserving the original history. For example, reassigning a settled purchase from Personal to Joint creates a reimbursement of the same amount from Joint next time.

Initial setup accepts a baseline CSV containing only transactions the user confirms are already settled. Import all IDs as already settled and retain searchable transaction history. Do not invent historical transfer amounts or balances. Full reconciliation history begins with the first reconciliation managed in the app.

Include credit-card purchases and qualifying bills paid from configured personal accounts. Personal-account purchases categorised as Joint or Shared Savings generate reimbursements from that funding source through the intermediate account, reducing the personal contribution. They do not change the card payment target. Exclude account transfers and credit-card repayments from purchase allocations.

Uncategorised transactions require review before transfers can be confirmed. Version one uses GBP only. Non-GBP transactions require review and the actual GBP charged amount before reconciliation; no automatic currency conversion.

## Reconciliation and payment target

Date each reconciliation when the user initiates it and imports transactions. There are no fixed monthly or statement-date periods. Include eligible unsettled imported transactions, including late imports dated before the previous reconciliation. Use ID and settlement status, not date alone, to prevent counting a purchase twice.

The user enters the card balance to pay. This is the authoritative payment target. It may differ from the net imported total because the window is unconstrained. Show the discrepancy and allow the user to proceed after acknowledging it.

Only one unfinished reconciliation is permitted.

## Funding and protected balances

The user enters current balances for each relevant account at every reconciliation. These already reflect bills paid from those accounts; never deduct those bills a second time.

Gather funding in a user-selected intermediate account, then pay the card balance from it. Each account has a configurable non-negative protected minimum, explicitly set during account setup. Zero is permitted. There are no prescribed monetary defaults. Funding roles include the intermediate account, a primary personal account, personal savings sources, Joint, and Shared Savings.

Treat money at or below a minimum as unavailable. Calculate transfers to leave at least the intermediate minimum after the card payment and all other required outgoing transfers. Include any funding needed to restore a balance already below its minimum.

Joint and Shared Savings cover their allocated purchases and reimbursements in full. Personal funds must not subsidise their responsibilities. Refunds reduce the contribution owed by the original purchase's funding source; if the original cannot be identified, use the refund's category mapping, with an override available.

If a contribution is negative, show a transfer back from the intermediate account to that source and include it in the funding calculation. If reimbursements exceed what the card payment needs, still reimburse in full and leave the surplus in the intermediate account.

Personal funding order:

1. Available money already in the intermediate account above its minimum.
2. Available money above the personal primary account's minimum.
3. Personal savings in configurable priority order, respecting their minimums. Support one or more personal savings sources.

Using signed Joint and Shared Savings contributions (positive means incoming to the intermediate account; negative means outgoing), calculate:

```text
Additional personal transfers = max(0,
    Card payment target + intermediate minimum
    - current intermediate balance
    - Joint contribution - Shared Savings contribution)
```

Show transfers by source and projected balances after transfers and the card payment. Show funding needed to restore a minimum separately within its transfer total.

If Joint or Shared Savings lacks available funds, show the shortfall and block confirmation of the affected transfer. Do not shift its contribution to Personal. If combined available personal funds are insufficient, warn with the shortfall and block completion. A plan that breaches a protected minimum cannot be treated as fully funded.

## Guided workflow and locking

Provide a dashboard showing current progress and access to history. Initial workflow:

1. Import.
2. Review allocations with live category totals alongside the transaction list, resolve required reviews, and enter current balances and the card payment target.
3. Review transfers, discrepancies, and projected balances.
4. Perform transfers outside the app and acknowledge each transfer separately.
5. After required funding transfers are confirmed, pay the card balance outside the app and acknowledge “Card paid”.

Each transfer acknowledgement confirms its full suggested amount. Partial transfers are out of scope for version one. These are manual records, not banking operations.

All included transactions, including personal-account reimbursements, become settled together when the user acknowledges “Card paid”. Individual transfer acknowledgements track funding progress only. Locked transactions remain reserved to their reconciliation and cannot be included again while awaiting final settlement.

The first confirmed transfer locks the reconciliation's transactions and agreed amounts. Before that, freely correct and recalculate the draft. Subsequent imports wait for the next reconciliation without changing locked amounts.

If a re-import changes a transaction in a locked, unfinished reconciliation, flag the change immediately and preserve the agreed transfers. Queue the correction for explicit review as an adjustment in the next reconciliation.

Allow current account balances to be updated after locking, for example after an external top-up resolves a funding block. Agreed transfers remain fixed. Updated balances describe the current state: do not apply confirmed transfers a second time when projecting remaining steps.

Allow correction of accidental acknowledgements. The latest completed reconciliation can be reopened only if no newer reconciliation has started. Warn that reopening changes records and does not reverse bank transfers or card payments. Older reconciliations remain read-only.

Undoing an accidental transfer acknowledgement marks that transfer as outstanding again. The reconciliation stays locked and all agreed amounts remain fixed. This changes the record only; it does not imply that money moved back.

## History and presentation

Preserve completed reconciliation dates, transaction sets, allocations, totals, balance snapshots, agreed transfers, and acknowledgements. Make previous reconciliations and their transactions inspectable.

Show totals by funding account, purchases by category, separate reporting-category subtotals within their funding sources, transfer amounts and direction, shortfalls, and projected personal balances. Use British pound formatting.

While reviewing transactions, keep a category-totals sidebar visible alongside the transaction list, including while scrolling through transactions. Show running totals for the current reconciliation draft and update them immediately when an accepted transaction correction, allocation override, or rule change affects that draft, without requiring navigation to another screen or a manual refresh. Use the same draft state for the list and totals; unapplied rule-change previews must not replace the current totals. Include refunds using their signed effect and keep uncategorised amounts visible as requiring review. Preserve funding-source totals and separate reporting-category subtotals where applicable. A final confirmation/acknowledgement screen may summarise the agreed amounts, but does not replace the live totals during transaction review. Locked and completed amounts retain the existing immutability rules.

Support dark and light modes throughout the application, including transaction tables, dialogs, warnings, and history. Provide a theme setting with System, Light, and Dark options, defaulting to System, and remember the selection across restarts. Maintain readable contrast and distinguish funding shortfalls, validation errors, and completion states in either theme without relying on colour alone. Include both themes in the UI prototype review.

## Encryption and backups

Encrypt the working database and backups using the same user-provided password. Unlock once per app session. Advise the user to retain the password securely. No recovery service: make clear at setup that losing the password means losing access to the encrypted data.

Provide manual backup and restore. Automatically create an encrypted backup in a chosen local folder when a reconciliation completes. Backups must restore across supported platforms.

Support user-managed transfer of encrypted backup files between machines. The transport method is outside the application; the app must not connect to external storage services or perform online synchronisation.

## Testing

Generate synthetic financial data; do not use real financial data for development or automated testing. Cover duplicate IDs, settled baseline imports, late imports, corrections, category and subcategory rules, overrides, personal-account reimbursements, refunds, negative contributions, payment-target discrepancies, protected minimums, insufficient funds, surplus reimbursements, locking, balance updates after transfers, reopening, and encrypted backup/restore.

Verify that the category-totals sidebar remains visible during transaction review and scrolling, updates from the same draft state after accepted changes, and agrees with the final confirmation totals. Cover refunds, uncategorised transactions, and unapplied versus accepted rule-change previews. Include the sidebar in Light and Dark prototype review.

## Planning boundary

Use GitHub Issues and a GitHub Project for development ticketing. Use LikeC4 for versioned architectural diagrams, kept current with implementation changes and validated and generated as part of the full build. See [development-workflow.md](development-workflow.md) for the proposed agent loop and validation gates.

The requirements above capture the refinement decisions. Confirm shared understanding before beginning implementation planning.

The desktop framework, database and encryption libraries, parser details, and implementation structure remain planning decisions.
