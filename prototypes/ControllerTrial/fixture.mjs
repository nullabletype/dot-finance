// Deliberate synthetic failure, never a substitute for repository validation.
const mode = process.argv[2];
console.log(`Synthetic controller gate: ${mode}`);
process.exitCode = mode === 'fail' ? 1 : 0;
