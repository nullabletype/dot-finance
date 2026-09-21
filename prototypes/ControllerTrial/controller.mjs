// DISPOSABLE development harness. Never imported by the shipped application.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, renameSync, mkdirSync, existsSync, openSync, closeSync, unlinkSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const source = fileURLToPath(import.meta.url);
const root = resolve(dirname(source), '../..');
const policy = { corrections: 3, milliseconds: 90 * 60 * 1000, gates: ['validation', 'review'] };
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const policyHash = () => hash(Buffer.concat([readFileSync(source), readFileSync(resolve(dirname(source), 'supervisor.mjs'))]));
function git(...args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  if (result.status !== 0) throw new Error('Cannot identify source checkout');
  return result.stdout.trim();
}
function identity() {
  return { commit: git('rev-parse', 'HEAD'), branch: git('branch', '--show-current'), dirty: git('status', '--porcelain') !== '' };
}
function save(path, state) {
  const temporary = `${path}.tmp`;
  writeFileSync(temporary, `${JSON.stringify(state, null, 2)}\n`, { mode: 0o600, flush: true });
  renameSync(temporary, path);
}
function event(state, type, detail) {
  state.events.push({ sequence: state.events.length + 1, at: new Date().toISOString(), type, detail });
}
function invalidate(state, reason) {
  state.evidence = {};
  state.gate = 'validation';
  state.status = state.correctionDue ? 'failed' : state.blockers.length ? 'blocked' : 'pending';
  state.nextAction = reason;
  event(state, 'invalidated', reason);
}

