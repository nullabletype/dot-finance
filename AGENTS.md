# dot-finance agent guidance

Read `spec-file.md` and `development-workflow.md` before work. Keep the shipped app entirely offline and use synthetic financial data in all development activity.

## Agent skills

### Issue tracker

GitHub Issues and Project 2 under nullabletype are authoritative. External PRs are not a request surface. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the configured readiness and review labels in `docs/agents/triage-labels.md`. Dependencies and human gates must be satisfied before promotion.

### Domain docs

Single domain context, described in `docs/agents/domain.md`, with the spec as the behavioural authority and future decisions under `docs/adr/`.

## Execution rules

- Use current, supported releases for all dependencies and tooling, including transitive packages, SDKs, runtimes, GitHub Actions, runner images, and container/base images. Verify upstream support status and applicable security updates when selecting or updating versions and before acceptance; do not rely on a major-version tag, a successful build, or a forced runtime override as evidence of support. Keep reproducible pins and lockfiles current. Unsupported or deprecated dependencies block acceptance; see `development-workflow.md`.
- Use the SDK-generated root `.editorconfig` and `Directory.Build.props` for .NET coding standards. Keep the built-in .NET 10 default analyser set; do not introduce alternative style packages or project-level overrides without a stated reason.
- Run `dotnet format <project-or-solution> --verify-no-changes --no-restore --severity info` after restore as the formatting/style gate. Build analysis does not enforce every suggestion-level style preference.
- Reserve GitHub Actions artifact storage for explicitly published, release-ready application builds. An artifact may contain only Release-configuration compiled application binaries and optional PDBs needed for debugging. Never upload test results or evidence, logs, screenshots, traces, architecture sites, source, caches, packages, databases or backups, container images or layers, or other intermediate output.

- Implement one ready issue at a time on an isolated branch; preserve unrelated changes.
- Keep prototypes disposable and separate from production code.
- Respect persisted correction and time limits. Escalate ambiguity with evidence.
- Never remove meaningful assertions or weaken gates to obtain a pass.
- Update LikeC4 when architecture changes; otherwise record why no update is needed.
- Missing platform checks remain pending. Attach evidence for the actual tested commit.
- Obtain independent review before acceptance. Do not claim review happened unless it did.
- No automatic merging, release publishing, or unattended execution until its policy and controller have been explicitly approved and validated.
