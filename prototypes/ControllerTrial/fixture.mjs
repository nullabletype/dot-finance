// Deliberate synthetic failure, never a substitute for repository validation.
import { spawn, spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
const mode = process.argv[2];
console.log(`Synthetic controller gate: ${mode}`);
process.exitCode = mode === 'fail' ? 1 : 0;
if (mode === 'interrupt') {
  console.log(`Interrupting controller while fixture ${process.pid} is running`);
  setInterval(() => {}, 1000);
  process.kill(Number(process.argv[3]), 'SIGKILL');
}
if (mode === 'hang') {
  const grandchild = spawn(process.execPath, [process.argv[1], 'leaf'], { stdio: 'inherit' });
  console.log(`Grandchild PID: ${grandchild.pid}`);
  setInterval(() => {}, 1000);
}
if (mode === 'leaf') setInterval(() => {}, 1000);
if (mode === 'infrastructure') {
  const result = spawnSync('dot-finance-controller-deliberately-unavailable-tool');
  process.exitCode = result.error?.code === 'ENOENT' ? 75 : 1;
}
if (mode === 'fail-dirty') {
  writeFileSync(`.controller-trial-dirty-${Number(process.argv[3])}`, 'Synthetic checkout mutation\n', { flag: 'wx' });
  process.exitCode = 1;
}
