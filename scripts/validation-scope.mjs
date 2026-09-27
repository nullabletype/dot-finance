import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const persistenceSensitive = [
  /^\.github\/workflows\/prototype\.yml$/,
  /^(global\.json|Directory\.Build\.props|DotFinance\.slnx|package(?:-lock)?\.json)$/,
  /^build\/DesktopDependencies\.props$/,
  /^scripts\/(?:build|persistence-smoke|production-build|production-smoke|production-vault-smoke)\.mjs$/,
  /^prototypes\/DesktopShell(?:\.Tests)?\//,
  /^src\/DotFinance\.Storage\//,
  /^src\/DotFinance\.Application\/ReconciliationCompletion\.cs$/,
  /^src\/DotFinance\.Domain\/(?:FinancialModel|ReconciliationBook)\.cs$/,
  /^src\/DotFinance\.Desktop\/(?:Program|StorageScenario|StorageScenarioJsonContext)\.cs$/,
  /^src\/[^/]+\/(?:[^/]+\.csproj|packages\.lock\.json)$/,
  /^src\/.*(?:Backup|Infrastructure|Persistence|Storage|Vault).*\.cs$/,
  /^tests\/DotFinance\.Storage\.Tests\//,
];

export function selectValidationScope({ eventName, reviewReady, runNative, runPersistence, changedPaths = [] }) {
  if (eventName === 'workflow_dispatch') {
    return {
      fullValidation: runNative,
      persistenceValidation: runPersistence,
    };
  }

  if (eventName !== 'pull_request' || !reviewReady) {
    return { fullValidation: false, persistenceValidation: false };
  }

  return {
    fullValidation: true,
    persistenceValidation: changedPaths.some(path => persistenceSensitive.some(pattern => pattern.test(path))),
  };
}

function boolean(value) {
  return value === 'true';
}

function run() {
  const eventName = process.env.EVENT_NAME ?? '';
  const reviewReady = boolean(process.env.REVIEW_READY);
  let changedPaths = [];
  if (eventName === 'pull_request' && reviewReady) {
    changedPaths = execFileSync('git', ['diff', '--name-only', process.env.BASE_SHA, process.env.HEAD_SHA], {
      encoding: 'utf8',
    }).split(/\r?\n/u).filter(Boolean);
  }

  const result = selectValidationScope({
    eventName,
    reviewReady,
    runNative: boolean(process.env.RUN_NATIVE),
    runPersistence: boolean(process.env.RUN_PERSISTENCE),
    changedPaths,
  });
  process.stdout.write(`full_validation=${result.fullValidation}\n`);
  process.stdout.write(`persistence_validation=${result.persistenceValidation}\n`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) run();
