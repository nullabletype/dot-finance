// Disposable process supervisor. Survives controller SIGKILL and stops its
// validation process group on owner death or deadline. No arbitrary commands.
import { spawn, spawnSync } from 'node:child_process';
import { openSync, closeSync, readFileSync, writeFileSync } from 'node:fs';
const [action, mode, owner, milliseconds, receiptPath] = process.argv.slice(2);
if (!['check', 'build'].includes(action)) throw new Error('Unknown validation command');
const args = action === 'build' ? ['scripts/build.mjs'] :
  ['prototypes/ControllerTrial/fixture.mjs', mode, owner];
const outputPath = `${receiptPath}.output`;
const output = openSync(outputPath, 'wx', 0o600);
const child = spawn(process.execPath, args, { detached: process.platform !== 'win32', stdio: ['ignore', output, output] });
let reason;
let error;
function terminate(why) {
  if (reason) return;
  reason = why;
  if (!child.pid) return;
  if (process.platform === 'win32') {
    const result = spawnSync('taskkill', ['/pid', String(child.pid), '/t', '/f'], { stdio: 'ignore' });
    if (result.status !== 0) error = { code: 'PROCESS_TREE_TERMINATION_UNVERIFIED' };
  } else {
    try { process.kill(-child.pid, 'SIGKILL'); }
    catch (failure) { if (failure.code !== 'ESRCH') error = { code: failure.code }; }
  }
}
const deadline = setTimeout(() => terminate('deadline'), Number(milliseconds));
const watchdog = setInterval(() => {
  try { process.kill(Number(owner), 0); }
  catch { terminate('controller-interrupted'); }
}, 100);
process.on('SIGTERM', () => terminate('supervisor-terminated'));
process.on('SIGINT', () => terminate('supervisor-interrupted'));
child.on('error', failure => { error = { code: failure.code }; });
child.on('close', (status, signal) => {
  clearTimeout(deadline);
  clearInterval(watchdog);
  closeSync(output);
  writeFileSync(receiptPath, JSON.stringify({ status: reason ? null : status, signal, reason, error,
    pid: child.pid, stdout: readFileSync(outputPath, 'utf8'), stderr: '' }), { flush: true, mode: 0o600 });
});
