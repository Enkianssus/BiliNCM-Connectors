import fs from 'node:fs';
import path from 'node:path';

const [
  connectorId,
  version,
  assetName,
  sha256,
  signature,
  sizeText,
  runtime
] = process.argv.slice(2);

if (!connectorId || !version || !assetName || !sha256 || !signature || !runtime) {
  throw new Error(
    'Usage: update-catalog.mjs <id> <version> <asset> <sha256> <signature> <size> <runtime>'
  );
}

const supported = {
  netease: {
    name: '网易云音乐',
    playerVersionPolicy: '3.1.*'
  },
  kugou: {
    name: '酷狗音乐',
    playerVersionPolicy: '20.*'
  },
  qqmusic: {
    name: 'QQ音乐',
    playerVersionPolicy: '22.*'
  },
  folia: {
    name: 'Folia',
    playerVersionPolicy: 'Stage API'
  }
};

if (!supported[connectorId]) {
  throw new Error(`Unsupported connector: ${connectorId}`);
}

const catalogPath = path.resolve('catalog.json');
const catalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'));
const publishedAt = new Date().toISOString();

catalog.generatedAt = publishedAt;
catalog.connectors[connectorId] = {
  id: connectorId,
  name: supported[connectorId].name,
  channel: 'stable',
  version,
  protocolVersion: 1,
  minimumCoreVersion: '1.1.0',
  playerVersionPolicy: supported[connectorId].playerVersionPolicy,
  runtime,
  asset: assetName,
  size: Number(sizeText),
  sha256,
  signature,
  publishedAt,
  downloadUrl:
    `https://app.enkianss.us/connectors/v1/download/${connectorId}/${version}/${assetName}`
};

fs.writeFileSync(
  catalogPath,
  `${JSON.stringify(catalog, null, 2)}\n`,
  'utf8'
);
