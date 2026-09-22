import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';

// Disposable abrupt-interruption recovery matrix for the authenticated snapshot vault.
// Mirrors scripts/persistence-smoke.mjs: each named persistence phase is interrupted
// with SIGKILL and the vault must recover to a complete old-or-new state, never mixed.

const project = 'prototypes/AuthenticatedSnapshots.Trial/AuthenticatedSnapshots.Trial.csproj';
const assembly = 'prototypes/AuthenticatedSnapshots.Trial/bin/Release/net10.0/AuthenticatedSnapshots.Trial.dll';
const root = 'artifacts/snapshots-fault-local';
const password = 'synthetic fault-injection password';
const newPassword = 'synthetic new password';
const expectedChecksum = 'BD2A862CC097E9E1B6A8C00C40CC2BE1E1677B40865D72A8B009CF04CCBBAC66';

rmSync(root, { recursive: true, force: true });
mkdirSync(root, { recursive: true });

build();

{
  const vault = join(root, 'save-save-before-rename');
  initVault(vault);
  interrupt('save-before-rename', 'save-before-rename', ['save', vault]);
  verify(vault, password);
  assertNoPartialIsActive(vault);
}

{
  const vault = join(root, 'save-save-before-pointer');
  initVault(vault);
  interrupt('save-before-pointer', 'save-before-pointer', ['save', vault]);
  verify(vault, password);
  assertNoPartialIsActive(vault);
  run(['save', vault]); // A subsequent save proves the orphan generation does not strand the vault.
  verify(vault, password);
  assertNoPartialIsActive(vault);
}

{
  const vault = join(root, 'backup-vault');
  const backupPath = join(root, 'backup.dfsnap');
  initVault(vault);
  interrupt('backup', 'backup-before-rename', ['backup', vault, backupPath]);
  verify(vault, password);
  assertNoPartialIsActive(vault);
  if (existsSync(backupPath)) {
    throw new Error('interrupted backup unexpectedly produced a backup file');
  }
}

{
  const vault = join(root, 'password-change-vault');
  initVault(vault);
  interrupt('password-change', 'password-change-before-pointer', ['password-change', vault]);
  verify(vault, password);
  assertNoPartialIsActive(vault);
  verifyFails(vault, newPassword);
  run(['password-change', vault]); // A subsequent rotation proves the orphan generation does not strand the vault.
  verifyFails(vault, password);
  verify(vault, newPassword);
  assertNoPartialIsActive(vault);
}

{
  const sourceVault = join(root, 'restore-source');
  const backupPath = join(root, 'restore-backup.dfsnap');
  const restoreVault = join(root, 'restore-target');
  initVault(sourceVault);
  run(['backup', sourceVault, backupPath]);
  interrupt('restore', 'restore-before-rename', ['restore', backupPath, restoreVault]);
  assertEmptyVault(restoreVault);
  run(['restore', backupPath, restoreVault]);
  verify(restoreVault, password);
  assertNoPartialIsActive(restoreVault);
}

console.log(`PASS: abrupt-interruption recovery at every save/backup/restore/password-change phase. Evidence: ${root}`);

function build() {
  const result = spawnSync('dotnet', ['build', project, '--configuration', 'Release'], { stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}

function run(faultArgs) {
  const result = spawnSync('dotnet', [assembly, '--fault', ...faultArgs], { stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}

function initVault(vaultDirectory) {
  run(['init', vaultDirectory]);
}

function interrupt(kind, crashPoint, faultArgs) {
  const marker = join(root, `${kind}.marker`);
  rmSync(marker, { force: true });
  const result = spawnSync('dotnet', [assembly, '--fault', ...faultArgs], {
    stdio: 'ignore',
    env: { ...process.env, DFSNAP_CRASH_POINT: crashPoint, DFSNAP_CRASH_MARKER: marker }
  });
  if (result.error && !result.signal) throw result.error;
  if (result.status === 0) throw new Error(`${kind} interruption unexpectedly completed`);
  if (!existsSync(marker) || readFileSync(marker, 'utf8') !== crashPoint) {
    throw new Error(`${kind} failed before reaching the requested interruption point`);
  }
}

function verify(vaultDirectory, vaultPassword) {
  const result = spawnSync('dotnet', [assembly, '--fault', 'verify', vaultDirectory, vaultPassword], {
    stdio: ['ignore', 'pipe', 'inherit']
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`verify failed with status ${result.status}`);
  assertReceipt(JSON.parse(result.stdout.toString('utf8').trim()));
}

function verifyFails(vaultDirectory, vaultPassword) {
  const result = spawnSync('dotnet', [assembly, '--fault', 'verify', vaultDirectory, vaultPassword], {
    stdio: 'ignore'
  });
  if (result.error && !result.signal) throw result.error;
  if (result.status === 0) throw new Error('expected verify to fail, but the vault opened with an uncommitted password');
}

function assertReceipt(receipt) {
  const expected = {
    Transactions: 1,
    Reconciliations: 1,
    FirstTransactionId: 'FAULT-0001',
    FirstTransactionAccount: 'Fictional Card',
    ConfigurationTheme: 'System',
    DocumentChecksum: expectedChecksum
  };
  for (const [key, value] of Object.entries(expected)) {
    if (receipt[key] !== value) {
      throw new Error(`receipt field ${key} mismatch: expected ${JSON.stringify(value)}, got ${JSON.stringify(receipt[key])}`);
    }
  }
  if (receipt.FirstTransactionAmount !== 42.5) {
    throw new Error(`receipt amount mismatch: ${receipt.FirstTransactionAmount}`);
  }
}

function assertNoPartialIsActive(vaultDirectory) {
  const entries = readdirSync(vaultDirectory);
  const activePath = join(vaultDirectory, 'active-generation');
  if (existsSync(activePath)) {
    const name = readFileSync(activePath, 'utf8').trim();
    if (!/^generation-\d{10}\.dfsnap$/.test(name)) {
      throw new Error(`active-generation pointer is not a generation file: ${name}`);
    }
    if (!existsSync(join(vaultDirectory, name))) {
      throw new Error(`active-generation pointer references a missing file: ${name}`);
    }
  }
  if (!entries.some((entry) => /^generation-\d{10}\.dfsnap$/.test(entry))) {
    throw new Error('no complete generation file present after recovery');
  }
  for (const entry of entries) {
    if (/\.(active-partial|restore-partial|journal-partial)$/.test(entry) && /^generation-/.test(entry)) {
      throw new Error(`partial file is named like a generation and could be treated as active: ${entry}`);
    }
  }
}

function assertEmptyVault(vaultDirectory) {
  const entries = readdirSync(vaultDirectory);
  const generations = entries.filter((entry) => /^generation-\d{10}\.dfsnap$/.test(entry));
  if (generations.length !== 0) {
    throw new Error(`restore target unexpectedly has generation files after interrupted restore: ${generations.join(', ')}`);
  }
  if (entries.includes('active-generation')) {
    throw new Error('restore target unexpectedly has an active-generation pointer after interrupted restore');
  }
}
