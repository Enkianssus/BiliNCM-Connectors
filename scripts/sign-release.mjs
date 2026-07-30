import crypto from 'node:crypto';
import fs from 'node:fs';

const [assetPath] = process.argv.slice(2);
const privateKey = process.env.CONNECTOR_SIGNING_PRIVATE_KEY;

if (!assetPath || !privateKey) {
  throw new Error(
    'Asset path and CONNECTOR_SIGNING_PRIVATE_KEY are required.'
  );
}

const asset = fs.readFileSync(assetPath);
const signature = crypto.sign(null, asset, privateKey);
const sha256 = crypto.createHash('sha256').update(asset).digest('hex');

process.stdout.write(JSON.stringify({
  sha256,
  signature: signature.toString('base64'),
  size: asset.length
}));

