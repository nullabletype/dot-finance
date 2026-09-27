import { spawnSync } from 'node:child_process';
import { readdirSync } from 'node:fs';
import { relative, resolve, dirname, sep } from 'node:path';
import { pathToFileURL } from 'node:url';

const rules = {
  'DotFinance.Domain': { references: [], packages: ['Microsoft.NET.ILLink.Tasks'] },
  'DotFinance.Application': { references: ['DotFinance.Domain'], packages: ['Microsoft.NET.ILLink.Tasks'] },
  'DotFinance.Storage': { references: ['DotFinance.Domain'], packages: ['Microsoft.NET.ILLink.Tasks', 'Microsoft.Data.Sqlite.Core', 'SQLite3MC.PCLRaw.bundle', 'SQLitePCLRaw.core'] },
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
  console.log('PASS: evaluated production dependencies match ADR-0003');
}
