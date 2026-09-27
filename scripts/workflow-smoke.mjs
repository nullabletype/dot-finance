import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const output = 'artifacts/workflow-local';
rmSync(output, { recursive: true, force: true });
mkdirSync(output, { recursive: true });

const build = JSON.parse(readFileSync('artifacts/build.json', 'utf8'));
const current = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' });
const assemblySha256 = createHash('sha256').update(readFileSync('artifacts/desktop/DesktopShell.dll')).digest('hex');
if (current.status !== 0 || current.stdout.trim() !== build.commit || assemblySha256 !== build.assemblySha256) {
  throw new Error('Published assembly or commit differs from the validated build; run npm run build again.');
}

const result = spawnSync('dotnet', ['artifacts/desktop/DesktopShell.dll', '--workflow-capture', output], {
  stdio: 'inherit',
  timeout: 30000
});
if (result.error) throw result.error;
if (result.status !== 0) throw new Error(`Workflow capture failed: ${result.status}`);

for (const name of ['review-before-light.png', 'review-before-dark.png', 'review-compact-light.png', 'review-compact-dark.png', 'review-after-light.png', 'review-after-dark.png']) {
  if (!existsSync(join(output, name))) throw new Error(`Workflow capture is missing ${name}`);
}

const evidence = JSON.parse(readFileSync(join(output, 'workflow.json'), 'utf8'));
if (evidence.compactLayout.ledgerOverlapsInspector) {
  throw new Error('Compact ledger overlaps the transaction inspector.');
}
if (!evidence.compactLayout.horizontalOverflow || evidence.compactLayout.horizontalScrollMode !== 'Auto') {
  throw new Error('Compact ledger must retain its column width and scroll horizontally inside its frame.');
}
if (!evidence.compactLayout.filterHorizontalOverflow || evidence.compactLayout.filterScrollMode !== 'Auto' || !evidence.compactLayout.filterClipsToFrame) {
  throw new Error('Compact filters must scroll horizontally inside their clipped frame.');
}
const checks = [
  ['base Joint', evidence.beforeAcceptedCorrection.jointContribution, evidence.expected.beforeAcceptedCorrection.joint],
  ['base Shared Savings', evidence.beforeAcceptedCorrection.sharedSavingsContribution, evidence.expected.beforeAcceptedCorrection.sharedSavings],
  ['base Personal', evidence.beforeAcceptedCorrection.additionalPersonalTransfers, evidence.expected.beforeAcceptedCorrection.personal],
  ['base required review', evidence.beforeAcceptedCorrection.requiresReview, evidence.expected.beforeAcceptedCorrection.requiresReview],
  ['corrected Joint', evidence.afterAcceptedCorrection.jointContribution, evidence.expected.afterAcceptedCorrection.joint],
  ['corrected required review', evidence.afterAcceptedCorrection.requiresReview, evidence.expected.afterAcceptedCorrection.requiresReview],
  ['Joint bill contribution', evidence.jointBill.jointContribution, evidence.expected.jointBill.joint],
  ['Joint bill Personal', evidence.jointBill.additionalPersonalTransfers, evidence.expected.jointBill.personal],
  ['refund', evidence.refund.jointContribution, evidence.expected.refund.joint],
  ['Joint shortfall', evidence.insufficientFunds.shortfalls.find(item => item.account === 'Joint')?.amount, evidence.expected.insufficientFunds.shortfall],
  ['duplicates added', evidence.duplicateImport.added, evidence.expected.duplicateImport.added],
  ['duplicates skipped', evidence.duplicateImport.skipped, evidence.expected.duplicateImport.skipped],
  ['unresolved review blocked', evidence.reviewBlock.blocked, evidence.expected.reviewBlock.blocked],
  ['unresolved review did not lock', evidence.reviewBlock.locked, evidence.expected.reviewBlock.locked],
  ['non-GBP initially requires review', evidence.nonGbp.initiallyRequiresReview, true],
  ['non-GBP review clears after GBP entry', evidence.nonGbp.requiresReviewAfterGbpEntry, false],
  ['non-GBP actual amount', evidence.nonGbp.actualGbpAmount, evidence.expected.nonGbp.actualGbpAmount],
  ['non-GBP Joint contribution', evidence.nonGbp.jointContribution, evidence.expected.nonGbp.jointContribution],
  ['correction queued', evidence.correction.queued, evidence.expected.correction.queued],
  ['correction consumed', evidence.correction.remaining, evidence.expected.correction.remaining],
  ['correction adjustment transactions', evidence.correction.adjustmentTransactions, evidence.expected.correction.adjustmentTransactions],
  ['correction Joint adjustment', evidence.correction.jointAdjustment, evidence.expected.correction.jointAdjustment],
  ['correction preserves history', evidence.correction.preservedOriginalAmount, evidence.expected.correction.preservedOriginalAmount],
  ['accepted correction replay skipped', evidence.correction.acceptedReplaySkipped, evidence.expected.correction.acceptedReplaySkipped],
  ['changed target blocks reopen', evidence.reopenGuard.changedTargetBlocksReopen, evidence.expected.reopenGuard.changedTargetBlocksReopen]
];
for (const [name, actual, expected] of checks) {
  if (actual !== expected) throw new Error(`${name}: expected ${expected}, got ${actual}`);
}

console.log(`PASS: guided workflow evidence and six responsive theme screenshots captured in ${output}${build.dirty ? ' (uncommitted build; provisional evidence)' : ''}.`);
