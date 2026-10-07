import { spawn } from 'node:child_process';
import { dirname, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const definitions = {
  architecture: ['Svm.ArchitectureTests', 'Architecture'],
  security: ['Svm.SecurityTests', 'Security'],
  framework: ['Svm.FrameworkTests', 'Business']
};
const options = process.argv.slice(2);
const group = options[0] && !options[0].startsWith('--') ? options.shift() : 'architecture';
const fail = message => { throw new Error(message); };

try {
  const definition = Object.hasOwn(definitions, group) ? definitions[group] : null;
  if (!definition) fail('Choose architecture, security or framework.');
  let filter, preview = false, directory;
  for (let i = 0; i < options.length; i++) {
    const option = options[i];
    if (option === '--preview' && !preview) preview = true;
    else if (option === '--filter' && filter === undefined) {
      filter = options[++i];
      if (!filter?.trim() || filter.startsWith('--')) fail('--filter requires an explicit test selection.');
    } else if (option === '--results-directory' && directory === undefined) {
      directory = options[++i];
      if (!directory?.trim() || directory.startsWith('--')) fail('--results-directory requires a path under artifacts/test-results.');
    } else fail('Usage: eng/test [architecture|security|framework] [--filter selection] [--preview] [--results-directory path]');
  }
  if (group === 'architecture' && filter !== undefined) fail('Architecture runs the complete architecture group; --filter is not accepted.');
  if (group !== 'architecture' && filter === undefined) fail(`${group} requires --filter; there is no automatic broad test group.`);
  const resultRoot = resolve(root, 'artifacts/test-results');
  const results = resolve(root, directory ?? `artifacts/test-results/manual/${group}/${Date.now()}-${process.pid}`);
  const resultPath = relative(resultRoot, results);
  if (resultPath === '..' || resultPath.startsWith('../') || resultPath.startsWith('..\\')) fail('Test results must stay under artifacts/test-results.');
  const project = `src/tests/${definition[0]}/${definition[0]}.csproj`;
  const selection = `Category=${definition[1]}` + (filter === undefined ? '' : `&(${filter})`);
  const args = ['test', project, '--no-build', '--no-restore', '--filter', selection,
    '--logger', 'trx', '--results-directory', results, '--verbosity', 'minimal'];
  console.log(JSON.stringify({ project, filter: selection, resultsDirectory: relative(root, results), preview }));
  if (!preview) {
    process.exitCode = await new Promise((accept, reject) => {
      const child = spawn(resolve(root, 'eng/dotnet'), args, { cwd: root, stdio: 'inherit', env: process.env });
      child.on('error', reject);
      child.on('exit', (code, signal) => accept(code ?? (signal ? 130 : 1)));
    });
  }
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
