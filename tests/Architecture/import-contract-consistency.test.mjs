import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');

function source(path) {
  return readFileSync(resolve(root, path), 'utf8');
}

function expectedHeaders(text) {
  const declaration = text.match(/ExpectedHeaders\s*=\s*\[(?<body>[\s\S]*?)\];/);
  assert.ok(declaration?.groups?.body, 'ExpectedHeaders declaration was not found');
  return [...declaration.groups.body.matchAll(/"([^"]+)"/g)].map(match => match[1]);
}

test('keeps the private inspector and production importer aligned on the verified header contract', () => {
  const importer = source('src/DotFinance.Import/EmmaXlsxImporter.cs');
  const inspector = source('tools/DotFinance.ImportInspector/ExportInspector.cs');

  assert.deepEqual(expectedHeaders(importer), expectedHeaders(inspector));
  assert.match(importer, /MaximumInputBytes\s*=\s*32\s*\*\s*1024\s*\*\s*1024/);
  assert.match(inspector, /MaximumInputBytes\s*=\s*32\s*\*\s*1024\s*\*\s*1024/);
});
