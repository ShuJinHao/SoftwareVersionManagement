import { execFileSync } from 'node:child_process';
import { resolve, relative } from 'node:path';
import { mkdirSync, statSync, realpathSync } from 'node:fs';
import { root, paths, environment, dotnetCommand, run } from './toolchain.mjs';
const definitions = { architecture: ['Svm.ArchitectureTests', 'Architecture'],
  security: ['Svm.SecurityTests', 'Security'], framework: ['Svm.FrameworkTests', 'Business'], tools: ['tools', 'tools'], web: ['web', 'web'] };
try {
  const options = process.argv.slice(2);
  const group = options[0] && !options[0].startsWith('--') ? options.shift() : 'architecture';
  const definition = Object.hasOwn(definitions, group) ? definitions[group] : null;
  if (!definition) throw new Error('Choose architecture, security, framework, tools or web.');
  let filter, preview = false, list = false, directory; const files = [];
  for (let i = 0; i < options.length; i++) {
    const option = options[i];
    if (option === '--preview' && !preview) preview = true;
    else if (option === '--list' && !list) list = true;
    else if (option === '--file') {
      const file = options[++i];
      if (!file || !/^tests\/[A-Za-z0-9_.-]+\.test\.ts$/.test(file) || files.includes(file))
        throw new Error('--file requires a distinct exact tests/name.test.ts path under the web project.');
      files.push(file);
    }
    else if (option === '--filter' && filter === undefined) {
      filter = options[++i];
      if (!filter?.trim() || filter.startsWith('--')) throw new Error('--filter requires an explicit test selection.');
    } else if (option === '--results-directory' && directory === undefined) {
      directory = options[++i];
      if (!directory?.trim() || directory.startsWith('--')) throw new Error('--results-directory requires a path.');
    } else throw new Error('Usage: eng/test group [--file tests/name.test.ts] [--filter selection] [--preview | --list] [--results-directory path]');
  }
  if (group === 'architecture' && filter !== undefined) throw new Error('Architecture always runs its complete group.');
  if (group !== 'architecture' && filter === undefined) throw new Error(group + ' requires --filter; no automatic broad test group.');
  if (files.length && group !== 'web' || list && group !== 'web' || list && preview) throw new Error('--file and --list are web-only; choose --preview or --list.');
  const webRoot = resolve(root, 'src/ui/svm-web');
  if (group === 'web') {
    if (!files.length) throw new Error('web requires exact --file paths and --filter names; no automatic broad web suite.');
    if (['.*', '^.*$', '.+', '^.+$'].includes(filter.trim())) throw new Error('web requires selected test names rather than an all-tests pattern.');
    try { new RegExp(filter); } catch { throw new Error('--filter must be a valid test-name regular expression.'); }
    for (const file of files) {
      const full = resolve(webRoot, file);
      if (!statSync(full).isFile() || realpathSync(full) !== full) throw new Error('--file must reference an existing regular test file without links.');
    }
  }
  const resultRoot = resolve(root, 'artifacts/test-results');
  const results = resolve(root, directory ?? 'artifacts/test-results/manual/' + group + '/' + Date.now() + '-' + process.pid);
  const resultPath = relative(resultRoot, results);
  if (resultPath === '..' || resultPath.startsWith('../') || resultPath.startsWith('..\\') || resolve(results) === resultRoot)
    throw new Error('Test results must stay under artifacts/test-results.');
  const project = group === 'web' ? 'src/ui/svm-web' : group === 'tools' ? 'eng/delivery.test.mjs' : 'src/tests/' + definition[0] + '/' + definition[0] + '.csproj';
  const selection = ['tools', 'web'].includes(group) ? filter : 'Category=' + definition[1] + (filter === undefined ? '' : '&(' + filter + ')');
  console.log(JSON.stringify({ project, ...(group === 'web' ? { files } : {}), filter: selection, resultsDirectory: relative(root, results), preview, list }));
  if (!preview) {
    if (!list) mkdirSync(results, { recursive: true });
    if (group === 'web') {
      const runner = resolve(webRoot, 'node_modules/vitest/vitest.mjs');
      const found = JSON.parse(execFileSync(paths().node, [runner, 'list', ...files, '--testNamePattern', filter, '--json'],
        { cwd: webRoot, env: environment(), encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
      if (!Array.isArray(found) || !found.length) throw new Error('The selected web files and names match no tests.');
      if (list) console.log(JSON.stringify(found, null, 2));
      else process.exitCode = await run(paths().node,
        [runner, 'run', ...files, '--testNamePattern', filter, '--reporter=default', '--reporter=junit', '--outputFile.junit=' + resolve(results, 'web.xml')],
        { cwd: webRoot, env: environment() });
    }
    else if (group === 'tools') process.exitCode = await run(paths().node,
      ['--test', '--test-name-pattern', filter, '--test-reporter=spec', '--test-reporter=junit',
       '--test-reporter-destination=stdout', '--test-reporter-destination=' + resolve(results, 'tools.xml'), project], { env: environment() });
    else {
      const dotnet = dotnetCommand();
      process.exitCode = await run(dotnet.command, ['test', project, '--no-build', '--no-restore', '--filter', selection,
        '--logger', 'trx', '--results-directory', results, '--verbosity', 'minimal'], { env: dotnet.env });
    }
  }
} catch (error) { console.error(error.message); process.exitCode = 1; }
