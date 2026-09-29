import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';

if (process.argv.length !== 2) {
  console.error('The import inspector accepts no command-line paths. Run it without arguments and use the hidden prompts.');
  process.exit(64);
}

const environment = {
  ...process.env,
  DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  TESTINGPLATFORM_TELEMETRY_OPTOUT: '1',
};
const project = 'tools/DotFinance.ImportInspector/DotFinance.ImportInspector.csproj';
const build = spawnSync('dotnet', ['build', project, '--no-restore', '--configuration', 'Release'], {
  env: environment,
  stdio: 'inherit',
});
if (build.error) throw build.error;
if (build.status !== 0) process.exit(build.status ?? 1);

const executableName = process.platform === 'win32' ? 'dot-finance-import-inspector.exe' : 'dot-finance-import-inspector';
const executable = resolve('tools', 'DotFinance.ImportInspector', 'bin', 'Release', 'net10.0', executableName);
const inspection = spawnSync(executable, ['inspect'], { env: environment, stdio: 'inherit' });
if (inspection.error) throw inspection.error;
process.exit(inspection.status ?? 1);
