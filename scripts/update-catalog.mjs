import fs from 'node:fs';
import path from 'node:path';

const [
  connectorId,
  version,
  awooAssetName,
  awooSha256,
  awooSignature,
  awooSizeText,
  legacyAssetName,
  legacySha256,
  legacySignature,
  legacySizeText,
  runtime,
  awooFrameworkAssetName,
  awooFrameworkSha256,
  awooFrameworkSignature,
  awooFrameworkSizeText,
  legacyFrameworkAssetName,
  legacyFrameworkSha256,
  legacyFrameworkSignature,
  legacyFrameworkSizeText,
  runtimeChannel
] = process.argv.slice(2);

if (
  !connectorId || !version
  || !awooAssetName || !awooSha256 || !awooSignature || !awooSizeText
  || !legacyAssetName || !legacySha256 || !legacySignature || !legacySizeText
  || !runtime
  || !awooFrameworkAssetName || !awooFrameworkSha256
  || !awooFrameworkSignature || !awooFrameworkSizeText
  || !legacyFrameworkAssetName || !legacyFrameworkSha256
  || !legacyFrameworkSignature || !legacyFrameworkSizeText
  || !runtimeChannel
) {
  throw new Error(
    'Usage: update-catalog.mjs <id> <version> '
    + '<awoo-asset> <awoo-sha256> <awoo-signature> <awoo-size> '
    + '<legacy-asset> <legacy-sha256> <legacy-signature> <legacy-size> '
    + '<runtime> '
    + '<awoo-framework-asset> <awoo-framework-sha256> '
    + '<awoo-framework-signature> <awoo-framework-size> '
    + '<legacy-framework-asset> <legacy-framework-sha256> '
    + '<legacy-framework-signature> <legacy-framework-size> '
    + '<runtime-channel>'
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
    testedPlayerVersion: '22.22 / 22.41 / 22.51 / 22.52'
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
  asset: legacyAssetName,
  size: Number(legacySizeText),
  sha256: legacySha256,
  signature: legacySignature,
  publishedAt,
  downloadUrl:
    `https://app.enkianss.us/connectors/v1/download/${connectorId}/${version}/${legacyAssetName}`,
  awooPackage: {
    asset: awooAssetName,
    size: Number(awooSizeText),
    sha256: awooSha256,
    signature: awooSignature,
    downloadUrl:
      `https://app.enkianss.us/connectors/v1/download/${connectorId}/${version}/${awooAssetName}`
  },
  frameworkDependent: {
    runtime,
    runtimeChannel,
    asset: legacyFrameworkAssetName,
    size: Number(legacyFrameworkSizeText),
    sha256: legacyFrameworkSha256,
    signature: legacyFrameworkSignature,
    downloadUrl:
      `https://app.enkianss.us/connectors/v1/download/${connectorId}/${version}/${legacyFrameworkAssetName}`
  },
  awooFrameworkDependent: {
    runtime,
    runtimeChannel,
    asset: awooFrameworkAssetName,
    size: Number(awooFrameworkSizeText),
    sha256: awooFrameworkSha256,
    signature: awooFrameworkSignature,
    downloadUrl:
      `https://app.enkianss.us/connectors/v1/download/${connectorId}/${version}/${awooFrameworkAssetName}`
  }
};

fs.writeFileSync(
  catalogPath,
  `${JSON.stringify(catalog, null, 2)}\n`,
  'utf8'
);
