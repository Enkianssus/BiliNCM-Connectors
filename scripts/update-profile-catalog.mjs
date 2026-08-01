import fs from 'node:fs';
import path from 'node:path';

const [version, asset, sha256, signature, sizeText] = process.argv.slice(2);
if (!version || !asset || !sha256 || !signature || !sizeText) {
  throw new Error(
    'Usage: update-profile-catalog.mjs <version> <asset> <sha256> <signature> <size>'
  );
}

const catalogPath = path.resolve('qqmusic-profile-catalog.json');
const catalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'));
const publishedAt = new Date().toISOString();
catalog.generatedAt = publishedAt;
catalog.profiles.qqmusic = {
  id: 'qqmusic',
  version,
  schemaVersion: 1,
  minimumConnectorVersion: '1.4.0',
  asset,
  size: Number(sizeText),
  sha256,
  signature,
  publishedAt,
  downloadUrl:
    `https://app.enkianss.us/connectors/v1/profiles/qqmusic/download/${version}/${asset}`
};
fs.writeFileSync(
  catalogPath,
  `${JSON.stringify(catalog, null, 2)}\n`,
  'utf8'
);
