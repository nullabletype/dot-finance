# Experimental desktop foundation — issue #1

Question: can Avalonia/.NET 10 display a native desktop window on macOS, Windows, and Linux while the same build validates and generates LikeC4 documentation?

The summary uses locally drawn vector icons alongside text labels; no icon downloads or additional packages are required. These presentation details do not change the LikeC4 architecture.

This is disposable code. The summary amounts are fictional constants. It does not calculate transfers, import files, save financial data, or make network requests. Theme selection and persisted preferences belong to the later workflow experiment.

## Run from the repository root

Install the stable .NET SDK selected by `global.json` and Node >=22.22.3. The workflow uses Node 24.15.0. Dependencies are pinned in the project/package manifests and lockfiles.

```sh
npm ci
npm run build
npm start
```

`npm ci` and .NET restore require access to package registries. These are development operations, not application networking. The .NET output requires the .NET 10 runtime on the destination machine and the platform's native desktop libraries. Signing, installers and self-contained distribution are outside this experiment.

## Validation and evidence

`npm run build` runs LikeC4 format and validation, confirms an intentionally invalid temporary model is rejected, generates the architecture site, restores locked NuGet dependencies, builds with warnings as errors, runs the SDK formatting gate, and publishes the desktop shell.

```sh
npm run smoke
```

Smoke mode launches the published assembly through the real platform backend. After its native window opens and is visible, it captures the control tree to PNG, writes OS/runtime/window metadata, and exits. The harness fails on timeout, nonzero exit, missing evidence or a changed assembly/commit. It does not use Avalonia's headless backend. The image is a control-tree render, not an OS desktop screenshot; this is launch evidence, not proof of input behaviour or full UI acceptance.

Generated outputs:

- `artifacts/desktop/`: framework-dependent application.
- `artifacts/architecture/`: interactive static architecture site.
- `artifacts/build.json`: source commit, dirty flag and published assembly hash.
- `artifacts/launch-*/`: fresh per-run PNG, launch metadata and build manifest.

Uncommitted local builds are explicitly marked provisional. Acceptance evidence must identify a clean tested commit. A failed build removes the old success manifest; rerun the full build before smoke verification.

On Linux CI, install Xvfb plus Avalonia's X11/font libraries and run `xvfb-run -a npm run smoke`. The workflow lists the installed packages. Windows/macOS smoke runs require a desktop session; infrastructure failure remains a failed/pending platform gate, not a pass.

GitHub Actions runs the full build and launch check on all three OS families, with a 20-minute job timeout. It uploads generated artifacts with the workflow commit in their names for seven days. Artifact access follows repository visibility; changing the repository to public also changes the audience of workflow artifacts. No Pages deployment is configured.

## Architecture review

The LikeC4 model describes only this implemented experiment: synthetic window, opt-in local evidence capture, and separate development tooling. It intentionally does not portray future storage or reconciliation logic as implemented. To inspect locally:

```sh
npx --no-install likec4 preview -o artifacts/architecture
```

## Decision

Provisional recommendation: retain the stack for the next experiment only after all three launch jobs and independent review pass. Issue #1 remains open until evidence is recorded and the work is integrated. This does not approve Avalonia or the prototype code for production use.
