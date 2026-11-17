#!/usr/bin/env node
// Generates the ECDSA P-256 signing key pair for VRPC update manifests.
// The private key must live outside every source repo; never commit it.
//
// Usage:
//   node packaging/update-keygen.mjs --key-id vrpc-updates-2026-11
//   node packaging/update-keygen.mjs --key-id <id> --out <dir> [--force]
//
// Default output is ~/.vrpc-release-keys (outside every repo). Writing inside a
// git work tree requires --force.
//
// Writes <key-id>.private.pem (PKCS#8), <key-id>.public.spki.b64 and
// <key-id>.keygen.json. Prints the C# trusted-key entry for
// Modules/Updates/TrustedUpdateKeys.cs.

import { generateKeyPairSync } from 'node:crypto';
import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

function isInsideGitWorkTree(start) {
  let current = resolve(start);
  while (true) {
    if (existsSync(join(current, '.git'))) { return true; }
    const parent = dirname(current);
    if (parent === current) { return false; }
    current = parent;
  }
}

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 2) {
    const name = argv[i];
    const value = argv[i + 1];
    if (!name?.startsWith('--') || value === undefined) {
      throw new Error(`Invalid argument: ${name}`);
    }
    args[name.slice(2)] = value;
  }
  return args;
}

const rawArgs = process.argv.slice(2);
const force = rawArgs.includes('--force');
const args = parseArgs(rawArgs.filter((argument) => argument !== '--force'));
const keyId = args['key-id'];
const outDir = resolve(args.out ?? join(homedir(), '.vrpc-release-keys'));
if (!keyId || !/^[a-zA-Z0-9._-]{3,64}$/.test(keyId)) {
  throw new Error('A --key-id of 3-64 chars [a-zA-Z0-9._-] is required.');
}
if (!force && isInsideGitWorkTree(outDir)) {
  throw new Error(`Refusing to write signing keys inside a git work tree (${outDir}). Use the default ~/.vrpc-release-keys or pass --force deliberately.`);
}

mkdirSync(outDir, { recursive: true });
const privatePath = join(outDir, `${keyId}.private.pem`);
const publicPath = join(outDir, `${keyId}.public.spki.b64`);
if (existsSync(privatePath) || existsSync(publicPath)) {
  throw new Error(`Refusing to overwrite existing key files in ${outDir}. Delete them deliberately to rotate.`);
}

const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
const privatePem = privateKey.export({ type: 'pkcs8', format: 'pem' });
const publicBase64 = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');

writeFileSync(privatePath, privatePem, { encoding: 'utf8', mode: 0o600 });
writeFileSync(publicPath, publicBase64 + '\n', { encoding: 'utf8' });
writeFileSync(
  join(outDir, `${keyId}.keygen.json`),
  JSON.stringify({ keyId, algorithm: 'ECDSA-P256-SHA256', createdAtUtc: new Date().toISOString(), publicKeySpkiBase64: publicBase64 }, null, 2) + '\n',
  { encoding: 'utf8' }
);

console.log(`Created ${privatePath}`);
console.log(`Created ${publicPath}`);
console.log('');
console.log('C# entry for Modules/Updates/TrustedUpdateKeys.cs:');
console.log(`            ["${keyId}"] = "${publicBase64}",`);
console.log('');
console.log('Back up the private key offline (password manager / encrypted backup).');
console.log('Anyone holding it can sign updates that every VRPC client will trust.');
