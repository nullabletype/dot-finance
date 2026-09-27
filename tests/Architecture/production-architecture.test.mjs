import test from 'node:test';
import assert from 'node:assert/strict';
import { validateGraph } from '../../scripts/production-architecture.mjs';

function graph() {
  return [
    { name: 'DotFinance.Domain', references: [], packages: ['Microsoft.NET.ILLink.Tasks'], assemblyReferences: [], frameworks: ['Microsoft.NETCore.App'], sources: [] },
    { name: 'DotFinance.Application', references: ['src/DotFinance.Domain/DotFinance.Domain.csproj'], packages: [], assemblyReferences: [], frameworks: ['Microsoft.NETCore.App'], sources: [] },
    { name: 'DotFinance.Desktop', references: ['src/DotFinance.Application/DotFinance.Application.csproj'], packages: ['Avalonia.Desktop', 'Avalonia.Themes.Fluent'], assemblyReferences: [], frameworks: ['Microsoft.NETCore.App'], sources: ['src/DotFinance.Desktop/App.cs'] }
  ];
}

test('accepts inward references with presentation dependencies confined to Desktop', () => {
  assert.doesNotThrow(() => validateGraph(graph()));
});

for (const [label, mutate, error] of [
  ['Domain referencing Desktop', g => g[0].references.push('src/DotFinance.Desktop/DotFinance.Desktop.csproj'), /forbidden or missing project reference/],
  ['Desktop bypassing Application', g => g[2].references.push('src/DotFinance.Domain/DotFinance.Domain.csproj'), /forbidden or missing project reference/],
  ['a prototype project reference', g => g[2].references.push('prototypes/DesktopShell/DesktopShell.csproj'), /forbidden or missing project reference/],
  ['Avalonia in Domain', g => g[0].packages.push('Avalonia'), /unapproved package Avalonia/],
  ['SQLite in Application', g => g[1].packages.push('Microsoft.Data.Sqlite.Core'), /unapproved package Microsoft.Data.Sqlite.Core/],
  ['a raw assembly reference', g => g[0].assemblyReferences.push('Avalonia.Base'), /direct assembly references/],
  ['a server framework', g => g[1].frameworks.push('Microsoft.AspNetCore.App'), /unapproved framework reference/],
  ['linked prototype source', g => g[2].sources.push('prototypes/DesktopShell/WorkflowSession.cs'), /source outside its boundary/],
  ['an undeclared production project', g => g.push({ ...g[0], name: 'DotFinance.Other' }), /project inventory/],
  ['a missing production project', g => g.pop(), /project inventory/]
]) {
  test(`rejects ${label}`, () => {
    const value = graph();
    mutate(value);
    assert.throws(() => validateGraph(value), error);
  });
}
