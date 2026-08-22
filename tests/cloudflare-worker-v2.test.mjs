import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repositoryDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '..'
);
const workerSourcePath = path.join(
  repositoryDirectory,
  'cloudflare',
  'appdownload',
  'worker.js'
);
const temporaryDirectory = fs.mkdtempSync(
  path.join(os.tmpdir(), 'awoo-appdownload-worker-')
);
const temporaryWorkerPath = path.join(temporaryDirectory, 'worker.mjs');
fs.copyFileSync(workerSourcePath, temporaryWorkerPath);

const originalFetch = globalThis.fetch;
const calls = [];
const v2Catalog = {
  schemaVersion: 2,
  repository: 'Enkianssus/awoo-connectors',
  publicKeyId: 'bilincm-connectors-2026-01',
  connectors: {}
};

globalThis.fetch = async (input, init = {}) => {
  const target = String(input);
  calls.push({ target, init });
  const targetUrl = new URL(target);

  if (
    targetUrl.hostname === 'raw.githubusercontent.com'
    && targetUrl.pathname.endsWith('/main/catalog-v2.json')
  ) {
    return new Response(
      init.method === 'HEAD' ? null : JSON.stringify(v2Catalog),
      {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      }
    );
  }
  if (
    targetUrl.hostname === 'raw.githubusercontent.com'
    && targetUrl.pathname.endsWith('/main/catalog.json')
  ) {
    return new Response(
      init.method === 'HEAD' ? null : JSON.stringify({ schemaVersion: 1 }),
      {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      }
    );
  }
  if (
    targetUrl.hostname === 'raw.githubusercontent.com'
    && targetUrl.pathname.endsWith('/main/qqmusic-profile-catalog.json')
  ) {
    return new Response(
      init.method === 'HEAD' ? null : JSON.stringify({ schemaVersion: 1 }),
      {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      }
    );
  }
  if (
    targetUrl.hostname === 'github.com'
    && targetUrl.pathname.includes('/releases/download/')
  ) {
    return new Response(init.method === 'HEAD' ? null : 'zipbytes', {
      status: 206,
      headers: {
        'Content-Type': 'application/zip',
        'Content-Length': '8',
        'Content-Range': 'bytes 0-7/8',
        ETag: 'v2-test'
      }
    });
  }
  throw new Error(`Unexpected upstream request in test: ${target}`);
};

try {
  const moduleUrl = `${pathToFileURL(temporaryWorkerPath).href}?test=1`;
  const worker = (await import(moduleUrl)).default;

  let response = await worker.fetch(
    new Request('https://app.enkianss.us/connectors/v2/catalog.json'),
    {}
  );
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), v2Catalog);
  assert.equal(
    new URL(calls.at(-1).target).pathname,
    '/Enkianssus/awoo-connectors/main/catalog-v2.json'
  );

  response = await worker.fetch(
    new Request('https://app.enkianss.us/connectors/v2/catalog.json', {
      method: 'HEAD'
    }),
    {}
  );
  assert.equal(response.status, 200);
  assert.equal(await response.text(), '');
  assert.equal(calls.at(-1).init.method, 'HEAD');

  const v2Asset =
    'awoo-connector-kugou-20.1.42.1-win-x86-framework-dependent.zip';
  response = await worker.fetch(
    new Request(
      `https://app.enkianss.us/connectors/v2/download/kugou/20.1.42.1/${v2Asset}`,
      { headers: { Range: 'bytes=0-7' } }
    ),
    {}
  );
  assert.equal(response.status, 206);
  assert.equal(response.headers.get('Content-Range'), 'bytes 0-7/8');
  assert.equal(
    response.headers.get('Content-Disposition'),
    `attachment; filename="${v2Asset}"`
  );
  const assetCall = calls.at(-1);
  assert.equal(
    new URL(assetCall.target).pathname,
    `/Enkianssus/awoo-connectors/releases/download/kugou-v20.1.42.1/${v2Asset}`
  );
  assert.equal(assetCall.init.headers.get('Range'), 'bytes=0-7');

  const callCountBeforeRejects = calls.length;
  response = await worker.fetch(
    new Request(
      'https://app.enkianss.us/connectors/v2/download/kugou/20.1.42.1/'
        + 'bilincm-connector-kugou-20.1.42.1-win-x86.zip'
    ),
    {}
  );
  assert.equal(response.status, 400);
  assert.equal(calls.length, callCountBeforeRejects);

  response = await worker.fetch(
    new Request(
      'https://app.enkianss.us/connectors/v2/download/netease/3.1.37.205354.9/'
        + 'awoo-connector-netease-3.1.37.205354.9-win-x86-framework-dependent.zip'
    ),
    {}
  );
  assert.equal(response.status, 400);
  assert.equal(calls.length, callCountBeforeRejects);

  response = await worker.fetch(
    new Request(
      `https://app.enkianss.us/connectors/v2/download/kugou/20.1.42.1/${v2Asset}`,
      { method: 'POST' }
    ),
    {}
  );
  assert.equal(response.status, 405);
  assert.equal(calls.length, callCountBeforeRejects);

  response = await worker.fetch(
    new Request('https://app.enkianss.us/connectors/v1/catalog.json'),
    {}
  );
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { schemaVersion: 1 });
  assert.equal(
    new URL(calls.at(-1).target).pathname,
    '/Enkianssus/awoo-connectors/main/catalog.json'
  );

  response = await worker.fetch(
    new Request(
      'https://app.enkianss.us/connectors/v1/profiles/qqmusic/catalog.json'
    ),
    {}
  );
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { schemaVersion: 1 });
  assert.equal(
    new URL(calls.at(-1).target).pathname,
    '/Enkianssus/awoo-connectors/main/qqmusic-profile-catalog.json'
  );

  console.log('cloudflare-worker-v2.test.mjs passed.');
} finally {
  globalThis.fetch = originalFetch;
  fs.rmSync(temporaryDirectory, { recursive: true, force: true });
}
