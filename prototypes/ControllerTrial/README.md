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

`build` invokes the existing full validation entry point, including architecture generation, locked restore, .NET build/tests/format and publish. Its output is a hashed receipt tied to a clean commit. A successful build waits for external independent review; the harness deliberately has no real acceptance action. `init <directory> synthetic` permits fixed `check pass|fail|interrupt|hang` and `trial-approve` commands. Synthetic approval is explicitly labelled and cannot be used on a real run.

`unavailable <directory> <reason>` records a durable outstanding platform requirement; local success cannot clear it. This prototype has no platform-evidence ingestion action, so it remains blocked for an external decision. `ambiguity` stops for an owner decision. `correct` records each correction durably, discards previous validation/review progress, and stops at the third correction. Correction debt survives source/evidence changes and infrastructure blocks. Ninety active minutes also stop the run. Stops are terminal: renewal requires explicit owner approval and a separately recorded run, never editing a checkpoint to reset limits.

## State and trust boundary

The atomic JSON checkpoint holds issue, branch, commit, current gate, next action, correction count, elapsed time, evidence paths/hashes and events. Command outputs are receipts, not agent summaries. An arbitrary `acceptance.json` is never read. Changing the controller/policy fingerprint stops a run for separate review. Evidence changes or source changes invalidate previous validation. Failed validation cannot be retried without a recorded correction.

One exclusive `writer.lock` prevents concurrent local writers. After an interrupted process, verify that the PID in the lock is dead before removing the lock. Resume discards interrupted gate progress and conservatively charges time since the pre-command checkpoint. Normal idle/human-review time between commands is not charged. Unknown downtime after a crash may therefore stop early rather than grant extra budget. Local filesystem rename and flush provide process-interruption protection, not a demonstrated power-loss guarantee.

A separate supervisor monitors controller liveness and the remaining deadline, terminating the validation process group on macOS/Linux, or using `taskkill /t /f` on Windows. The interruption experiment kills the controller **while a fixture is running**, then verifies the supervisor killed it. A deadline experiment starts a child and grandchild and checks both are gone. Windows and Linux supervision remain pending actual platform execution. Killing the supervisor itself, escaped process groups, or hostile child processes are outside this trusted-process experiment; unattended use remains no-go.

This experiment assumes the operator and scratch directory are trusted. A malicious operator able to rewrite both controller and state is outside its boundary; there is no sandbox or signed independent evidence service. Use `begin-work`/`end-work` around active investigation and correction: the durable interval is included in the same budget on every command, survives restarts, and must end before validation/retry. Operators must start the interval truthfully; the harness cannot observe work in another editor. The overall implementation checkpoint also records this prototype's development budget. Synthetic clock/commit/policy fault injection is labelled in the transcript; it is not evidence that another platform ran or that 90 minutes actually elapsed. Review rejection in the automated trial is simulated; actual independent review is recorded separately on #4.

## Tracker and permissions

Issue [#10](https://github.com/nullabletype/dot-finance/issues/10) is the explicitly designated synthetic tracker sandbox. Only a marked comment and Project 2 Todo/In Progress status may be changed by a tracker trial. Issue #4 remains the work record. A tracker write adapter is pending a verified least-privilege credential: repository Issues read/write for this repository, Project 2 read/write, and metadata read. No contents write, Actions write, merge, or release permission is needed. Prefer a narrowly scoped GitHub App installation; classic `repo` tokens are not least privilege. Never put credentials in state, transcripts, command arguments or logs.

Tracker retries must reconcile the marked comment and current board status before repeating writes; ambiguous outcomes must remain pending. This first local harness does not claim that distributed write recovery is validated.

## Decision

Recommendation and tested-commit evidence are recorded on #4 after execution and independent review. Until the tracker permission/recovery trial, actual independent-review rejection routing and dependency support audit are complete, the result is **no-go for unattended execution**. This prototype cannot advance the production queue or promote issues.
