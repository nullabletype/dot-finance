import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';

const root = 'artifacts/persistence-local';
const assembly = 'artifacts/desktop/DesktopShell.dll';
const fixture = 'artifacts/desktop/Fixtures/synthetic-emma.csv';
const password = 'synthetic-ci-password-not-a-user-secret';
rmSync(root, { recursive: true, force: true });
mkdirSync(root, { recursive: true });

function run(args) {
  const result = spawnSync('dotnet', [assembly, '--persistence-cycle', ...args], {
    stdio: 'inherit',
    env: { ...process.env, DOT_FINANCE_PROTOTYPE_PASSWORD: password }
  });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}

function interrupt(kind, crashPoint) {
  const marker = `${root}/stage-a/interruption-${kind}.marker`;
  rmSync(marker, { force: true });
  const result = spawnSync('dotnet', [assembly, '--persistence-interrupt', kind, `${root}/stage-a`, fixture], {
    stdio: 'ignore',
    env: {
      ...process.env,
      DOT_FINANCE_PROTOTYPE_PASSWORD: password,
      DOT_FINANCE_CRASH_POINT: crashPoint,
      DOT_FINANCE_CRASH_MARKER: marker
    }
  });
  if (result.error && !result.signal) throw result.error;
  if (result.status === 0) throw new Error(`${kind} interruption unexpectedly completed`);
  if (!existsSync(marker) || readFileSync(marker, 'utf8') !== crashPoint) {
    throw new Error(`${kind} failed before reaching the requested interruption point`);
  }
}

function sha256(path) {
  return createHash('sha256').update(readFileSync(path)).digest('hex');
}

run(['initialize', `${root}/stage-a`, fixture]);
interrupt('save', 'save-before-commit');
runRecoveryCheck();
const backup = `${root}/stage-a/portable.dotfinance-backup`;
const originalBackupHash = sha256(backup);
interrupt('backup', 'backup-staged');
if (sha256(backup) !== originalBackupHash) throw new Error('Interrupted backup replaced the valid backup');
if (!existsSync(`${backup}.partial`)) throw new Error('Interrupted backup did not leave a recognisable partial file for inspection');
runRecoveryCheck();
run(['continue', `${root}/stage-b`, fixture, `${root}/stage-a/portable.dotfinance-backup`]);
run(['verify', `${root}/stage-c`, fixture, `${root}/stage-b/portable.dotfinance-backup`]);
console.log(`PASS: encrypted save, abrupt recovery and two backup moves. Evidence: ${root}`);

function runRecoveryCheck() {
  const result = spawnSync('dotnet', [assembly, '--persistence-check-recovery', `${root}/stage-a`, fixture], {
    stdio: 'inherit',
    env: { ...process.env, DOT_FINANCE_PROTOTYPE_PASSWORD: password }
  });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
