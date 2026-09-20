# Domain documentation

This repository has one domain context. Read `spec-file.md` for approved behaviour and `development-workflow.md` for delivery policy. Record architecture decisions under `docs/adr/` when evidence supports them. Authored architecture models belong under `docs/architecture/`.

Funding source means the account responsible for a purchase; payment account means the account it was paid from. Shared Savings and Personal Savings are distinct. The intermediate account gathers funds before the card payment. Reserved transactions belong to a locked reconciliation; settled transactions have passed final “Card paid” acknowledgement.

Do not infer implementation readiness from a diagram: proposed and experimental views must be labelled as such.
