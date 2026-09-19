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

- Implement one ready issue at a time on an isolated branch; preserve unrelated changes.
- Keep prototypes disposable and separate from production code.
- Respect persisted correction and time limits. Escalate ambiguity with evidence.
- Never remove meaningful assertions or weaken gates to obtain a pass.
- Update LikeC4 when architecture changes; otherwise record why no update is needed.
- Missing platform checks remain pending. Attach evidence for the actual tested commit.
- Obtain independent review before acceptance. Do not claim review happened unless it did.
- No automatic merging, release publishing, or unattended execution until its policy and controller have been explicitly approved and validated.
