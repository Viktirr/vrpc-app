#!/usr/bin/env node
// Canonicalizes (RFC 8785 restricted profile) and signs the VRPC update
// discovery payload offline with ECDSA P-256 / SHA-256.
//
// Modes:
//   Package mode:  --zip <path> --version 1.2.0 --channel stable --runtime win-x64
//                  --key <private.pem> --key-id <id> [--release-tag v1.2.0]
//                  [--notes-url <url>] [--asset-base <github-download-base>]
//                  [--published-at-utc <ts>] --out <envelope.json>
//   Payload mode:  --payload-file <payload.json> --key ... --key-id ... --out ...
//   Verify mode:   --verify <envelope.json> --public-key <spki.b64> [--canonical-out <file>]
//
// The payload object is the signature subject. The output envelope is
// { schema: 1, payload, signature: { algorithm, keyId, value } }.
// Nothing here ever touches the network.

import { createHash, createPrivateKey, createPublicKey, sign as cryptoSign, verify as cryptoVerify } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const ALGORITHM = 'ECDSA-P256-SHA256';
const SCHEMA = 1;
const SAFE_INTEGER = Number.MAX_SAFE_INTEGER;

function fail(message) {
  console.error(`error: ${message}`);
  process.exit(1);
}

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 2) {
    const name = argv[i];
    const value = argv[i + 1];
    if (!name?.startsWith('--') || value === undefined) { fail(`Invalid argument: ${name}`); }
    args[name.slice(2)] = value;
  }
  return args;
}

// RFC 8785 restricted profile: objects/arrays/strings/safe integers/bool/null.
export function jcs(value) {
  if (value === null) { return 'null'; }
  const type = typeof value;
  if (type === 'boolean') { return value ? 'true' : 'false'; }
  if (type === 'number') {
    if (!Number.isSafeInteger(value) || Math.abs(value) > SAFE_INTEGER) {
      fail(`JCS only supports safe integers, got ${value}`);
    }
    return Object.is(value, -0) ? '0' : String(value);
  }
  if (type === 'string') { return JSON.stringify(value); }
  if (Array.isArray(value)) { return `[${value.map(jcs).join(',')}]`; }
  if (type === 'object') {
    const keys = Object.keys(value).sort();
    return `{${keys.map((key) => `${JSON.stringify(key)}:${jcs(value[key])}`).join(',')}}`;
  }
  return fail(`JCS does not support a value of type ${type}`);
}

function sha256Hex(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

function signPayload(payload, privatePem, keyId) {
  const canonical = Buffer.from(jcs(payload), 'utf8');
  const key = createPrivateKey(privatePem);
  const signature = cryptoSign('sha256', canonical, { key, dsaEncoding: 'ieee-p1363' });
  const publicKey = createPublicKey(key);
  const valid = cryptoVerify('sha256', canonical, { key: publicKey, dsaEncoding: 'ieee-p1363' }, signature);
  if (!valid) { fail('Self-verification of the signature failed.'); }

  return {
    envelope: {
      schema: SCHEMA,
      payload,
      signature: {
        algorithm: ALGORITHM,
        keyId,
        value: signature.toString('base64')
      }
    },
    canonical,
    publicKeySpkiBase64: publicKey.export({ type: 'spki', format: 'der' }).toString('base64')
  };
}

function buildPayloadFromPackage(args) {
  const version = args.version;
  const channel = args.channel ?? 'stable';
  const runtime = args.runtime ?? 'win-x64';
  if (!/^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$/.test(version ?? '')) {
    fail('--version must be a strict numeric dotted version, e.g. 1.2.0');
  }
  if (channel !== 'stable') { fail('Only the stable channel is supported.'); }
  if (runtime !== 'win-x64') { fail('Only win-x64 is supported.'); }

  const zipPath = resolve(args.zip ?? fail('--zip is required in package mode'));
  const zip = readFileSync(zipPath);
  if (zip.length <= 0) { fail('The package is empty.'); }

  const name = `VRPC-${version}-win-x64.zip`;
  const releaseTag = args['release-tag'] ?? `v${version}`;
  if (releaseTag !== `v${version}`) { fail('--release-tag must be v<version> for the stable channel.'); }

  const assetBase = (args['asset-base'] ?? 'https://github.com/Viktirr/vrpc-app/releases/download').replace(/\/+$/, '');
  const notesUrl = args['notes-url'] ?? `https://github.com/Viktirr/vrpc-app/releases/tag/${releaseTag}`;
  if (!notesUrl.startsWith('https://')) { fail('--notes-url must be https.'); }

  const publishedAtUtc = args['published-at-utc'] ?? new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');

  return {
    product: 'VRPC',
    version,
    channel,
    runtime,
    releaseTag,
    asset: {
      name,
      url: `${assetBase}/${releaseTag}/${name}`,
      size: zip.length,
      sha256: sha256Hex(zip)
    },
    notesUrl,
    publishedAtUtc
  };
}

const args = parseArgs(process.argv.slice(2));

if (args.verify) {
  const publicKeyBase64 = args['public-key'] ?? fail('--public-key is required in verify mode');
  const envelope = JSON.parse(readFileSync(resolve(args.verify), 'utf8'));
  const canonical = Buffer.from(jcs(envelope.payload), 'utf8');
  if (args['canonical-out']) { writeFileSync(resolve(args['canonical-out']), canonical); }
  const key = createPublicKey({ key: Buffer.from(publicKeyBase64, 'base64'), format: 'der', type: 'spki' });
  const ok = cryptoVerify('sha256', canonical, { key, dsaEncoding: 'ieee-p1363' }, Buffer.from(envelope.signature.value, 'base64'));
  if (!ok) { fail('Signature verification failed.'); }
  console.log(`OK ${envelope.payload.product} ${envelope.payload.version} ${envelope.signature.keyId}`);
  console.log(`sha256=${sha256Hex(canonical)} size=${envelope.payload.asset.size}`);
  process.exit(0);
}

const keyPath = args.key ?? fail('--key is required');
const keyId = args['key-id'] ?? fail('--key-id is required');
const outPath = args.out ?? fail('--out is required');

const payload = args['payload-file']
  ? JSON.parse(readFileSync(resolve(args['payload-file']), 'utf8'))
  : buildPayloadFromPackage(args);

const { envelope, canonical, publicKeySpkiBase64 } = signPayload(payload, readFileSync(resolve(keyPath), 'utf8'), keyId);
writeFileSync(resolve(outPath), JSON.stringify(envelope, null, 2) + '\n', 'utf8');

console.log(`Signed ${envelope.payload.product} ${envelope.payload.version} (${envelope.signature.keyId})`);
console.log(`  asset: ${envelope.payload.asset.name} size=${envelope.payload.asset.size} sha256=${envelope.payload.asset.sha256}`);
console.log(`  canonical bytes: ${canonical.length}, sha256=${sha256Hex(canonical)}`);
console.log(`  public key (SPKI base64): ${publicKeySpkiBase64}`);
console.log(`  envelope: ${resolve(outPath)}`);
