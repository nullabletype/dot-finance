import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';

const project = 'prototypes/DesktopShell/DesktopShell.csproj';
// Invalidate a previous success before running any gates.
rmSync('artifacts/build.json', { force: true });
const commit = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' });
const status = spawnSync('git', ['status', '--porcelain'], { encoding: 'utf8' });
if (commit.status !== 0 || status.status !== 0) throw new Error('Cannot identify build source');
const dirty = status.stdout.trim().length !== 0;
function run(command, args) {
  const result = spawnSync(command, args, { stdio: 'inherit', shell: false });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
const cli = 'node_modules/likec4/bin/likec4.mjs';
run(process.execPath, [cli, 'format', '--check', 'docs/architecture']);
run(process.execPath, [cli, 'validate', 'docs/architecture']);
run(process.execPath, ['scripts/check-invalid-model.mjs']);
run(process.execPath, [cli, 'build', 'docs/architecture', '-o', 'artifacts/architecture', '--base', './']);
run('dotnet', ['restore', project, '--locked-mode']);
run('dotnet', ['build', project, '--no-restore', '--configuration', 'Release', '--warnaserror']);
run('dotnet', ['format', project, '--verify-no-changes', '--no-restore', '--severity', 'info']);
rmSync('artifacts/desktop', { recursive: true, force: true });
run('dotnet', ['publish', project, '--no-build', '--configuration', 'Release', '--output', 'artifacts/desktop']);
const endCommit = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' });
const endStatus = spawnSync('git', ['status', '--porcelain'], { encoding: 'utf8' });
if (endCommit.status !== 0 || endStatus.status !== 0 || endCommit.stdout !== commit.stdout || endStatus.stdout !== status.stdout) {
  throw new Error('Checkout changed during build; rerun validation');
}
mkdirSync('artifacts', { recursive: true });
const assemblySha256 = createHash('sha256').update(readFileSync('artifacts/desktop/DesktopShell.dll')).digest('hex');
writeFileSync('artifacts/build.json', JSON.stringify({ commit: commit.stdout.trim(), dirty, assemblySha256, platform: process.platform, architecture: process.arch, node: process.version }, null, 2));
