import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const directory = mkdtempSync(join(tmpdir(), 'dot-finance-invalid-model-'));
try {
  writeFileSync(join(directory, 'invalid.c4'), 'model { deliberately invalid syntax !!!');
  const result = spawnSync(process.execPath, ['node_modules/likec4/bin/likec4.mjs', 'validate', directory], { encoding: 'utf8' });
  if (result.error) throw result.error;
  if (result.status === null || result.status === 0 || !/invalid\.c4/.test(result.stdout + result.stderr)) {
    throw new Error(`Invalid-model gate did not report the expected fixture error: ${result.stdout}${result.stderr}`);
  }
  console.log('PASS: LikeC4 rejects the deliberately invalid model.');
} finally {
  rmSync(directory, { recursive: true, force: true });
}
