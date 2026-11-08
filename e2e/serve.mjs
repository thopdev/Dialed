// Serves a published Dialed.Web (the wwwroot folder) like Azure Static Web Apps would:
// static files, and index.html for every route that is not a file (client-side routing).
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, resolve } from 'node:path';

const root = resolve(process.env.E2E_ROOT ?? '../publish/wwwroot');
const port = Number(process.env.E2E_PORT ?? 5080);

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.webmanifest': 'application/manifest+json',
  '.wasm': 'application/wasm',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.dll': 'application/octet-stream',
  '.dat': 'application/octet-stream',
  '.blat': 'application/octet-stream',
  '.pdb': 'application/octet-stream',
};

async function fileFor(urlPath) {
  const relative = normalize(decodeURIComponent(urlPath)).replace(/^(\.\.[/\\])+/, '');
  const candidate = join(root, relative);
  if (!candidate.startsWith(root)) {
    return null;
  }
  try {
    if ((await stat(candidate)).isFile()) {
      return candidate;
    }
  } catch {
    // not a file: fall through
  }
  return null;
}

createServer(async (request, response) => {
  const { pathname } = new URL(request.url ?? '/', 'http://localhost');
  let file = await fileFor(pathname);
  // Routes without a file extension are app routes; a missing file with an extension is a real 404.
  if (!file && extname(pathname) === '') {
    file = join(root, 'index.html');
  }
  if (!file) {
    response.writeHead(404).end('Not found');
    return;
  }
  const body = await readFile(file);
  response.writeHead(200, {
    'content-type': types[extname(file)] ?? 'application/octet-stream',
    'cache-control': 'no-cache',
  });
  response.end(body);
}).listen(port, () => console.log(`Serving ${root} on http://localhost:${port}`));
