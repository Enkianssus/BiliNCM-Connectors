import fs from 'node:fs';
import path from 'node:path';

const [
  connectorId,
  version,
  frameworkAssetName,
  frameworkSha256,
  frameworkSignature,
  frameworkSizeText,
  runtime,
  runtimeChannel
] = process.argv.slice(2);

if (
  !connectorId
  || !version
  || !frameworkAssetName
  || !frameworkSha256
  || !frameworkSignature
  || !frameworkSizeText
  || !runtime
  || !runtimeChannel
) {
  throw new Error(
    'Usage: update-catalog-v2.mjs <id> <version> '
    + '<awoo-framework-asset> <sha256> <signature> <size> '
    + '<runtime> <runtime-channel>'
  );
}

const supported = {
  netease: {
    name: '网易云音乐',
    playerVersionPolicy: '3.1.*',
    testedPlayerVersion: '3.1.37.205354',
    runtime: 'win-x64',
    versionPattern: /^\d+\.\d+\.\d+\.\d+\.\d+$/
  },
  kugou: {
    name: '酷狗音乐',
    playerVersionPolicy: '20.*',
    testedPlayerVersion: '20.1.41.27870',
    runtime: 'win-x86',
    versionPattern: /^\d+\.\d+\.\d+\.\d+$/
  },
  qqmusic: {
    name: 'QQ音乐',
    playerVersionPolicy: '22.*',
    testedPlayerVersion: '22.22 / 22.41 / 22.51 / 22.52',
    runtime: 'win-x86',
    versionPattern: /^\d+\.\d+\.\d+$/
  },
  folia: {
    name: 'Folia',
    playerVersionPolicy: 'Stage API',
    testedPlayerVersion: 'Stage API',
    runtime: 'win-x86',
    versionPattern: /^\d+\.\d+\.\d+$/
  }
};

const metadata = supported[connectorId];
if (!metadata) {
  throw new Error(`Unsupported connector: ${connectorId}`);
}
if (!metadata.versionPattern.test(version)) {
  throw new Error(`Invalid ${connectorId} connector version: ${version}`);
}
if (runtime !== metadata.runtime) {
  throw new Error(
    `${connectorId} must publish for ${metadata.runtime}, received ${runtime}`
  );
}
if (runtimeChannel !== '8.0') {
  throw new Error(`Invalid private .NET runtime channel: ${runtimeChannel}`);
}

const expectedAssetName =
  `awoo-connector-${connectorId}-${version}-${runtime}-framework-dependent.zip`;
if (frameworkAssetName !== expectedAssetName) {
  throw new Error(
    `v2 releases must use the Awoo framework-dependent asset name: ${expectedAssetName}`
  );
}
if (!/^[0-9a-f]{64}$/i.test(frameworkSha256)) {
  throw new Error('Framework-dependent SHA-256 must be 64 hexadecimal characters.');
}
if (!/^[A-Za-z0-9+/]+={0,2}$/.test(frameworkSignature)) {
  throw new Error('Framework-dependent signature must be base64 text.');
}

const frameworkSize = Number(frameworkSizeText);
if (!Number.isSafeInteger(frameworkSize) || frameworkSize <= 0) {
  throw new Error(`Framework-dependent size must be a positive integer: ${frameworkSizeText}`);
}

const catalogPath = path.resolve('catalog-v2.json');
const catalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'));
if (catalog.schemaVersion !== 2) {
  throw new Error('catalog-v2.json must have schemaVersion 2.');
}
if (catalog.repository !== 'Enkianssus/awoo-connectors') {
  throw new Error('catalog-v2.json must identify the Enkianssus/awoo-connectors repository.');
}
if (catalog.publicKeyId !== 'bilincm-connectors-2026-01') {
  throw new Error('catalog-v2.json must retain the established publicKeyId.');
}
if (!catalog.connectors || typeof catalog.connectors !== 'object') {
  throw new Error('catalog-v2.json must contain a connectors object.');
}
for (const [id, entry] of Object.entries(catalog.connectors)) {
  if (entry.asset || entry.awooPackage || entry.frameworkDependent || entry.awooFrameworkDependent) {
    throw new Error(`v2 catalog entry ${id} contains a legacy package field.`);
  }
  if (!entry.package || entry.package.deployment !== 'framework-dependent') {
    throw new Error(`v2 catalog entry ${id} must contain only a framework-dependent package.`);
  }
}

const publishedAt = new Date().toISOString();
catalog.generatedAt = publishedAt;
catalog.connectors[connectorId] = {
  id: connectorId,
  name: metadata.name,
  channel: 'stable',
  version,
  protocolVersion: 1,
  minimumCoreVersion: '1.1.10',
  playerVersionPolicy: metadata.playerVersionPolicy,
  testedPlayerVersion: metadata.testedPlayerVersion,
  publishedAt,
  package: {
    deployment: 'framework-dependent',
    runtime,
    runtimeChannel,
    asset: frameworkAssetName,
    size: frameworkSize,
    sha256: frameworkSha256,
    signature: frameworkSignature,
    downloadUrl:
      `https://app.enkianss.us/connectors/v2/download/${connectorId}/${version}/${frameworkAssetName}`
  }
};

fs.writeFileSync(
  catalogPath,
  `${JSON.stringify(catalog, null, 2)}\n`,
  'utf8'
);
