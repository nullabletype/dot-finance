# ADR-0002: Avalonia and .NET for the desktop application

- **Status:** Accepted
- **Date:** 2026-09-27

## Context

dot-finance must ship as an entirely offline desktop application on macOS, Windows, and Linux. The prototype phase tested an Avalonia 12 application on .NET 10 across all three operating-system families, including native launch, responsive Light and Dark layouts, encrypted persistence, cross-platform backup restoration, and the guided reconciliation workflow.

## Decision

Use **Avalonia on .NET 10** as the desktop direction for production planning. Retain the product behaviour and evidence from the prototype, but build the production application outside `prototypes/`; prototype code is not promoted merely because the stack was selected.

## Rationale

- The stack keeps the implementation in the repository's selected, supported .NET ecosystem while providing one native desktop UI codebase for all three required platforms.
- The exact prototype has launched and exercised its principal workflow on macOS, Windows, and Linux rather than relying on cross-compilation alone.
- Avalonia supports the required local-only application boundary, responsive layouts, keyboard-oriented desktop interaction, and Light, Dark, and System themes.

No competing UI framework was implemented during the bounded prototype phase. The decision is based on Avalonia satisfying the defined cross-platform and workflow gates, not on a claim that every alternative was exhaustively benchmarked.

## Consequences

- Production planning must define project boundaries, navigation, accessibility, packaging, signing, update policy, and platform-specific validation rather than treating the disposable shell as the production architecture.
- Supported Avalonia, .NET, native, and transitive dependencies remain an acceptance gate for every production increment.
- Manual screen-reader and keyboard checks remain necessary; screenshot and control-tree automation do not establish accessibility.
- The authored LikeC4 prototype views remain labelled experimental until production boundaries are implemented and documented.
