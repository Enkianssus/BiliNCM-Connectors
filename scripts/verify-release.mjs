import crypto from 'node:crypto';
import fs from 'node:fs';

const [connectorId = 'netease'] = process.argv.slice(2);
const catalogUrl =
  'https://app.enkianss.us/connectors/v1/catalog.json';

const catalogResponse = await fetch(catalogUrl, {
  headers: { Accept: 'application/json' }
});
if (!catalogResponse.ok) {
  throw new Error(`Catalog HTTP ${catalogResponse.status}`);
}

const catalog = await catalogResponse.json();
const entry = catalog.connectors?.[connectorId];
if (!entry) {
  throw new Error(`Connector is missing from catalog: ${connectorId}`);
}

const assetResponse = await fetch(entry.downloadUrl);
if (!assetResponse.ok) {
  throw new Error(`Asset HTTP ${assetResponse.status}`);
}
const asset = Buffer.from(await assetResponse.arrayBuffer());
const digest = crypto
  .createHash('sha256')
  .update(asset)
  .digest('hex');
const publicKey = fs.readFileSync(
  new URL('../keys/release-public-key.pem', import.meta.url),
  'utf8'
);
const signatureValid = crypto.verify(
  null,
  asset,
  publicKey,
  Buffer.from(entry.signature, 'base64')
);

if (
  asset.length !== entry.size
  || digest !== entry.sha256
  || !signatureValid
) {
  throw new Error(
    `Verification failed: size=${asset.length === entry.size}, `
    + `sha256=${digest === entry.sha256}, signature=${signatureValid}`
  );
}

process.stdout.write(JSON.stringify({
  connectorId,
  version: entry.version,
  bytes: asset.length,
  sha256: digest,
  signatureValid
}, null, 2));

