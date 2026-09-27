# Agentic development workflow

## Purpose and status

Build dot-finance through bounded implementation, automated validation, independent review, and correction. The owner accepted this manually invoked operating model at the issue #5 decision gate on 2026-09-27. It does not provision unattended automation or grant automatic merge or release permissions.

GitHub Issues and a GitHub Project will track work. Use the repository [nullabletype/dot-finance](https://github.com/nullabletype/dot-finance) and board [Project dot-finance](https://github.com/users/nullabletype/projects/2) (owner `nullabletype`, project number `2`). LikeC4 is the selected architectural diagramming tool and must participate in the development loop and build.

The development toolchain may use GitHub, package registries, and agent services. The shipped application remains entirely offline. Only source code and synthetic financial data belong in development tickets, agent inputs, screenshots, and logs. Follow the release-only GitHub Actions artifact policy in `AGENTS.md`; validation output remains in the job workspace or workflow result.

## Work tracking

Use GitHub Issues as the authoritative work queue and the Project as its status view. Avoid a competing backlog in local files.

Use the existing board statuses Todo, In Progress, and Done. Readiness, blockers, and review are represented by labels described in `docs/agents/triage-labels.md`.

Each issue contains its purpose, specification references, acceptance scenarios, scope exclusions, dependencies, required validation, and architectural impact. Before entering Ready, its expected outcomes must be unambiguous.

Each pull request links its issue and includes the implementation summary, evidence for the tested commit, and architectural impact: either updated LikeC4 sources or an explicit explanation of why no diagram change is needed. Project status should follow actual issue/PR/check events. An agent's claim that work is complete is insufficient.

Close an issue after integration and required validation, not merely after opening a PR. Invalidate stale evidence when the code changes. Configure any automatic merge policy explicitly before enabling it.

## Prototype phase

Before detailed implementation planning, run the prototype tickets on the GitHub Project. Prototypes answer bounded questions and are disposable by default. Keep experiments under `prototypes/`; retaining behaviour or technology choices later requires production validation and an explicit decision. The issue #5 gate accepted Avalonia/.NET, SQLite3MC, and the guided reconciliation semantics as production-planning directions; it did not promote prototype code into production.

Sequence: build and LikeC4 foundation → encrypted desktop round trip → reconciliation workflow trial → decision checkpoint. Exercise controller failure and recovery after the foundation is available, before unattended runs. Track dependencies on GitHub, not in a duplicate local backlog.

For each experiment, record the question, hypothesis, tested commit, commands and outputs, observed result, limitations, and retain/rework/discard recommendation. Use synthetic data only. A successful experiment can disprove a hypothesis; it must not claim the capability was validated when it failed.

Initial execution limits: at most three correction attempts and 90 minutes of active agent work per prototype run, whichever comes first. Stop at the limit with evidence and remaining work; resuming requires an explicit budget renewal. These are investigation limits, not delivery estimates. Stop sooner on a product ambiguity or a required policy change. Human review and hosted runner queue time do not consume active agent time. Persist attempt count and elapsed budget across restarts.

The technical gate requires evidence on macOS, Windows, and Linux for launch, encrypted persistence, and cross-platform backup restoration. The UI gate requires the owner's review of the synthetic guided reconciliation. The controller gate requires demonstrated failure handling, resumption, and truthful reporting. Record architecture decisions after those gates; only then prepare production implementation tickets.

## Feature execution

1. Select one Ready issue whose dependencies are complete.
2. Inspect the checkout and baseline checks; work on an isolated branch.
3. Implement the smallest complete behaviour, with appropriate tests and documentation.
4. Update the LikeC4 model and affected views if boundaries, responsibilities, dependencies, persistence, or significant flows change.
5. Run applicable deterministic checks and generate architecture documentation.
6. Obtain independent review of the implementation, test quality, and agreement between code and diagrams.
7. Correct failures and rerun affected checks plus required regression gates.
8. Integrate under the approved merge policy, attach evidence, and advance the issue.

Proposed correction budget: three attempts per feature plus a configured time/cost ceiling. Distinguish code failures from infrastructure failures. Escalate unresolved product decisions and repeated failures with a concise diagnosis and evidence. Never weaken acceptance criteria, disable checks, or delete meaningful assertions merely to obtain a passing result.

Persist enough state to resume an interrupted run: issue, branch and commit, attempts, completed gates, evidence locations, and next action. The GitHub issue remains the work record; local run state is a resumable execution checkpoint.

## Validation gates

- Use current, supported releases throughout the development and runtime stacks: direct and transitive packages, SDKs, runtimes, GitHub Actions (including their declared execution runtimes), hosted/self-hosted runner images, and container/base images. Select an actively supported stable release line and keep its patches and applicable security fixes current. Verify upstream lifecycle/support information and release notes when selecting or updating versions and before acceptance; record the checked versions, sources, and check date in the issue/PR evidence. Where no formal lifecycle is published, verify that the release is current and maintained upstream. A successful build, floating `latest`/major tag, or runner-forced runtime upgrade does not establish support. Deprecated or end-of-life components and unresolved applicable security updates block acceptance until upgraded or replaced. Preserve reproducibility through explicit version pins and lockfiles, updating them together and rerunning affected validation.
- Builds and the built-in .NET 10 default analyser checks pass. The SDK-generated root EditorConfig is the style authority; `Directory.Build.props` supplies shared defaults. After restore, run `dotnet format <project-or-solution> --verify-no-changes --no-restore --severity info` to check formatting and suggestion-level style. Prototype CI must enforce this gate when projects are added.
- Approved financial examples and generated invariant checks pass, including duplicates, reimbursements, minimum balances, refunds, and shortfalls.
- Settlement, locking, reopening, corrections, and restart behaviour are verified.
- Persistence, migration, interrupted writes, encryption failure cases, and backup/restore are verified.
- Packaged application workflows succeed with networking blocked, with monitoring for attempted outbound connections.
- Desktop workflow evidence covers the supported operating systems; missing platform evidence stays pending.
- LikeC4 validation, formatting checks, and documentation generation pass.
- Review confirms diagrams represent the implementation and no blocking findings remain.

Use mutation checks for critical financial and settlement rules where they establish whether tests detect meaningful faults. Do not equate coverage percentages or agent review with proof of correctness.

## LikeC4 architecture documentation

Keep authored LikeC4 source under `docs/architecture/`, versioned alongside the implementation. Start with a small set of views: system context and offline boundary; major application components and storage; reconciliation and settlement flow; encrypted backup/restore flow. Add detail only when it aids understanding. Distinguish proposed architecture from implemented architecture explicitly.

Pin the LikeC4 development dependency and commit its package lockfile. LikeC4 and its Node tooling are build/documentation dependencies, not application runtime dependencies.

The full project build must validate and generate architecture documentation. Run the architecture stage on every PR and main-branch build, alongside the .NET checks. Fast local code-only builds may remain available, but cannot substitute for the full acceptance build.

From the architecture workspace, the official CLI supports:

```text
likec4 format --check
likec4 validate
likec4 build -o <artifact-directory>
```

Invoke the pinned local CLI through repository scripts; do not fetch an unpinned latest version at build time. Final paths and script names will be selected and verified during setup.

Generate the interactive static site during the build and associate its validation with the tested commit through the workflow result. Do not retain the generated site or optional PNG exports as GitHub Actions artifacts; reviewers can generate them locally from the tested source. Keep generated outputs out of the authored source tree. Public hosting is not required; publishing diagrams needs an explicit decision.

CLI validation detects model syntax errors and manual layout drift. It does not prove that the diagrams match the C# implementation. Enforce that separately through architectural-impact review and, where useful, automated dependency rules applied to the actual code and model consistency checks. An unchanged diagram is acceptable when the implementation change has no architectural impact.

## Human checkpoints

Proposed checkpoints: approve the contract and worked financial examples; review the first complete synthetic desktop workflow; accept a release candidate with cross-platform and security evidence. Routine repairs within approved scope remain autonomous. Changes to financial semantics, validation policy, destructive operations, and release publication require the applicable explicit authority.

Before unattended execution, test the loop itself with an intentional failure, interrupted run, unavailable runner, and review rejection. Verify resumption, bounded retries, and truthful status reporting.

## References

- [LikeC4 CLI](https://likec4.dev/tooling/cli/)
- [LikeC4 model validation](https://likec4.dev/guides/validate-your-model/)
- [Long-running agent harness guidance](https://www.anthropic.com/engineering/effective-harnesses-for-long-running-agents)
- [Agent evaluation guidance](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)
