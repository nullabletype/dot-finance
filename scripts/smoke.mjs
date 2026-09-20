import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, copyFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';

mkdirSync('artifacts', { recursive: true });
const output = mkdtempSync('artifacts/launch-');
const build = JSON.parse(readFileSync('artifacts/build.json', 'utf8'));
const current = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' });
const assemblySha256 = createHash('sha256').update(readFileSync('artifacts/desktop/DesktopShell.dll')).digest('hex');
if (current.status !== 0 || current.stdout.trim() !== build.commit || assemblySha256 !== build.assemblySha256) {
  throw new Error('Published assembly or commit differs from the validated build; run npm run build again.');
}
if (process.env.CI && build.dirty) throw new Error('CI acceptance requires a clean source commit.');
const result = spawnSync('dotnet', ['artifacts/desktop/DesktopShell.dll', '--smoke-test', output], {
  stdio: 'inherit', timeout: 30000, env: { ...process.env, PROTOTYPE_COMMIT: build.commit }
});
if (result.error) throw result.error;
if (result.status !== 0) throw new Error(`Native launch failed: ${result.status}`);
const evidence = JSON.parse(readFileSync(join(output, 'launch.json'), 'utf8'));
if (!evidence.windowOpened || evidence.commit !== build.commit || !existsSync(join(output, 'window.png'))) {
  throw new Error('Launch evidence is incomplete');
}
copyFileSync('artifacts/build.json', join(output, 'build.json'));
console.log(`PASS: native launch on ${evidence.os}${build.dirty ? ' (uncommitted build; provisional evidence)' : ''}. Evidence: ${output}`);
