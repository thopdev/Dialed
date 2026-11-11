// Serves a published Dialed.Web (the wwwroot folder) like Azure Static Web Apps would:
// static files, and index.html for every route that is not a file (client-side routing).
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { extname, join, normalize, resolve } from 'node:path';

const root = resolve(process.env.E2E_ROOT ?? '../publish/wwwroot');
const port = Number(process.env.E2E_PORT ?? 5080);
// The Development API (started by playwright.config.ts) for the sign-in and sync tests. The published app has no Api:BaseUrl, so it calls
// its own origin; this server passes those calls on. Same origin means the real CSP (connect-src 'self') and no CORS changes are needed.
const api = process.env.E2E_API ? new URL(process.env.E2E_API) : null;
const apiPrefixes = ['/auth/', '/sync/', '/households', '/friends/', '/ai/', '/account/'];

// The headers Static Web Apps sends (CSP, nosniff, ...), read from the same file the deployment uses, so the tests run under the real policy.
const config = JSON.parse(await readFile(fileURLToPath(new URL('../src/Dialed.Web/wwwroot/staticwebapp.config.json', import.meta.url)), 'utf8'));
const globalHeaders = Object.fromEntries(Object.entries(config.globalHeaders ?? {}).map(([name, value]) => [name.toLowerCase(), value]));

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

// API calls are fetches; a page load of an app route with the same name (a navigation) still gets index.html.
function isApiCall(request, pathname) {
  const navigation = request.headers['sec-fetch-mode'] === 'navigate' || (request.headers.accept ?? '').includes('text/html');
  return api !== null && !navigation && apiPrefixes.some(prefix => pathname.startsWith(prefix));
}

async function proxyToApi(request, response) {
  const chunks = [];
  for await (const chunk of request) {
    chunks.push(chunk);
  }
  const headers = { ...request.headers, host: api.host };
  delete headers.connection;
  try {
    const upstream = await fetch(new URL(request.url ?? '/', api), {
      method: request.method,
      headers,
      body: chunks.length > 0 ? Buffer.concat(chunks) : undefined,
      redirect: 'manual',
    });
    const body = Buffer.from(await upstream.arrayBuffer());
    const upstreamHeaders = Object.fromEntries([...upstream.headers].filter(([name]) => !['content-encoding', 'content-length', 'transfer-encoding', 'connection'].includes(name)));
    response.writeHead(upstream.status, upstreamHeaders).end(body);
  } catch (error) {
    response.writeHead(502).end(`API not reachable: ${error.message}`);
  }
}

createServer(async (request, response) => {
  const { pathname } = new URL(request.url ?? '/', 'http://localhost');
  if (isApiCall(request, pathname)) {
    await proxyToApi(request, response);
    return;
  }
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
    ...globalHeaders,
    'content-type': types[extname(file)] ?? 'application/octet-stream',
    'cache-control': 'no-cache',
  });
  response.end(body);
}).listen(port, () => console.log(`Serving ${root} on http://localhost:${port}${api ? `, API calls go to ${api.origin}` : ''}`));
