import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const [privatePath, publicPath] = process.argv.slice(2);
if (!privatePath || !publicPath) {
  throw new Error(
    'Usage: generate-signing-key.mjs <private-path> <public-path>'
  );
}
if (fs.existsSync(privatePath)) {
  throw new Error(`Refusing to overwrite existing key: ${privatePath}`);
}

const { privateKey, publicKey } = crypto.generateKeyPairSync('ed25519');
fs.mkdirSync(path.dirname(privatePath), { recursive: true });
fs.mkdirSync(path.dirname(publicPath), { recursive: true });
fs.writeFileSync(
  privatePath,
  privateKey.export({ format: 'pem', type: 'pkcs8' }),
  { encoding: 'utf8', mode: 0o600 }
);
fs.writeFileSync(
  publicPath,
  publicKey.export({ format: 'pem', type: 'spki' }),
  'utf8'
);

