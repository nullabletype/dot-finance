import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync, statSync, unlinkSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { resolve, join } from 'node:path';

const build = JSON.parse(readFileSync('artifacts/production-build.json', 'utf8'));
const git = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' });
const status = spawnSync('git', ['status', '--porcelain', '--untracked-files=all'], { encoding: 'utf8' });
if (git.status !== 0 || status.status !== 0 || git.stdout.trim() !== build.commit) throw new Error('Build commit no longer matches checkout');
if (process.env.CI && (build.dirty || status.stdout.trim())) throw new Error('CI requires clean committed source');
const source = resolve('artifacts', 'production', build.rid, build.executable);
if (createHash('sha256').update(readFileSync(source)).digest('hex') !== build.sha256) throw new Error('Executable changed since build');
mkdirSync('artifacts', { recursive: true });
const scratch = resolve(mkdtempSync('artifacts/production-launch-'));
const isolated = join(scratch, 'isolated');
mkdirSync(isolated);
const executable = join(isolated, build.executable);
copyFileSync(source, executable);
const extraction = join(scratch, 'extracted');
function launch(phase) {
  const evidence = join(scratch, phase);
  const token = randomUUID();
  const result = spawnSync(executable, ['--smoke-test', evidence], {
    cwd: isolated, encoding: 'utf8', timeout: 30000,
    env: { ...process.env, DOT_FINANCE_SMOKE_TOKEN: token, DOTNET_BUNDLE_EXTRACT_BASE_DIR: extraction, DOTNET_EnableDiagnostics: '0' }
  });
  if (result.error || result.status !== 0) throw new Error(`${phase} launch failed: ${result.error ?? result.stderr ?? result.status}`);
  if (readFileSync(join(evidence, 'opened.txt'), 'utf8') !== token) throw new Error(`${phase}: missing native-window receipt`);
  const screenshot = readFileSync(join(evidence, 'window.png'));
  if (!screenshot.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))) throw new Error(`${phase}: missing rendered window evidence`);
  if (readdirSync(isolated).join() !== build.executable) throw new Error(`${phase}: standalone directory acquired sidecars`);
}
function inventory() {
  return readdirSync(extraction, { recursive: true }).filter(path => /\.(dll|dylib|so)$/.test(path)).sort().map(path => ({
    path, sha256: createHash('sha256').update(readFileSync(join(extraction, path))).digest('hex'), modified: statSync(join(extraction, path)).mtimeMs
  }));
}
launch('cold');
const cold = inventory();
const skia = cold.find(file => /SkiaSharp/.test(file.path));
if (!skia) throw new Error('Native extraction was not exercised');
launch('warm');
if (JSON.stringify(inventory()) !== JSON.stringify(cold)) throw new Error('Warm launch replaced existing native files');
unlinkSync(join(extraction, skia.path));
launch('recovered');
const recovered = inventory();
if (recovered.length !== cold.length || recovered.find(file => file.path === skia.path)?.sha256 !== skia.sha256) throw new Error('Missing native file was not recovered');
const update = resolve('artifacts', 'production-update', build.rid, build.executable);
if (createHash('sha256').update(readFileSync(update)).digest('hex') !== build.updateSha256) throw new Error('Update probe differs from build receipt');
copyFileSync(update, executable);
launch('updated');
const updated = inventory();
if (updated.length !== cold.length * 2) throw new Error('Updated bundle did not use a distinct extraction identity');
for (const file of recovered) {
  const retained = updated.find(candidate => candidate.path === file.path);
  if (!retained || JSON.stringify(retained) !== JSON.stringify(file)) throw new Error('Update modified the previous extraction');
}
copyFileSync(source, executable);
launch('rollback');
if (JSON.stringify(inventory()) !== JSON.stringify(updated)) throw new Error('Rollback changed the extraction cache');
writeFileSync(join(scratch, 'launch.json'), JSON.stringify({ ...build, windowOpened: true, phases: ['cold', 'warm', 'recovered', 'updated', 'rollback'], extracted: updated, os: process.platform, arch: process.arch }, null, 2));
console.log(`PASS: standalone production launch, cache reuse/recovery, replacement and rollback (${build.rid}); evidence: ${scratch}`);
