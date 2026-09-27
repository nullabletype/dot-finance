// Explicitly authorised live trial against only nullabletype/dot-finance#10 and Project 2.
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { AmbiguousTrackerWrite, createTracker } from './tracker.mjs';

if (process.env.DOT_FINANCE_TRACKER_TRIAL !== 'issue-10') {
  throw new Error('Set DOT_FINANCE_TRACKER_TRIAL=issue-10 to acknowledge the authorised live sandbox target');
}
const tracker = createTracker();
const transcript = { startedAt: new Date().toISOString(), authorisation: 'Explicit owner approval recorded on issue #4',
  credentialBoundary: 'Existing manual operator gh session; observed permissions recorded separately; not least privilege or unattended authority',
  events: [] };
const initial = tracker.status();
assert.equal(initial.markerComments, 0, 'Live trial requires a clean sandbox without the marked comment');
assert.equal(initial.projectStatus, 'Todo', 'Live trial requires sandbox #10 to begin in Todo');
transcript.events.push({ type: 'initial', state: initial });
try {
  tracker.advance('lost-comment-response');
  assert.fail('Comment response-loss simulation must remain pending');
} catch (error) {
  if (!(error instanceof AmbiguousTrackerWrite)) throw error;
  transcript.events.push({ type: 'ambiguous-write', receipt: error.receipt });
}
const afterComment = tracker.status();
assert.equal(afterComment.markerComments, 1);
assert.equal(afterComment.projectStatus, 'Todo');
transcript.events.push({ type: 'reconciled-comment', state: afterComment });
try {
  tracker.advance('lost-project-response');
  assert.fail('Project response-loss simulation must remain pending');
} catch (error) {
  if (!(error instanceof AmbiguousTrackerWrite)) throw error;
  transcript.events.push({ type: 'ambiguous-write', receipt: error.receipt });
}
const afterProject = tracker.status();
assert.equal(afterProject.markerComments, 1);
assert.equal(afterProject.projectStatus, 'In Progress');
transcript.events.push({ type: 'reconciled-project', state: afterProject });
const replay = tracker.advance();
assert.deepEqual(replay.mutations, [], 'A fully reconciled retry must not repeat either write');
transcript.events.push({ type: 'idempotent-replay', receipt: replay });
const reset = tracker.reset();
assert.equal(reset.after.projectStatus, 'Todo');
assert.equal(reset.after.markerComments, 1);
transcript.events.push({ type: 'bounded-cleanup', receipt: reset });
transcript.completedAt = new Date().toISOString();
const directory = resolve('artifacts/controller/tracker');
mkdirSync(directory, { recursive: true });
const path = resolve(directory, `live-${transcript.completedAt.replaceAll(':', '-')}.json`);
writeFileSync(path, `${JSON.stringify(transcript, null, 2)}\n`, { mode: 0o600, flush: true });
console.log(`Live tracker reconciliation trial passed: ${path}`);
