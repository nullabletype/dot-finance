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
  assert.deepEqual(selectValidationScope({ eventName: 'pull_request', reviewReady: true, changedPaths: ['README.md'] }), {
    fullValidation: true,
    persistenceValidation: false,
  });
});

test('runs persistence for reviewed workflow, production contract, and storage-test changes', () => {
  for (const path of [
    '.github/workflows/prototype.yml',
    'src/DotFinance.Application/ReconciliationCompletion.cs',
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
});
