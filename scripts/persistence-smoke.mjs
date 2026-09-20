import { spawnSync } from 'node:child_process';
import { mkdirSync, rmSync } from 'node:fs';

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

run(['initialize', `${root}/stage-a`, fixture]);
run(['continue', `${root}/stage-b`, fixture, `${root}/stage-a/portable.dotfinance-backup`]);
run(['verify', `${root}/stage-c`, fixture, `${root}/stage-b/portable.dotfinance-backup`]);
console.log(`PASS: encrypted save, restart and two backup moves. Evidence: ${root}`);
