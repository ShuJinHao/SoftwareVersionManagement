import { resolve, relative } from 'node:path';
import { mkdirSync } from 'node:fs';
import { root, paths, environment, dotnetCommand, run } from './toolchain.mjs';
const definitions = { architecture: ['Svm.ArchitectureTests', 'Architecture'],
  security: ['Svm.SecurityTests', 'Security'], framework: ['Svm.FrameworkTests', 'Business'], tools: ['tools', 'tools'] };
try {
  const options = process.argv.slice(2);
  const group = options[0] && !options[0].startsWith('--') ? options.shift() : 'architecture';
  const definition = Object.hasOwn(definitions, group) ? definitions[group] : null;
  if (!definition) throw new Error('Choose architecture, security, framework or tools.');
  let filter, preview = false, directory;
  for (let i = 0; i < options.length; i++) {
    const option = options[i];
    if (option === '--preview' && !preview) preview = true;
    else if (option === '--filter' && filter === undefined) {
      filter = options[++i];
      if (!filter?.trim() || filter.startsWith('--')) throw new Error('--filter requires an explicit test selection.');
    } else if (option === '--results-directory' && directory === undefined) {
      directory = options[++i];
      if (!directory?.trim() || directory.startsWith('--')) throw new Error('--results-directory requires a path.');
    } else throw new Error('Usage: eng/test group [--filter selection] [--preview] [--results-directory path]');
  }
  if (group === 'architecture' && filter !== undefined) throw new Error('Architecture always runs its complete group.');
  if (group !== 'architecture' && filter === undefined) throw new Error(group + ' requires --filter; no automatic broad test group.');
  const resultRoot = resolve(root, 'artifacts/test-results');
  const results = resolve(root, directory ?? 'artifacts/test-results/manual/' + group + '/' + Date.now() + '-' + process.pid);
  const resultPath = relative(resultRoot, results);
  if (resultPath === '..' || resultPath.startsWith('../') || resultPath.startsWith('..\\') || resolve(results) === resultRoot)
    throw new Error('Test results must stay under artifacts/test-results.');
  const project = group === 'tools' ? 'eng/delivery.test.mjs' : 'src/tests/' + definition[0] + '/' + definition[0] + '.csproj';
  const selection = group === 'tools' ? filter : 'Category=' + definition[1] + (filter === undefined ? '' : '&(' + filter + ')');
  console.log(JSON.stringify({ project, filter: selection, resultsDirectory: relative(root, results), preview }));
  if (!preview) {
    mkdirSync(results, { recursive: true });
    if (group === 'tools') process.exitCode = await run(paths().node,
      ['--test', '--test-name-pattern', filter, '--test-reporter=spec', '--test-reporter=junit',
       '--test-reporter-destination=stdout', '--test-reporter-destination=' + resolve(results, 'tools.xml'), project], { env: environment() });
    else {
      const dotnet = dotnetCommand();
      process.exitCode = await run(dotnet.command, ['test', project, '--no-build', '--no-restore', '--filter', selection,
        '--logger', 'trx', '--results-directory', results, '--verbosity', 'minimal'], { env: dotnet.env });
    }
  }
} catch (error) { console.error(error.message); process.exitCode = 1; }
