# Disposable controller trial — issue #4

**Question:** can a manually invoked development controller preserve bounded run state and route failures truthfully after interruption? This is a logic/state prototype, separate from the offline desktop application. No scheduling, merge, release, or production acceptance authority exists.

## Run

Commit changes first, then run `npm run controller:trial`. It executes actual child processes for passing and failing synthetic checks, kills one controller process, resumes its checkpoint, and prints the transcript directory under `artifacts/controller/`. Each invocation creates new **synthetic experiment** runs; it does not renew a real implementation budget.

Manual commands:

```sh
npm run controller -- init .agent-runs/manual-controller
npm run controller -- build .agent-runs/manual-controller
npm run controller -- status .agent-runs/manual-controller
npm run controller -- reject .agent-runs/manual-controller 'independent review URL and finding'
npm run controller -- correct .agent-runs/manual-controller
npm run controller -- begin-work .agent-runs/manual-controller
# Investigate/correct; stop this interval before waiting for human review.
npm run controller -- end-work .agent-runs/manual-controller
```

`build` invokes the existing full validation entry point, including architecture generation, locked restore, .NET build/tests/format and publish. Its output is a hashed receipt tied to a clean commit. Child-launch infrastructure errors use reserved exit 75 and block rather than consuming a code correction. A successful build waits for external independent review; the harness deliberately has no real acceptance action. `init <directory> synthetic` permits fixed `check pass|fail|fail-dirty|interrupt|hang|infrastructure` and `trial-approve` commands. Synthetic approval is explicitly labelled and cannot be used on a real run. The `fail-dirty` fixture deliberately creates an exclusive `.controller-trial-dirty-<controller-pid>` file; the automated trial removes it after checking source-change/failure interaction. If manually invoked or interrupted, remove that synthetic marker before retrying.

`unavailable <directory> <reason>` records a durable outstanding platform requirement; local success cannot clear it. This prototype has no platform-evidence ingestion action, so it remains blocked for an external decision. `ambiguity` stops for an owner decision. `correct` records each correction durably, discards previous validation/review progress, and stops at the third correction. Correction debt survives source/evidence changes and infrastructure blocks. Ninety active minutes also stop the run. Stops are terminal: renewal requires explicit owner approval and a separately recorded run, never editing a checkpoint to reset limits.

## State and trust boundary

The atomic JSON checkpoint holds issue, branch, commit, current gate, next action, correction count, elapsed time, evidence paths/hashes and events. Command outputs are receipts, not agent summaries. An arbitrary `acceptance.json` is never read. Changing the controller/policy fingerprint stops a run for separate review. Evidence changes or source changes invalidate previous validation. Failed validation cannot be retried without a recorded correction.

One exclusive `writer.lock` prevents concurrent local writers. After an interrupted process, verify that the PID in the lock is dead before removing the lock. Resume discards interrupted gate progress and conservatively charges time since the pre-command checkpoint. Normal idle/human-review time between commands is not charged. Unknown downtime after a crash may therefore stop early rather than grant extra budget. Local filesystem rename and flush provide process-interruption protection, not a demonstrated power-loss guarantee.

A separate supervisor monitors controller liveness and the remaining deadline, terminating the validation process group on macOS/Linux, or using `taskkill /t /f` on Windows. The interruption experiment kills the controller **while a fixture is running**, then verifies the supervisor killed it. A deadline experiment starts a child and grandchild and checks both are gone. The configured hosted trial exercises this recovery on macOS, Windows and Linux for the tested commit. Killing the supervisor itself, escaped process groups, or hostile child processes are outside this trusted-process experiment; unattended use remains no-go.

The pull-request workflow runs the adversarial trial on macOS, Windows and Ubuntu without retaining its scratch state or transcript as a GitHub Actions artifact. Platform evidence applies only to the exact commit after those hosted jobs pass; the workflow definition alone is not evidence.

This experiment assumes the operator and scratch directory are trusted. A malicious operator able to rewrite both controller and state is outside its boundary; there is no sandbox or signed independent evidence service. Use `begin-work`/`end-work` around active investigation and correction: the durable interval is included in the same budget on every command, survives restarts, and must end before validation/retry. Operators must start the interval truthfully; the harness cannot observe work in another editor. The overall implementation checkpoint also records this prototype's development budget. Synthetic clock/commit/policy fault injection is labelled in the transcript; it is not evidence that another platform ran or that 90 minutes actually elapsed. Review rejection in the automated trial is simulated; actual independent review is recorded separately on #4.

## Tracker and permissions

Issue [#10](https://github.com/nullabletype/dot-finance/issues/10) is the explicitly designated synthetic tracker sandbox. Only its single marked comment and Project 2 Todo/In Progress status may be changed by a tracker trial. Issue #4 remains the work record. The manual adapter uses the operator's existing authenticated `gh` session only after explicit owner authorisation. It is structurally fixed to repository `nullabletype/dot-finance`, issue #10, user-owned Project 2 under `nullabletype` and those two statuses; it accepts no alternate repository, owner, issue, project, comment or status arguments and verifies the resolved Project identity before writing.

The current operator session has broader permissions than this trial needs, so it is not a least-privilege security boundary and must not be represented as one. The adapter never reads, accepts, stores or logs a credential. A future unattended controller requires a genuinely isolated environment in which broader stored `gh` authentication is unavailable and only a narrowly scoped credential is present.

`npm run controller:trial` includes an offline fake-GitHub regression for both ambiguous writes. With the owner authorisation recorded on #4 and a clean #10 in Todo, run the live exercise explicitly:

```sh
DOT_FINANCE_TRACKER_TRIAL=issue-10 npm run controller:tracker:live
```

The live trial runs the comment and Project response-loss phases in separate child processes. Each phase first reconciles remote state, so rerunning the entry point after termination following either write resumes from the bounded intermediate state rather than duplicating the write. It then proves a fully reconciled retry performs no writes, returns #10 to Todo and leaves the one marked evidence comment. Its local transcript contains remote state and mutation names, never credentials. Any unexpected issue state, duplicate marker, Project identity or Project status fails closed for owner review.

## Decision

The owner accepted the recommendation from #4 at the issue #5 decision gate: retain this bounded, manually invoked development aid only. Passing the manual tracker recovery trial does not approve unattended execution, automatic merge, or release. This prototype cannot advance the production queue or promote issues. Any future unattended controller requires its own explicit security and policy decision after validation in an isolated environment containing only a narrowly scoped credential.
