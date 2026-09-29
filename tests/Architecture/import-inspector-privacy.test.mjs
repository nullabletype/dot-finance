import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';

test('launches private inspection without command-line paths or dotnet run telemetry', () => {
  const packageManifest = JSON.parse(readFileSync('package.json', 'utf8'));
  const launcher = readFileSync('scripts/import-inspector.mjs', 'utf8');

  assert.equal(packageManifest.scripts['import:inspect'], 'node scripts/import-inspector.mjs');
  assert.match(launcher, /process\.argv\.length !== 2/u);
  assert.match(launcher, /DOTNET_CLI_TELEMETRY_OPTOUT: '1'/u);
  assert.match(launcher, /TESTINGPLATFORM_TELEMETRY_OPTOUT: '1'/u);
  assert.match(launcher, /spawnSync\(executable, \['inspect'\]/u);
  assert.doesNotMatch(launcher, /\['run'/u);
  assert.doesNotMatch(launcher, /https?:|fetch\(|request\(|connect\(|curl|wget/u);
});

test('allows only the pinned Open XML package and keeps outbound networking APIs absent', () => {
  const project = readFileSync('tools/DotFinance.ImportInspector/DotFinance.ImportInspector.csproj', 'utf8');
  assert.match(project, /PackageReference Include="DocumentFormat\.OpenXml" Version="3\.5\.1"/u);
  assert.match(project, /PackageReference Include="System\.IO\.Packaging" Version="10\.0\.12"/u);
  assert.equal((project.match(/PackageReference/gu) ?? []).length, 2);
  assert.doesNotMatch(project, /FrameworkReference/u);

  const source = readdirSync('tools/DotFinance.ImportInspector')
    .filter(path => path.endsWith('.cs'))
    .map(path => readFileSync(`tools/DotFinance.ImportInspector/${path}`, 'utf8'))
    .join('\n');
  assert.doesNotMatch(
    source,
    /System\.Net|HttpClient|WebClient|WebRequest|Dns|TcpClient|UdpClient|Socket|NetworkStream|Quic|TelemetryClient|Process\.Start|DllImport|LibraryImport/u,
  );
});
