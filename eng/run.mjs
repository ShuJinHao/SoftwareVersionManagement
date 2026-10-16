import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { paths, root, manifest, environment, verifyInstallation, dotnetCommand, run } from './toolchain.mjs';
try {
  const [tool, ...args] = process.argv.slice(2);
  verifyInstallation('node');
  if (process.version !== 'v' + manifest.node) throw new Error('Use the reviewed repository Node installation.');
  if (tool === 'dotnet') {
    const dotnet = dotnetCommand();
    process.exitCode = await run(dotnet.command, args, { env: dotnet.env });
  } else if (tool === 'node') process.exitCode = await run(paths().node, args, { env: environment() });
  else if (tool === 'npm') {
    if (execFileSync(paths().node, [paths().npm, '--version'], {env: environment(), encoding:'utf8'}).trim() !== manifest.npm)
      throw new Error('Bundled npm version differs from the reviewed installation.');
    process.exitCode = await run(paths().node, [paths().npm, ...args], { env: environment() });
  }
  else if (['test', 'publish', 'postgres', 'rabbitmq', 'personnel', 'site'].includes(tool))
    process.exitCode = await run(paths().node, [join(root, 'eng/' + tool + '.mjs'), ...args], { env: environment() });
  else throw new Error('Unknown repository tool.');
} catch (error) { console.error(error.message); process.exitCode = 1; }
