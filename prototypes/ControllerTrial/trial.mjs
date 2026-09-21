// Repeatable adversarial experiment, not a production acceptance test suite.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync, unlinkSync } from 'node:fs';
import { resolve } from 'node:path';

const base = resolve('artifacts/controller', new Date().toISOString().replaceAll(':', '-'));
mkdirSync(base, { recursive: true });
const transcript = [];
const statePath = name => resolve(base, name, 'state.json');
const read = name => JSON.parse(readFileSync(statePath(name), 'utf8'));
const seed = (name, change) => {
  const state = read(name);
  change(state);
  writeFileSync(statePath(name), JSON.stringify(state));
  transcript.push({ name, simulation: 'Fault injected into scratch state, not acceptance evidence' });
};
function step(name, action, argument, expected) {
  const result = spawnSync(process.execPath, ['prototypes/ControllerTrial/controller.mjs', action, resolve(base, name), ...(argument ? [argument] : [])], { encoding: 'utf8' });
  const state = read(name);
  transcript.push({ name, action, argument, exit: result.status, signal: result.signal, status: state.status, stderr: result.stderr });
  if (expected) assert.equal(state.status, expected, `${name}: ${action}: ${result.stderr}`);
  return result;
}
try {
  step('passing', 'init', 'synthetic', 'pending');
  step('passing', 'check', 'pass', 'pending');
  step('passing', 'trial-approve', undefined, 'trial-passed');
  step('failure', 'init', 'synthetic');
  step('failure', 'check', 'fail', 'failed');
  // An invented acceptance file and prose do not satisfy a failed gate.
  writeFileSync(resolve(base, 'failure', 'acceptance.json'), '{"accepted":true}');
  assert.equal(step('failure', 'trial-approve', 'Agent says done', 'failed').status, 1);
  step('failure', 'correct', undefined, 'pending');
  step('failure', 'check', 'pass', 'pending');
  step('failure', 'reject', 'SIMULATED independent review rejection', 'failed');
  step('failure', 'correct', undefined, 'pending');
  step('failure', 'check', 'pass', 'pending');
  step('failure', 'trial-approve', undefined, 'trial-passed');
  assert.equal(read('failure').attempts, 2);

  step('interruption', 'init', 'synthetic');
  const killed = step('interruption', 'check', 'interrupt');
  assert.equal(killed.signal, 'SIGKILL');
  assert.notEqual(read('interruption').activeSince, null);
  // spawnSync has reaped this child, so its lock is now demonstrably stale.
  unlinkSync(resolve(base, 'interruption', 'writer.lock'));
  step('interruption', 'status', undefined, 'pending');
  assert.equal(read('interruption').gate, 'validation');
  step('interruption', 'check', 'pass', 'pending');

  step('runner', 'init', 'synthetic');
  step('runner', 'unavailable', 'Windows runner deliberately unavailable', 'blocked');
  assert.equal(step('runner', 'trial-approve', undefined, 'blocked').status, 1);

  step('changed-source', 'init', 'synthetic');
  step('changed-source', 'check', 'pass');
  seed('changed-source', state => { state.commit = 'older-synthetic-commit'; });
  step('changed-source', 'status', undefined, 'pending');
  assert.equal(read('changed-source').gate, 'validation');
  assert.deepEqual(read('changed-source').evidence, {});

  step('tampered-evidence', 'init', 'synthetic');
  step('tampered-evidence', 'check', 'pass');
  writeFileSync(read('tampered-evidence').evidence.validation.path, 'changed');
  step('tampered-evidence', 'status', undefined, 'pending');
  assert.equal(read('tampered-evidence').gate, 'validation');

  step('policy', 'init', 'synthetic');
  seed('policy', state => { state.policyHash = 'different-policy'; });
  step('policy', 'status', undefined, 'stopped');
  step('policy', 'check', 'pass', 'stopped');

  step('limits', 'init', 'synthetic');
  for (let attempt = 1; attempt <= 3; attempt++) {
    step('limits', 'check', 'fail', 'failed');
    step('limits', 'correct', undefined, attempt === 3 ? 'stopped' : 'pending');
  }
  step('limits', 'check', 'pass', 'stopped');
  assert.equal(read('limits').attempts, 3);
  step('time', 'init', 'synthetic');
  seed('time', state => { state.elapsedMs = 90 * 60 * 1000; });
  step('time', 'check', 'pass', 'stopped');
  step('ambiguity', 'init', 'synthetic');
  step('ambiguity', 'ambiguity', 'Financial semantics need owner input', 'stopped');
  step('ambiguity', 'check', 'pass', 'stopped');
  console.log(`Controller trial passed. Transcript and durable states: ${base}`);
} finally {
  writeFileSync(resolve(base, 'transcript.json'), JSON.stringify(transcript, null, 2));
}