export function run(directory, action, argument) {
  directory = resolve(directory);
  mkdirSync(directory, { recursive: true });
  const path = resolve(directory, 'state.json');
  // Single writer. An interrupted owner must explicitly remove the stale lock after
  // checking that its PID is dead; never guess that another controller has stopped.
  const lock = resolve(directory, 'writer.lock');
  const fd = openSync(lock, 'wx', 0o600);
  writeFileSync(fd, `${process.pid}\n`);
  let state;
  let started;
  try {
    const current = identity();
    if (action === 'init') {
      if (existsSync(path)) throw new Error('Run already exists; resume it instead');
      state = { issue: 4, ...current, policyHash: policyHash(), attempts: 0, elapsedMs: 0,
        activeSince: null, gate: 'validation', status: 'pending', evidence: {}, events: [],
        nextAction: 'Run check or build', simulation: argument === 'synthetic', correctionDue: false, blockers: [], workSince: null };
      event(state, 'started', 'Manual disposable trial; no acceptance or merge authority');
      save(path, state);
      return state;
    }
    state = JSON.parse(readFileSync(path, 'utf8'));
    if (state.status === 'stopped') return state;
    // Fingerprint precedes schema-dependent recovery: an older interrupted
    // checkpoint must stop for review, not be interpreted as the new schema.
    if (state.policyHash !== policyHash()) {
      state.status = 'stopped';
      state.nextAction = 'Policy/controller changed: separate review and explicit new run required';
      save(path, state);
      return state;
    }
    if (state.activeSince !== null) {
      const now = Date.now();
      const intervalStart = state.workSince === null ? state.activeSince : Math.min(state.activeSince, state.workSince);
      state.elapsedMs += Math.max(0, now - intervalStart);
      if (state.workSince !== null) state.workSince = now;
      state.activeSince = null;
      invalidate(state, 'Interrupted command: rerun gate; unobserved time charged conservatively');
    }
    if (state.commit !== current.commit || state.branch !== current.branch || current.dirty) {
      invalidate(state, 'Source changed or dirty: rerun against a clean identified commit');
      state.commit = current.commit;
      state.branch = current.branch;
      state.dirty = current.dirty;
    }
    for (const evidence of Object.values(state.evidence)) {
      if (!existsSync(evidence.path) || hash(readFileSync(evidence.path)) !== evidence.sha256) {
        invalidate(state, 'Evidence missing or changed: rerun validation');
        break;
      }
    }
    const externalActiveMs = state.workSince === null ? 0 : Math.max(0, Date.now() - state.workSince);
    if (state.elapsedMs + externalActiveMs >= policy.milliseconds || state.attempts >= policy.corrections) {
      state.elapsedMs += externalActiveMs;
      state.workSince = null;
      state.status = 'stopped';
      state.nextAction = 'Budget exhausted: owner decision and explicit renewal required';
    }
    if (action === 'status' || state.status === 'stopped') {
      save(path, state);
      return state;
    }
    started = Date.now();
    state.activeSince = started;
    save(path, state); // Durable before any potentially interrupted operation.
    if (state.workSince !== null && action !== 'end-work') throw new Error('End the active work interval before another action');
    if (action === 'begin-work') {
      state.workSince = started;
      event(state, 'work-started', 'Active investigation/correction interval, including across restarts');
    } else if (action === 'end-work') {
      if (state.workSince === null) throw new Error('No active work interval');
      state.elapsedMs += Math.max(0, started - state.workSince);
      state.workSince = null;
      event(state, 'work-ended', 'External active time charged to shared budget');
    } else if (action === 'ambiguity') {
      state.status = 'stopped';
      state.nextAction = `Owner decision required: ${argument || 'unresolved product ambiguity'}`;
      event(state, 'ambiguity', state.nextAction);
    } else if (action === 'unavailable') {
      if (!argument) throw new Error('Name the unavailable platform');
      if (!state.blockers.includes(argument)) state.blockers.push(argument);
      state.status = 'blocked';
      state.nextAction = `Platform evidence pending: ${argument || 'unavailable runner'}`;
      event(state, 'runner-unavailable', state.nextAction);
    } else if (action === 'correct') {
      if (!state.correctionDue) throw new Error('Correction requires a failed gate');
      state.attempts++;
      state.correctionDue = false;
      invalidate(state, 'Correction recorded; rerun validation and review');
      if (state.attempts >= policy.corrections) {
        state.status = 'stopped';
        state.nextAction = 'Three corrections reached: stop for owner decision';
      }
    } else if (action === 'check' || action === 'build') {
      if (state.correctionDue) throw new Error('Record correction before retrying a failed gate');
      if (current.dirty) throw new Error('Commit changes before collecting evidence');
      if (action === 'check' && !state.simulation) throw new Error('Synthetic check only allowed in a synthetic run');
      if (!['pass', 'fail', 'fail-dirty', 'interrupt', 'hang', 'infrastructure', undefined].includes(argument)) throw new Error('Unknown fixture mode');
      // Erase old approval BEFORE launching a process, including a retry that crashes.
      invalidate(state, 'Validation executing');
      save(path, state);
      const receiptPath = resolve(directory, `process-${state.events.length}.json`);
      const args = ['prototypes/ControllerTrial/supervisor.mjs', action, argument || 'pass', String(process.pid),
        String(Math.max(1, policy.milliseconds - state.elapsedMs)), receiptPath];
      const execution = spawnSync(process.execPath, args, {
        cwd: root, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024,
      });
      const result = existsSync(receiptPath) ? JSON.parse(readFileSync(receiptPath, 'utf8')) :
        { status: null, error: { code: execution.error?.code || 'SUPERVISOR_FAILED' } };
      const infrastructureFailure = result.status === 75 || result.error?.code === 'ENOENT';
      // Failure debt is independent of whether this checkout can accept evidence.
      state.correctionDue = (result.status !== 0 || Boolean(result.error)) && !infrastructureFailure;
      const after = identity();
      const evidencePath = resolve(directory, `validation-${state.events.length}.json`);
      const receipt = { commit: current.commit, command: [process.execPath, ...args], exit: result.status,
        signal: result.signal, error: result.error?.code, stdout: result.stdout, stderr: result.stderr,
        synthetic: action === 'check' };
      save(evidencePath, receipt);
      state.evidence.validation = { path: evidencePath, sha256: hash(readFileSync(evidencePath)) };
      if (after.commit !== current.commit || after.dirty || after.branch !== current.branch) {
        invalidate(state, 'Checkout changed during validation');
      } else if (result.status === 0 && !result.error) {
        state.status = state.blockers.length ? 'blocked' : 'pending';
        state.gate = 'review';
        state.nextAction = 'Obtain independent review; synthetic success is not product acceptance';
      } else {
        state.status = infrastructureFailure ? 'blocked' : 'failed';
        state.nextAction = 'Inspect gate output; correct failure or resolve infrastructure';
      }
      event(state, 'validation', receipt);
    } else if (action === 'reject') {
      if (state.gate !== 'review' || state.status !== 'pending') throw new Error('Review requires passing validation');
      if (!argument) throw new Error('Review rejection requires a reference');
      state.status = 'failed';
      state.correctionDue = true;
      state.nextAction = 'Address independent review then record correction';
      event(state, 'review-rejected', argument);
    } else if (action === 'trial-approve') {
      if (!state.simulation || state.gate !== 'review' || state.status !== 'pending' || state.correctionDue || state.blockers.length) throw new Error('Approval is synthetic-trial only after validation without outstanding blockers');
      state.status = 'trial-passed';
      state.nextAction = 'Synthetic routing only; independent review and acceptance remain external gates';
      event(state, 'synthetic-review-approved', state.nextAction);
    } else {
      throw new Error(`Unknown action: ${action}`);
    }
    return state;
  } finally {
    if (state && started) {
      // workSince already covers this command when an external interval is open.
      if (state.workSince === null) state.elapsedMs += Date.now() - started;
      state.activeSince = null;
      if (state.elapsedMs >= policy.milliseconds) {
        state.status = 'stopped';
        state.nextAction = 'Active time exhausted: owner decision required';
      }
      save(path, state);
    }
    closeSync(fd);
    unlinkSync(lock);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === source) {
  const [action = 'status', directory = '.agent-runs/controller-trial', argument] = process.argv.slice(2);
  try {
    const state = run(directory, action, argument);
    console.log(JSON.stringify(state, null, 2));
    if (['failed', 'blocked', 'stopped'].includes(state.status)) process.exitCode = 1;
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
