import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryDirectory = path.resolve(testDirectory, '..');
const generatorPath = path.join(repositoryDirectory, 'scripts', 'update-catalog-v2.mjs');
const workflowPath = path.join(
  repositoryDirectory,
  '.github',
  'workflows',
  'release-connector.yml'
);

const v1Catalog = JSON.parse(
  fs.readFileSync(path.join(repositoryDirectory, 'catalog.json'), 'utf8')
);
assert.equal(v1Catalog.schemaVersion, 1, 'the legacy catalog must remain schema v1');
for (const [id, entry] of Object.entries(v1Catalog.connectors)) {
  assert.ok(entry.asset, `v1 ${id} must retain its legacy package`);
  assert.ok(entry.awooPackage, `v1 ${id} must retain its Awoo full package`);
  assert.ok(entry.frameworkDependent, `v1 ${id} must retain its legacy small package`);
  assert.ok(
    entry.awooFrameworkDependent,
    `v1 ${id} must retain its Awoo small package`
  );
}

const v2Catalog = JSON.parse(
  fs.readFileSync(path.join(repositoryDirectory, 'catalog-v2.json'), 'utf8')
);
assert.equal(v2Catalog.schemaVersion, 2);
assert.equal(v2Catalog.repository, 'Enkianssus/awoo-connectors');
assert.equal(v2Catalog.publicKeyId, 'bilincm-connectors-2026-01');
for (const [id, entry] of Object.entries(v2Catalog.connectors)) {
  assert.equal(entry.minimumCoreVersion, '1.1.10', `v2 ${id} core boundary`);
  assert.ok(entry.package, `v2 ${id} must have a package`);
  assert.equal(entry.package.deployment, 'framework-dependent');
  assert.match(
    entry.package.asset,
    new RegExp(`^awoo-connector-${id}-.+-framework-dependent\\.zip$`)
  );
  assert.match(entry.package.downloadUrl, /\/connectors\/v2\/download\//);
  for (const forbidden of [
    'asset',
    'runtime',
    'awooPackage',
    'frameworkDependent',
    'awooFrameworkDependent'
  ]) {
    assert.equal(
      Object.hasOwn(entry, forbidden),
      false,
      `v2 ${id} must not expose the v1 ${forbidden} field`
    );
  }
}

const workflow = fs.readFileSync(workflowPath, 'utf8');
assert.match(workflow, /scripts\/update-catalog-v2\.mjs/);
assert.match(workflow, /git add catalog-v2\.json/);
assert.doesNotMatch(workflow, /scripts\/update-catalog\.mjs/);
assert.doesNotMatch(workflow, /legacyAsset|legacy_asset|legacyFramework/);
assert.doesNotMatch(workflow, /--self-contained true/);
assert.match(workflow, /frameworkAsset\.sig/);
assert.match(workflow, /frameworkAsset\.sha256/);

const temporaryDirectory = fs.mkdtempSync(
  path.join(os.tmpdir(), 'awoo-connector-catalog-v2-')
);
try {
  fs.writeFileSync(
    path.join(temporaryDirectory, 'catalog-v2.json'),
    `${JSON.stringify({
      schemaVersion: 2,
      generatedAt: null,
      repository: 'Enkianssus/awoo-connectors',
      publicKeyId: 'bilincm-connectors-2026-01',
      connectors: {}
    }, null, 2)}\n`,
    'utf8'
  );

  const validAsset =
    'awoo-connector-kugou-20.1.42.1-win-x86-framework-dependent.zip';
  const validHash = 'a'.repeat(64);
  const validSignature = 'A'.repeat(43) + '=';
  const validRun = spawnSync(
    process.execPath,
    [
      generatorPath,
      'kugou',
      '20.1.42.1',
      validAsset,
      validHash,
      validSignature,
      '1234',
      'win-x86',
      '8.0'
    ],
    { cwd: temporaryDirectory, encoding: 'utf8' }
  );
  assert.equal(validRun.status, 0, validRun.stderr || validRun.stdout);
  const generated = JSON.parse(
    fs.readFileSync(path.join(temporaryDirectory, 'catalog-v2.json'), 'utf8')
  );
  assert.equal(generated.connectors.kugou.minimumCoreVersion, '1.1.10');
  assert.equal(Object.hasOwn(generated.connectors.kugou, 'runtime'), false);
  assert.deepEqual(generated.connectors.kugou.package, {
    deployment: 'framework-dependent',
    runtime: 'win-x86',
    runtimeChannel: '8.0',
    asset: validAsset,
    size: 1234,
    sha256: validHash,
    signature: validSignature,
    downloadUrl:
      'https://app.enkianss.us/connectors/v2/download/kugou/20.1.42.1/'
      + validAsset
  });
  assert.equal(Object.hasOwn(generated.connectors.kugou, 'awooPackage'), false);

  const invalidRun = spawnSync(
    process.execPath,
    [
      generatorPath,
      'kugou',
      '20.1.42.1',
      'bilincm-connector-kugou-20.1.42.1-win-x86.zip',
      validHash,
      validSignature,
      '1234',
      'win-x86',
      '8.0'
    ],
    { cwd: temporaryDirectory, encoding: 'utf8' }
  );
  assert.notEqual(invalidRun.status, 0, 'legacy assets must be rejected by v2');
  assert.match(
    `${invalidRun.stdout}\n${invalidRun.stderr}`,
    /framework-dependent asset name/
  );
} finally {
  fs.rmSync(temporaryDirectory, { recursive: true, force: true });
}

console.log('catalog-v2-policy.test.mjs passed.');
