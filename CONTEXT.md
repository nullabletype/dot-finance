# Reconciliation

One offline context for allocating responsibility and recording manual settlement.

## Language

**Payment account**: The account from which a transaction was paid. It is distinct from responsibility for funding it.

**Source transaction type**: The provider-supplied transaction `Type` value retained exactly during import. It describes the source record but does not by itself decide reconciliation eligibility or funding responsibility.

**Import classification**: A conservative interpretation of an imported row as a purchase candidate, linked refund candidate, excluded transfer, or requiring review. It never assigns funding responsibility.

**Funding source**: Joint, Shared Savings or Personal responsibility for a purchase and its linked refunds.

**Intermediate account**: The account that gathers contributions and pays the entered card target.

**Protected minimum**: The configured balance that must remain unavailable for funding.

**Reconciliation draft**: The one unfinished reconciliation, dated when it is started and containing eligible unsettled transactions.

**Reserved transaction**: A transaction included in a locked reconciliation awaiting final card-payment acknowledgement.

**Settled transaction**: A transaction confirmed settled in the initial baseline or by final Card paid acknowledgement.

**Correction**: A changed reserved or settled transaction requiring explicit review before an adjustment is included in a later reconciliation.

**Adjustment**: The difference between previously accounted responsibility and an accepted corrected responsibility, preserving historical records.

**Acknowledgement**: A manual record that the user performed the full suggested transfer or card payment outside the app.
