import fs from 'node:fs';
import path from 'node:path';

const [
  connectorId,
  version,
  assetName,
  sha256,
  signature,
  sizeText,
  runtime,
  frameworkAssetName,
  frameworkSha256,
  frameworkSignature,
  frameworkSizeText,
  runtimeChannel
] = process.argv.slice(2);

if (
  !connectorId || !version || !assetName || !sha256 || !signature || !runtime
  || !frameworkAssetName || !frameworkSha256 || !frameworkSignature
  || !frameworkSizeText || !runtimeChannel
) {
  throw new Error(
    'Usage: update-catalog.mjs <id> <version> <asset> <sha256> <signature> <size> <runtime> '
    + '<framework-asset> <framework-sha256> <framework-signature> <framework-size> <runtime-channel>'
  );
}

const supported = {
  netease: {
    name: '网易云音乐',
    playerVersionPolicy: '3.1.*',
    testedPlayerVersion: '3.1.37.205354'
  },
  kugou: {
    name: '酷狗音乐',
    playerVersionPolicy: '20.*',
    testedPlayerVersion: '20.0.81.27563'
  },
  qqmusic: {
    name: 'QQ音乐',
    playerVersionPolicy: '22.*',
    testedPlayerVersion: '22.22 / 22.41'
  },
  folia: {
    name: 'Folia',
    playerVersionPolicy: 'Stage API',
    testedPlayerVersion: 'Stage API'
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
  testedPlayerVersion: supported[connectorId].testedPlayerVersion,
  runtime,
  asset: assetName,
  size: Number(sizeText),
  sha256,
  signature,
  publishedAt,
  downloadUrl:
    `https://app.enkianss.us/connectors/v1/download/${connectorId}/${version}/${assetName}`,
  frameworkDependent: {
    runtime,
    runtimeChannel,
    asset: frameworkAssetName,
    size: Number(frameworkSizeText),
    sha256: frameworkSha256,
    signature: frameworkSignature,
    downloadUrl:
      `https://app.enkianss.us/connectors/v1/download/${connectorId}/${version}/${frameworkAssetName}`
  }
};

fs.writeFileSync(
  catalogPath,
  `${JSON.stringify(catalog, null, 2)}\n`,
  'utf8'
);
