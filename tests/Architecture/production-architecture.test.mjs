import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { validateAdapterConfinement, validateGraph, validateImportBoundary } from '../../scripts/production-architecture.mjs';

function graph() {
  return [
    { name: 'DotFinance.Domain', references: [], packages: ['Microsoft.NET.ILLink.Tasks'], assemblyReferences: [], frameworks: ['Microsoft.NETCore.App'], sources: [] },
    { name: 'DotFinance.Application', references: ['src/DotFinance.Domain/DotFinance.Domain.csproj'], packages: [], assemblyReferences: [], frameworks: ['Microsoft.NETCore.App'], sources: [] },
    { name: 'DotFinance.Import', references: [], packages: ['DocumentFormat.OpenXml', 'DocumentFormat.OpenXml.Framework', 'System.IO.Packaging'], assemblyReferences: [], frameworks: ['Microsoft.NETCore.App'], sources: ['src/DotFinance.Import/EmmaXlsxImporter.cs'] },
    { name: 'DotFinance.Storage', references: ['src/DotFinance.Application/DotFinance.Application.csproj'], packages: ['Microsoft.Data.Sqlite.Core', 'SQLite3MC.PCLRaw.bundle', 'SQLitePCLRaw.core'], assemblyReferences: [], frameworks: ['Microsoft.NETCore.App'], sources: ['src/DotFinance.Storage/EncryptedVault.cs'] },
    { name: 'DotFinance.Desktop', references: ['src/DotFinance.Application/DotFinance.Application.csproj', 'src/DotFinance.Storage/DotFinance.Storage.csproj'], packages: ['Avalonia.Desktop', 'Avalonia.Themes.Fluent'], assemblyReferences: [], frameworks: ['Microsoft.NETCore.App'], sources: ['src/DotFinance.Desktop/App.cs'] }
  ];
}

test('accepts inward references with presentation dependencies confined to Desktop', () => {
  assert.doesNotThrow(() => validateGraph(graph()));
});

test('confines concrete storage usage to the Desktop validation composition source', () => {
  const root = mkdtempSync(join(tmpdir(), 'dot-finance-architecture-'));
  const desktop = join(root, 'src', 'DotFinance.Desktop');
  mkdirSync(desktop, { recursive: true });
  try {
    writeFileSync(join(desktop, 'StorageScenario.cs'), 'using DotFinance.Storage; EncryptedVault vault;');
    writeFileSync(join(desktop, 'App.cs'), 'internal sealed class App {}');
    assert.doesNotThrow(() => validateAdapterConfinement(root));
    writeFileSync(join(desktop, 'App.cs'), 'using DotFinance.Storage;');
    assert.throws(() => validateAdapterConfinement(root), /usage escaped composition/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('keeps the production importer offline and free of native execution APIs', () => {
  const root = mkdtempSync(join(tmpdir(), 'dot-finance-import-boundary-'));
  const importer = join(root, 'src', 'DotFinance.Import');
  mkdirSync(importer, { recursive: true });
  try {
    writeFileSync(join(importer, 'Importer.cs'), 'internal sealed class Importer {}');
    assert.doesNotThrow(() => validateImportBoundary(root));
    writeFileSync(join(importer, 'Importer.cs'), 'using System.Net.Http; internal sealed class Importer { HttpClient client = new(); }');
    assert.throws(() => validateImportBoundary(root), /outbound or native execution API/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

for (const [label, mutate, error] of [
  ['Domain referencing Desktop', g => g[0].references.push('src/DotFinance.Desktop/DotFinance.Desktop.csproj'), /forbidden or missing project reference/],
  ['Desktop bypassing Application', g => g[4].references.push('src/DotFinance.Domain/DotFinance.Domain.csproj'), /forbidden or missing project reference/],
  ['a prototype project reference', g => g[4].references.push('prototypes/DesktopShell/DesktopShell.csproj'), /forbidden or missing project reference/],
  ['Avalonia in Domain', g => g[0].packages.push('Avalonia'), /unapproved package Avalonia/],
  ['SQLite in Application', g => g[1].packages.push('Microsoft.Data.Sqlite.Core'), /unapproved package Microsoft.Data.Sqlite.Core/],
  ['a raw assembly reference', g => g[0].assemblyReferences.push('Avalonia.Base'), /direct assembly references/],
  ['a server framework', g => g[1].frameworks.push('Microsoft.AspNetCore.App'), /unapproved framework reference/],
  ['SQLite in Domain', g => g[0].packages.push('Microsoft.Data.Sqlite.Core'), /unapproved package Microsoft.Data.Sqlite.Core/],
  ['Avalonia in Storage', g => g[3].packages.push('Avalonia'), /unapproved package Avalonia/],
  ['a networking package in Import', g => g[2].packages.push('System.Net.Http'), /unapproved package System.Net.Http/],
  ['linked prototype source', g => g[4].sources.push('prototypes/DesktopShell/WorkflowSession.cs'), /source outside its boundary/],
  ['an undeclared production project', g => g.push({ ...g[0], name: 'DotFinance.Other' }), /project inventory/],
  ['a missing production project', g => g.pop(), /project inventory/]
]) {
  test(`rejects ${label}`, () => {
    const value = graph();
    mutate(value);
    assert.throws(() => validateGraph(value), error);
  });
}
