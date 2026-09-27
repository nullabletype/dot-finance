import { spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve, sep } from 'node:path';
import { tmpdir } from 'node:os';

// Mutate disposable copies, never the checkout. Compile errors and infrastructure failures
// cannot count as killed mutations: the output must report actual failed tests.
const scratch = mkdtempSync(join(tmpdir(), 'dot-finance-domain-mutations-'));
const project = 'tests/DotFinance.Domain.Tests/DotFinance.Domain.Tests.csproj';
function run(args) {
  const result = spawnSync('dotnet', args, { cwd: scratch, encoding: 'utf8', env: { ...process.env, MSBUILDDISABLENODEREUSE: '1' } });
  if (result.error) throw result.error;
  return { status: result.status, output: `${result.stdout}\n${result.stderr}` };
}
const mutations = [
  ['funding sign', 'FundingCalculator.cs', '- balance - joint - shared', '- balance + joint - shared'],
  ['protected minimum', 'FundingCalculator.cs', 'Current(account) - account.ProtectedMinimum', 'Current(account)'],
  ['first acknowledgement lock', 'ReconciliationBook.cs', 'Locked = true,\n            CurrentBalances = balances', 'Locked = false,\n            CurrentBalances = balances'],
  ['settlement membership', 'ReconciliationBook.cs', '_settled.Add(row.Transaction.Id); _effective[row.Transaction.Id] = row;', '_effective[row.Transaction.Id] = row;'],
  ['correction reversal', 'ReconciliationBook.cs', 'oldSource, -before.CostGbp, -before.CardCostGbp', 'oldSource, before.CostGbp, -before.CardCostGbp'],
  ['completion shortfall gate', 'ReconciliationBook.cs', 'draft.Transfers.Any(x => !x.Acknowledged) || draft.Plan.Shortfalls.Length > 0', 'draft.Transfers.Any(x => !x.Acknowledged)'],
];
try {
  for (const file of ['global.json', 'Directory.Build.props', '.editorconfig']) cpSync(file, join(scratch, file));
  for (const directory of ['src/DotFinance.Domain', 'tests/DotFinance.Domain.Tests']) {
    mkdirSync(join(scratch, directory), { recursive: true });
    cpSync(directory, join(scratch, directory), { recursive: true, filter: source => !source.split(sep).some(part => ['bin', 'obj', 'TestResults'].includes(part)) });
  }
  const restore = run(['restore', project, '--locked-mode']);
  if (restore.status !== 0) throw new Error(`Mutation scratch restore failed:\n${restore.output}`);
  const testArgs = ['test', '--project', project, '--no-restore', '-c', 'Release'];
  const baseline = run(testArgs);
  if (baseline.status !== 0 || !/failed:\s+0\b/.test(baseline.output)) throw new Error(`Mutation baseline failed:\n${baseline.output}`);
  for (const [name, file, from, to] of mutations) {
    const path = resolve(scratch, 'src/DotFinance.Domain', file);
    const original = readFileSync(path, 'utf8');
    const normalised = original.replace(/\r\n/g, '\n');
    if (normalised.split(from).length !== 2) throw new Error(`Mutation anchor changed: ${name}`);
    writeFileSync(path, normalised.replace(from, to));
    const result = run(testArgs);
    writeFileSync(path, original);
    if (result.status === 0 || !/failed:\s+[1-9]\d*/.test(result.output)) throw new Error(`Mutation survived or failed outside tests: ${name}\n${result.output}`);
    console.log(`PASS: tests killed ${name}`);
  }
  console.log(`PASS: all ${mutations.length} domain mutations killed; clean baseline passed`);
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
