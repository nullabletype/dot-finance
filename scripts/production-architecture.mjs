import { spawnSync } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
import { relative, resolve, dirname, sep } from 'node:path';
import { pathToFileURL } from 'node:url';

const rules = {
  'DotFinance.Domain': { references: [], packages: ['Microsoft.NET.ILLink.Tasks'] },
  'DotFinance.Application': { references: ['DotFinance.Domain'], packages: ['Microsoft.NET.ILLink.Tasks'] },
  'DotFinance.Import': { references: [], packages: ['Microsoft.NET.ILLink.Tasks', 'DocumentFormat.OpenXml', 'DocumentFormat.OpenXml.Framework', 'System.IO.Packaging'] },
  'DotFinance.Storage': { references: ['DotFinance.Application'], packages: ['Microsoft.NET.ILLink.Tasks', 'Microsoft.Data.Sqlite.Core', 'SQLite3MC.PCLRaw.bundle', 'SQLitePCLRaw.core'] },
  'DotFinance.Desktop': { references: ['DotFinance.Application', 'DotFinance.Storage'], packages: ['Microsoft.NET.ILLink.Tasks', 'Avalonia.Desktop', 'Avalonia.Themes.Fluent', 'SkiaSharp', 'SkiaSharp.NativeAssets.Linux', 'SkiaSharp.NativeAssets.WebAssembly', 'HarfBuzzSharp', 'HarfBuzzSharp.NativeAssets.Linux', 'HarfBuzzSharp.NativeAssets.WebAssembly', 'Tmds.DBus.Protocol'] }
};
const projectPath = name => `src/${name}/${name}.csproj`;

export function validateGraph(projects) {
  const names = projects.map(project => project.name).sort();
  if (JSON.stringify(names) !== JSON.stringify(Object.keys(rules).sort())) throw new Error('Production project inventory differs from ADR-0003');
  for (const project of projects) {
    const rule = rules[project.name];
    const expected = rule.references.map(projectPath).sort();
    if (JSON.stringify([...project.references].sort()) !== JSON.stringify(expected)) throw new Error(`${project.name}: forbidden or missing project reference`);
    for (const name of project.packages) {
      if (!rule.packages.includes(name)) throw new Error(`${project.name}: unapproved package ${name}`);
    }
    if (project.assemblyReferences.length) throw new Error(`${project.name}: direct assembly references bypass project boundaries`);
    if (project.frameworks.some(name => name !== 'Microsoft.NETCore.App')) throw new Error(`${project.name}: unapproved framework reference`);
    for (const source of project.sources) {
      if (!source.startsWith(`src/${project.name}/`)) throw new Error(`${project.name}: source outside its boundary: ${source}`);
    }
  }
}

export function validateAdapterConfinement(root = process.cwd()) {
  const desktopRoot = resolve(root, 'src', 'DotFinance.Desktop');
  const allowed = new Set(['StorageScenario.cs']);
  for (const path of readdirSync(desktopRoot, { recursive: true }).filter(path => path.endsWith('.cs'))) {
    const source = readFileSync(resolve(desktopRoot, path), 'utf8');
    if ((source.includes('DotFinance.Storage') || source.includes('EncryptedVault')) && !allowed.has(path))
      throw new Error(`DotFinance.Desktop: storage adapter usage escaped composition/native validation: ${path}`);
  }
}

export function validateImportBoundary(root = process.cwd()) {
  const importRoot = resolve(root, 'src', 'DotFinance.Import');
  const forbidden = /System\.Net|HttpClient|WebClient|WebRequest|Dns|TcpClient|UdpClient|Socket|NetworkStream|Quic|TelemetryClient|Process\.Start|DllImport|LibraryImport/u;
  for (const path of readdirSync(importRoot, { recursive: true })
    .filter(path => path.endsWith('.cs') && !path.startsWith(`obj${sep}`) && !path.startsWith(`bin${sep}`))) {
    const source = readFileSync(resolve(importRoot, path), 'utf8');
    if (forbidden.test(source)) throw new Error(`DotFinance.Import: outbound or native execution API is forbidden: ${path}`);
  }
}

export function readGraph(root = process.cwd()) {
  const paths = readdirSync(resolve(root, 'src'), { recursive: true })
    .filter(path => path.endsWith('.csproj'));
  return paths.map(path => {
    const fullPath = resolve(root, 'src', path);
    const result = spawnSync('dotnet', ['msbuild', fullPath, '-nologo', '-property:Configuration=Release', '-getItem:ProjectReference,PackageReference,Reference,FrameworkReference,Compile'], { encoding: 'utf8', cwd: root });
    if (result.error || result.status !== 0) throw new Error(`Cannot evaluate ${path}: ${result.error ?? result.stderr ?? result.stdout}`);
    const items = JSON.parse(result.stdout).Items;
    const normalise = value => relative(root, resolve(dirname(fullPath), value)).split(sep).join('/');
    return {
      name: path.split(sep).at(-1).replace(/\.csproj$/, ''),
      references: items.ProjectReference.map(item => normalise(item.FullPath)),
      packages: items.PackageReference.map(item => item.Identity),
      assemblyReferences: items.Reference.map(item => item.Identity),
      frameworks: items.FrameworkReference.map(item => item.Identity),
      sources: items.Compile.map(item => normalise(item.FullPath))
    };
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  validateGraph(readGraph());
  validateAdapterConfinement();
  validateImportBoundary();
  console.log('PASS: evaluated production dependencies and adapter confinement match ADR-0003');
}
