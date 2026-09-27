import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';

const build = JSON.parse(readFileSync('artifacts/production-build.json', 'utf8'));
const source = resolve('artifacts', 'production', build.rid, build.executable);
mkdirSync('artifacts', { recursive: true });
const scratch = resolve(mkdtempSync('artifacts/production-vault-'));
const isolated = join(scratch, 'isolated');
const workspace = join(scratch, 'workspace');
const extraction = join(scratch, 'extracted');
mkdirSync(isolated);
mkdirSync(workspace);
const executable = join(isolated, build.executable);
copyFileSync(source, executable);
const environment = {
  ...process.env,
  DOT_FINANCE_VAULT_PASSWORD: 'synthetic-native-smoke-password',
  DOTNET_BUNDLE_EXTRACT_BASE_DIR: extraction,
  DOTNET_EnableDiagnostics: '0'
};

function run(args, interrupted = false) {
  const result = spawnSync(executable, args, { cwd: isolated, encoding: 'utf8', timeout: 30000, env: environment });
  if (interrupted) {
    if (!result.error && result.status === 0) throw new Error(`${args[1]} interruption returned success`);
    return;
  }
  if (result.error || result.status !== 0) throw new Error(`${args[0]} failed: ${result.error ?? result.stderr ?? result.status}`);
}

run(['--vault-cycle', 'initialize', workspace]);
run(['--vault-interrupt', 'save', workspace], true);
run(['--vault-recovery', workspace]);
run(['--vault-interrupt', 'backup', workspace], true);
run(['--vault-recovery', workspace]);

if (readdirSync(isolated).join() !== build.executable) throw new Error('Vault smoke created a distributed sidecar');
const extracted = readdirSync(extraction, { recursive: true }).filter(path => /sqlite3mc/i.test(path));
if (extracted.length !== 1) throw new Error(`Expected one extracted SQLite3MC library; found ${extracted.join(', ')}`);
const receipt = JSON.parse(readFileSync(join(workspace, 'vault-receipt.json'), 'utf8'));
if (receipt.schemaVersion !== 1 || receipt.cipher !== 'chacha20' || receipt.kdfIterations !== 600000)
  throw new Error('Vault receipt does not match the production security policy');
writeFileSync(join(scratch, 'smoke.json'), JSON.stringify({ commit: build.commit, rid: build.rid, receipt, extracted }, null, 2));
console.log(`PASS: production encrypted-vault save, interruption and recovery (${build.rid}); evidence: ${scratch}`);
