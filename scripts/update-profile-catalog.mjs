import fs from 'node:fs';
import path from 'node:path';

const [
  version,
  awooAsset,
  awooSha256,
  awooSignature,
  awooSizeText,
  legacyAsset,
  legacySha256,
  legacySignature,
  legacySizeText
] = process.argv.slice(2);
if (
  !version
  || !awooAsset || !awooSha256 || !awooSignature || !awooSizeText
  || !legacyAsset || !legacySha256 || !legacySignature || !legacySizeText
) {
  throw new Error(
    'Usage: update-profile-catalog.mjs <version> '
    + '<awoo-asset> <awoo-sha256> <awoo-signature> <awoo-size> '
    + '<legacy-asset> <legacy-sha256> <legacy-signature> <legacy-size>'
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
  minimumConnectorVersion: '22.51.1',
  asset: legacyAsset,
  size: Number(legacySizeText),
  sha256: legacySha256,
  signature: legacySignature,
  publishedAt,
  downloadUrl:
    `https://app.enkianss.us/connectors/v1/profiles/qqmusic/download/${version}/${legacyAsset}`,
  awooPackage: {
    asset: awooAsset,
    size: Number(awooSizeText),
    sha256: awooSha256,
    signature: awooSignature,
    downloadUrl:
      `https://app.enkianss.us/connectors/v1/profiles/qqmusic/download/${version}/${awooAsset}`
  }
};
fs.writeFileSync(
  catalogPath,
  `${JSON.stringify(catalog, null, 2)}\n`,
  'utf8'
);
