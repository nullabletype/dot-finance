import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { selectValidationScope } from '../../scripts/validation-scope.mjs';

test('keeps routine pushes and unreviewed pull requests on the deterministic core gate', () => {
  assert.deepEqual(selectValidationScope({ eventName: 'push', changedPaths: ['src/DotFinance.Domain/ReconciliationBook.cs'] }), {
    fullValidation: false,
    persistenceValidation: false,
  });
  assert.deepEqual(selectValidationScope({ eventName: 'pull_request', reviewReady: false, changedPaths: ['src/DotFinance.Storage/EncryptedVault.cs'] }), {
    fullValidation: false,
    persistenceValidation: false,
  });
});

test('runs native validation without persistence for reviewed documentation', () => {
  for (const path of ['README.md', 'src/DotFinance.Desktop/App.cs']) {
    assert.deepEqual(selectValidationScope({ eventName: 'pull_request', reviewReady: true, changedPaths: [path] }), {
      fullValidation: true,
      persistenceValidation: false,
    }, path);
  }
});

test('runs persistence for reviewed workflow, production contract, and storage-test changes', () => {
  for (const path of [
    '.github/workflows/prototype.yml',
    'src/DotFinance.Application/ReconciliationCompletion.cs',
    'src/DotFinance.Domain/FinancialModel.cs',
    'src/DotFinance.Domain/ReconciliationBook.cs',
    'src/DotFinance.Storage/EncryptedVault.cs',
    'tests/DotFinance.Storage.Tests/EncryptedVaultTests.cs',
  ]) {
    assert.deepEqual(selectValidationScope({ eventName: 'pull_request', reviewReady: true, changedPaths: [path] }), {
      fullValidation: true,
      persistenceValidation: true,
    }, path);
  }
});

test('honours manual native and persistence choices independently', () => {
  assert.deepEqual(selectValidationScope({ eventName: 'workflow_dispatch', runNative: false, runPersistence: true }), {
    fullValidation: false,
    persistenceValidation: true,
  });
});

test('ignores unrelated label additions after a pull request enters review', () => {
  const workflow = readFileSync('.github/workflows/prototype.yml', 'utf8');
  assert.match(workflow, /if: \$\{\{ github\.event\.action != 'labeled' \|\| github\.event\.label\.name == 'in-review' \}\}/u);
  assert.match(workflow, /group: \$\{\{ github\.workflow \}\}-\$\{\{ github\.event\.action == 'labeled' && github\.event\.label\.name != 'in-review' && github\.run_id \|\| github\.event\.pull_request\.number \|\| github\.ref \}\}/u);
});

test('runs every architecture policy test in the deterministic production build', () => {
  const productionBuild = readFileSync('scripts/production-build.mjs', 'utf8');
  assert.match(productionBuild, /readdirSync\('tests\/Architecture'\)/u);
  assert.match(productionBuild, /\['--test', \.\.\.architectureTests\]/u);
});
