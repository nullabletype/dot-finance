// Explicitly authorised, restart-aware live trial against only issue #10 and Project 2.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { AmbiguousTrackerWrite, createTracker } from './tracker.mjs';

if (process.env.DOT_FINANCE_TRACKER_TRIAL !== 'issue-10') {
  throw new Error('Set DOT_FINANCE_TRACKER_TRIAL=issue-10 to acknowledge the authorised live sandbox target');
}
const source = fileURLToPath(import.meta.url);

function runPhase(phase) {
  const execution = spawnSync(process.execPath, [source, `--phase=${phase}`], {
    cwd: process.cwd(), encoding: 'utf8', env: process.env, maxBuffer: 16 * 1024 * 1024,
  });
  if (![0, 75].includes(execution.status)) {
    throw new Error(`Tracker ${phase} phase failed: ${execution.stderr.trim() || `exit ${execution.status}`}`);
  }
  return { phase, exit: execution.status, receipt: JSON.parse(execution.stdout) };
}

function executePhase(phase) {
  const tracker = createTracker();
  const state = tracker.status();
  if (phase === 'comment') {
    if (state.markerCurrent) return { outcome: 'already-reconciled', state };
    return tracker.advance('lost-comment-response');
  }
  if (phase === 'project') {
    if (!state.markerCurrent) throw new Error('Project phase requires the exact marked comment');
    if (state.projectStatus === 'In Progress') return { outcome: 'already-reconciled', state };
    return tracker.advance('lost-project-response');
  }
  throw new Error('Unknown live tracker phase');
}

const phase = process.argv[2]?.match(/^--phase=(comment|project)$/)?.[1];
if (phase) {
  try { console.log(JSON.stringify(executePhase(phase))); }
  catch (error) {
    if (!(error instanceof AmbiguousTrackerWrite)) throw error;
    console.log(JSON.stringify(error.receipt));
    process.exitCode = 75;
  }
} else {
  const tracker = createTracker();
  const transcript = { startedAt: new Date().toISOString(), authorisation: 'Explicit owner approval recorded on issue #4',
    credentialBoundary: 'Existing manual operator gh session; observed permissions recorded separately; not least privilege or unattended authority',
    events: [] };
  const initial = tracker.status();
  assert.ok(initial.markerComments <= 1, 'Live trial permits at most the single marked comment');
  transcript.events.push({ type: 'initial', state: initial });

  const commentPhase = runPhase('comment');
  transcript.events.push({ type: 'separate-process-comment-phase', ...commentPhase });
  const afterComment = tracker.status();
  assert.equal(afterComment.markerComments, 1);
  assert.equal(afterComment.markerCurrent, true);
  transcript.events.push({ type: 'reconciled-comment', state: afterComment });

  const projectPhase = runPhase('project');
  transcript.events.push({ type: 'separate-process-project-phase', ...projectPhase });
  const afterProject = tracker.status();
  assert.equal(afterProject.markerComments, 1);
  assert.equal(afterProject.markerCurrent, true);
  assert.equal(afterProject.projectStatus, 'In Progress');
  transcript.events.push({ type: 'reconciled-project', state: afterProject });

  const replay = tracker.advance();
  assert.deepEqual(replay.mutations, [], 'A fully reconciled retry must not repeat either write');
  transcript.events.push({ type: 'idempotent-replay', receipt: replay });
  const reset = tracker.reset();
  assert.equal(reset.after.projectStatus, 'Todo');
  assert.equal(reset.after.markerComments, 1);
  assert.equal(reset.after.markerCurrent, true);
  transcript.events.push({ type: 'bounded-cleanup', receipt: reset });
  transcript.completedAt = new Date().toISOString();
  const directory = resolve('artifacts/controller/tracker');
  mkdirSync(directory, { recursive: true });
  const path = resolve(directory, `live-${transcript.completedAt.replaceAll(':', '-')}.json`);
  writeFileSync(path, `${JSON.stringify(transcript, null, 2)}\n`, { mode: 0o600, flush: true });
  console.log(`Live tracker reconciliation trial passed: ${path}`);
}
