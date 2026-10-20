import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

// A separate prototype server; it never mounts the application, private files or APIs.
const port = Number(process.argv[2] ?? 4178);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Use a local port between 1024 and 65535.');
const files = new Map([
  ['/', ['index.html', 'text/html; charset=utf-8']],
  ['/index.html', ['index.html', 'text/html; charset=utf-8']],
  ['/app.js', ['app.js', 'text/javascript; charset=utf-8']],
  ['/model.js', ['model.js', 'text/javascript; charset=utf-8']],
  ['/style.css', ['style.css', 'text/css; charset=utf-8']],
]);
const server = createServer(async (request, response) => {
  response.setHeader('Cache-Control', 'no-store');
  response.setHeader('X-Content-Type-Options', 'nosniff');
  response.setHeader('Referrer-Policy', 'no-referrer');
  response.setHeader('Content-Security-Policy', "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'; img-src 'self'");
  if (!['GET', 'HEAD'].includes(request.method)) { response.writeHead(405, { Allow: 'GET, HEAD' }); response.end(); return; }
  const pathname = new URL(request.url, 'http://127.0.0.1').pathname;
  const entry = files.get(pathname);
  if (!entry) { response.writeHead(404); response.end('Prototype resource not found.'); return; }
  try {
    const body = await readFile(fileURLToPath(new URL('../design/workspace/' + entry[0], import.meta.url)));
    response.writeHead(200, { 'Content-Type': entry[1], 'Content-Length': body.length });
    response.end(request.method === 'HEAD' ? undefined : body);
  } catch { response.writeHead(500); response.end('Prototype resource unavailable.'); }
});
server.on('error', error => { console.error(error.message); process.exitCode = 1; });
server.listen(port, '127.0.0.1', () => console.log(`Workspace prototype: http://127.0.0.1:${port}/ · PID ${process.pid} · fictional data only`));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.close());
