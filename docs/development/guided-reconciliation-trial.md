# Guided reconciliation trial

## Decision

Owner decision on 2026-09-26: **accept the guided workflow prototype and continue**.

This accepts the prototype as evidence for the issue #3 product question. It does not promote the disposable Avalonia shell to production architecture or approve release, packaging, unattended execution, or real financial data.

## Owner feedback incorporated

The initial functional walkthrough was revised before acceptance to:

- use a modern, framed workbench layout with persistent workflow navigation and a contextual review inspector;
- provide account, category, status, and text filtering alongside a spreadsheet-like transaction list;
- increase general, header, and navigation typography while retaining clear hierarchy;
- replace ambiguous filled glyphs with recognisable outline icons and use the supplied dot-finance mark;
- request the encryption password only when saving or reopening rather than keeping it in the permanent header;
- align status pills, form fields, buttons, and the logo wordmark optically;
- keep amount columns clear of scrollbars and constrain compact layouts to internal horizontal scrolling;
- truncate long merchant, account, and category values within their own columns instead of allowing overlap;
- keep blocked discrepancy and shortfall actions visibly unavailable without terminating the application.

The final owner response after these corrections was: “Great :-) I'm happy to continue”.

## Local evidence

The accepted working tree passed:

```text
npm run build
npm run workflow:smoke
```

`workflow:smoke` checks the expected synthetic calculation outcomes and captures ten native screenshots: Light and Dark before/after correction, plus the compact-window ledger, balances, and rules layouts in both themes. Compact evidence asserts that filters and the transaction ledger scroll within clipped frames without crossing into the inspector, balance labels stay within their column, and paired rule controls stay within their row.

## Independent review follow-up

- Independent review on 2026-09-27 found four behavioural gaps in the integrated prototype: unresolved review items did not block acknowledgement, completion did not advance to a new draft, changed re-imports were silently skipped, and non-GBP rows did not require an actual GBP amount. Issue #25 owns the bounded repair and its fresh exact-commit review/hosted evidence.
- The issue #25 repair also closes review-discovered edge cases around locked corrections, correction replay and sequencing, preserved funding responsibility, foreign-currency display, history inspection, safe reopening, and legacy saved-state migration. Two independent final re-reviews found no remaining actionable spec, correctness, standards, or quality findings. Local build and encrypted-persistence checks pass with 38 workflow tests; fresh hosted exact-commit workflow evidence remains required.
- The earlier integrated prototype passed hosted macOS, Windows and Linux workflow validation plus the cross-platform backup chain in run 36311078900. That evidence predates issue #25 and cannot validate its repair commit.
- The prototype remains disposable and synthetic-only. Production architecture, importer formats, migrations, packaging/signing, security suitability, and large-dataset behaviour remain undecided.
