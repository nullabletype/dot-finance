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

`workflow:smoke` checks the expected synthetic calculation outcomes and captures five native screenshots, including Light, Dark, before/after correction, and a compact-window layout. Compact evidence asserts that filters and the transaction ledger scroll within clipped frames without crossing into the inspector.

## Remaining gates

- Independent review has not yet been recorded.
- Hosted macOS, Windows, and Linux evidence must run for the exact committed revision; local captures are provisional while the working tree is dirty.
- The prototype remains disposable and synthetic-only. Production architecture, importer formats, migrations, packaging/signing, security suitability, and large-dataset behaviour remain undecided.
