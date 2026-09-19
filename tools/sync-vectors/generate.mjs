// S04 跨端测试向量工具：generate.mjs
//
// 用与桌面端 src/main/security/crypto-vault.ts 相同原语（hash-wasm argon2id +
// node:crypto AES-256-GCM/HKDF/SHA256），按固定输入生成保险库信封与文档密文，
// 再用 tsx 只读导入桌面端 unwrapVaultKeyWithPassword / unwrapVaultKeyWithRecovery /
// decryptSyncDocument 自检能解开（只读 E:\code\ssh-tool，一行不改）。
//
// 固定输入（写死，永不改；向量文件一旦提交禁止修改）：
//   syncPassword = "S04-desktop-sync-password"
//   vaultId = "s04-desktop-vault", keyVersion = 1, schemaVersion = 1
//   kdf = argon2id 65536/3/1（桌面端 DEFAULT_KDF_PARAMETERS）
//   kdfSalt = A0..AF（16B）, vaultKey = 40..5F（32B）, recoveryRaw = 60..7F（32B）
//   pwNonce = B0..BB, recNonce = C0..CB（各 12B）
//   docNonce empty/typical/secrets = D0..DB / E0..EB / F0..FB（各 12B）
//   明文 = 3 份 canonical SyncDocumentV1 JSON（见 buildDocuments）
//
// 输出（全部确定性字节，--check 逐字节比对）：
//   tests/fixtures/sync/desktop-document-{empty,typical,secrets}.json（明文 canonical，单行无 BOM）
//   tests/fixtures/sync/desktop-vectors.json（信封 + 3 份文档信封，2 空格缩进）
//   native/tests/desktop_vectors.h（同内容 C++ 头，native 用）
//
// 用法：
//   node tools/sync-vectors/generate.mjs            生成全部文件（含 tsx 自检）
//   node tools/sync-vectors/generate.mjs --check    只校验（不写文件；CI/verify 用）
//   node tools/sync-vectors/generate.mjs --skip-self-check  离线调试用（默认不开）

import { createCipheriv, createHash, hkdfSync } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { argon2id } from 'hash-wasm';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, '..', '..');
const DESKTOP_DIR = process.env.SSH_TOOL_DESKTOP_DIR || 'E:/code/ssh-tool';

// ---- 固定输入 ----
const SYNC_PASSWORD = 'S04-desktop-sync-password';
const VAULT_ID = 's04-desktop-vault';
const KEY_VERSION = 1;
const SCHEMA_VERSION = 1;
const KDF = { algorithm: 'argon2id', memory: 65536, iterations: 3, parallelism: 1 };
const UPDATED_AT = '2026-09-17T08:00:00.000Z';

const bytes = (from, len) => Buffer.from(Array.from({ length: len }, (_, i) => (from + i) & 0xff));
const KDF_SALT = bytes(0xa0, 16);
const VAULT_KEY = bytes(0x40, 32);
const RECOVERY_RAW = bytes(0x60, 32);
const PW_NONCE = bytes(0xb0, 12);
const REC_NONCE = bytes(0xc0, 12);
const DOC_NONCES = {
  empty: bytes(0xd0, 12),
  typical: bytes(0xe0, 12),
  secrets: bytes(0xf0, 12),
};

const AES_KEY_BYTES = 32;
const DOCUMENT_DOMAIN = 'ssh-port-mapper/sync-document/v1';
const PASSWORD_WRAP_DOMAIN = 'ssh-port-mapper/vault-key/password/v1';
const RECOVERY_WRAP_DOMAIN = 'ssh-port-mapper/vault-key/recovery/v1';
const RECOVERY_DERIVE_DOMAIN = 'ssh-port-mapper/recovery-kek/v1';
const RECOVERY_PREFIX = 'SPM1';

// ---- 与 crypto-vault.ts 逐行对齐的原语 ----
function b64(buf) {
  return Buffer.from(buf).toString('base64');
}

function aad(domain, fields) {
  const encoded = fields
    .map((f) => {
      const v = String(f);
      return `${Buffer.byteLength(v, 'utf8')}:${v}`;
    })
    .join('|');
  return Buffer.from(`${domain}|${encoded}`, 'utf8');
}

const documentAad = (vaultId, schemaVersion, keyVersion) =>
  aad(DOCUMENT_DOMAIN, [vaultId, schemaVersion, keyVersion]);
const passwordWrapAad = (keyVersion) => aad(PASSWORD_WRAP_DOMAIN, [keyVersion]);
const recoveryWrapAad = (keyVersion) => aad(RECOVERY_WRAP_DOMAIN, [keyVersion]);

function encryptAesGcm(plaintext, key, associatedData, nonce) {
  const cipher = createCipheriv('aes-256-gcm', Buffer.from(key), Buffer.from(nonce));
  cipher.setAAD(Buffer.from(associatedData));
  const ct = Buffer.concat([cipher.update(Buffer.from(plaintext)), cipher.final()]);
  return { nonce: Buffer.from(nonce), ciphertextWithTag: Buffer.concat([ct, cipher.getAuthTag()]) };
}

function recoveryChecksum(raw) {
  return createHash('sha256').update(RECOVERY_PREFIX).update(Buffer.from(raw)).digest('hex').slice(0, 12).toUpperCase();
}

function encodeRecoveryKey(raw) {
  const b64url = Buffer.from(raw).toString('base64url');
  return `${RECOVERY_PREFIX}-${b64url}-${recoveryChecksum(raw)}`;
}

// ---- canonical SyncDocumentV1 序列化（与 Core SyncDocumentWriter 逐字节一致） ----
// 键序按 03-SYNC-PROTOCOL.md §4.1 书写顺序；数组按 id 序数排序；无缩进；UTF-8。
function js(s) {
  return JSON.stringify(s);
}

function canonicalDoc(doc) {
  const servers = [...doc.servers].sort((a, b) => (a.profile.id < b.profile.id ? -1 : a.profile.id > b.profile.id ? 1 : 0));
  const tunnels = [...doc.tunnels].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  const groups = [...doc.groups].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  let out = `{"schemaVersion":${doc.schemaVersion},"updatedAt":${js(doc.updatedAt)},"preferences":`;
  out += `{"syncPasswords":${doc.preferences.syncPasswords ? 'true' : 'false'},"syncPrivateKeys":${doc.preferences.syncPrivateKeys ? 'true' : 'false'}}`;
  out += `,"servers":[${servers.map(canonicalServer).join(',')}]`;
  out += `,"tunnels":[${tunnels.map(canonicalTunnel).join(',')}]`;
  out += `,"groups":[${groups.map(canonicalGroup).join(',')}]}`;
  return out;
}

function canonicalServer(s) {
  const p = s.profile;
  let out = `{"profile":{"id":${js(p.id)},"name":${js(p.name)},"host":${js(p.host)},"port":${p.port},"username":${js(p.username)},"authType":${js(p.authType)},"hostFingerprint":${js(p.hostFingerprint)},"keepalive":${p.keepalive}}`;
  if (s.secrets && Object.keys(s.secrets).length > 0) {
    out += `,"secrets":{`;
    const parts = [];
    // §4.2 固定顺序：password, passphrase, privateKey, privateKeyEncoding, privateKeyFormat, privateKeyFingerprint
    for (const k of ['password', 'passphrase', 'privateKey', 'privateKeyEncoding', 'privateKeyFormat', 'privateKeyFingerprint']) {
      if (s.secrets[k] !== undefined) parts.push(`${js(k)}:${js(s.secrets[k])}`);
    }
    out += parts.join(',') + '}';
  }
  return out + '}';
}

function canonicalTunnel(t) {
  return `{"id":${js(t.id)},"name":${js(t.name)},"serverId":${js(t.serverId)},"groupId":${t.groupId === null ? 'null' : js(t.groupId)},"type":${js(t.type)},"listenHost":${js(t.listenHost)},"listenPort":${t.listenPort},"destHost":${js(t.destHost)},"destPort":${t.destPort},"destServerId":${js(t.destServerId)},"autoReconnect":${t.autoReconnect ? 'true' : 'false'},"enabled":${t.enabled ? 'true' : 'false'}}`;
}

function canonicalGroup(g) {
  return `{"id":${js(g.id)},"name":${js(g.name)},"color":${js(g.color)}}`;
}

function buildDocuments() {
  const empty = {
    schemaVersion: 1,
    updatedAt: UPDATED_AT,
    preferences: { syncPasswords: false, syncPrivateKeys: false },
    servers: [],
    tunnels: [],
    groups: [],
  };
  const typical = {
    schemaVersion: 1,
    updatedAt: UPDATED_AT,
    preferences: { syncPasswords: false, syncPrivateKeys: false },
    servers: [
      {
        profile: {
          id: 'srv-01', name: 'jump', host: 'jump.example.com', port: 22,
          username: 'jump', authType: 'agent', hostFingerprint: '', keepalive: 30,
        },
      },
      {
        profile: {
          id: 'srv-02', name: 'prod', host: 'prod.internal', port: 2222,
          username: 'deploy', authType: 'password', hostFingerprint: 'SHA256:s04-desktop-fingerprint', keepalive: 60,
        },
      },
    ],
    tunnels: [
      {
        id: 'tun-01', name: 'web', serverId: 'srv-01', groupId: 'grp-01', type: 'local',
        listenHost: '127.0.0.1', listenPort: 8080, destHost: '10.0.0.5', destPort: 80,
        destServerId: '', autoReconnect: true, enabled: true,
      },
      {
        id: 'tun-02', name: 'relay-prod', serverId: 'srv-02', groupId: 'grp-02', type: 'relay',
        listenHost: '0.0.0.0', listenPort: 7000, destHost: '', destPort: 0,
        destServerId: 'srv-01', autoReconnect: false, enabled: true,
      },
    ],
    groups: [
      { id: 'grp-01', name: 'default', color: '#4F8CFF' },
      { id: 'grp-02', name: 'prod', color: '#16A34A' },
    ],
  };
  const secrets = {
    schemaVersion: 1,
    updatedAt: UPDATED_AT,
    preferences: { syncPasswords: true, syncPrivateKeys: false },
    servers: [
      {
        profile: {
          id: 'srv-secret-01', name: 'secret-host', host: 'secret.example.com', port: 22,
          username: 'admin', authType: 'password', hostFingerprint: '', keepalive: 30,
        },
        secrets: { password: 'S04-Test-Password-123', passphrase: 'S04-Test-Passphrase-456' },
      },
    ],
    tunnels: [],
    groups: [],
  };
  return { empty, typical, secrets };
}

// ---- 主生成 ----
async function generateAll() {
  assertHashWasmVersion();

  const recoveryKey = encodeRecoveryKey(RECOVERY_RAW);
  const passwordKey = Buffer.from(
    await argon2id({
      password: SYNC_PASSWORD,
      salt: KDF_SALT,
      iterations: KDF.iterations,
      parallelism: KDF.parallelism,
      memorySize: KDF.memory,
      hashLength: AES_KEY_BYTES,
      outputType: 'binary',
    }),
  );
  const recoveryKek = Buffer.from(hkdfSync('sha256', Buffer.from(RECOVERY_RAW), Buffer.alloc(0), Buffer.from(RECOVERY_DERIVE_DOMAIN, 'utf8'), AES_KEY_BYTES));

  const pwEnc = encryptAesGcm(VAULT_KEY, passwordKey, passwordWrapAad(KEY_VERSION), PW_NONCE);
  const recEnc = encryptAesGcm(VAULT_KEY, recoveryKek, recoveryWrapAad(KEY_VERSION), REC_NONCE);
  passwordKey.fill(0);
  recoveryKek.fill(0);

  const envelope = {
    keyVersion: KEY_VERSION,
    passwordWrappedKey: b64(pwEnc.ciphertextWithTag),
    passwordWrapNonce: b64(pwEnc.nonce),
    recoveryWrappedKey: b64(recEnc.ciphertextWithTag),
    recoveryWrapNonce: b64(recEnc.nonce),
    kdfSalt: b64(KDF_SALT),
    kdfParameters: { ...KDF },
  };

  const docs = buildDocuments();
  const documents = {};
  for (const name of ['empty', 'typical', 'secrets']) {
    const plaintext = canonicalDoc(docs[name]);
    const ptBytes = Buffer.from(plaintext, 'utf8');
    const enc = encryptAesGcm(ptBytes, VAULT_KEY, documentAad(VAULT_ID, SCHEMA_VERSION, KEY_VERSION), DOC_NONCES[name]);
    documents[name] = {
      plaintext,
      plaintextSha256: createHash('sha256').update(ptBytes).digest('hex'),
      nonce: b64(enc.nonce),
      nonceHex: Buffer.from(enc.nonce).toString('hex'),
      ciphertext: b64(enc.ciphertextWithTag),
      ciphertextHash: createHash('sha256').update(enc.ciphertextWithTag).digest('hex'),
      envelope: {
        schemaVersion: SCHEMA_VERSION,
        keyVersion: KEY_VERSION,
        algorithm: 'AES-256-GCM',
        nonce: b64(enc.nonce),
        ciphertext: b64(enc.ciphertextWithTag),
        ciphertextHash: createHash('sha256').update(enc.ciphertextWithTag).digest('hex'),
      },
    };
  }

  const vectors = {
    generator: 'tools/sync-vectors/generate.mjs (S04)',
    hashWasm: ownDependencyVersion('hash-wasm'),
    desktopLockHashWasm: desktopLockVersion('hash-wasm'),
    syncPassword: SYNC_PASSWORD,
    vaultId: VAULT_ID,
    schemaVersion: SCHEMA_VERSION,
    keyVersion: KEY_VERSION,
    kdfParameters: { ...KDF },
    kdfSalt: b64(KDF_SALT),
    kdfSaltHex: Buffer.from(KDF_SALT).toString('hex'),
    vaultKeyHex: Buffer.from(VAULT_KEY).toString('hex'),
    vaultKeyB64: b64(VAULT_KEY),
    recoveryRawHex: Buffer.from(RECOVERY_RAW).toString('hex'),
    recoveryKey,
    passwordWrapNonce: b64(PW_NONCE),
    passwordWrapNonceHex: Buffer.from(PW_NONCE).toString('hex'),
    recoveryWrapNonce: b64(REC_NONCE),
    recoveryWrapNonceHex: Buffer.from(REC_NONCE).toString('hex'),
    envelope,
    documents: {
      empty: stripPlaintext(documents.empty),
      typical: stripPlaintext(documents.typical),
      secrets: stripPlaintext(documents.secrets),
    },
  };
  // 明文只存在文档夹具里；vectors 额外带 sha256 供交叉核对（不重复存明文，避免两处不一致）。
  // 但 native/C# 测试需要明文解密比对：明文 = 对应 desktop-document-*.json 文件内容。
  return { vectors, plaintexts: { empty: documents.empty.plaintext, typical: documents.typical.plaintext, secrets: documents.secrets.plaintext }, fullDocs: documents };
}

function stripPlaintext(d) {
  return { plaintextSha256: d.plaintextSha256, nonce: d.nonce, nonceHex: d.nonceHex, ciphertext: d.ciphertext, ciphertextHash: d.ciphertextHash, envelope: d.envelope };
}

function ownDependencyVersion(name) {
  const pkg = JSON.parse(readFileSync(join(scriptDir, 'package.json'), 'utf8'));
  return pkg.dependencies[name];
}

function desktopLockVersion(name) {
  const lock = JSON.parse(readFileSync(join(DESKTOP_DIR, 'package-lock.json'), 'utf8'));
  return lock.packages[`node_modules/${name}`]?.version;
}

function assertHashWasmVersion() {
  const own = ownDependencyVersion('hash-wasm');
  const lock = desktopLockVersion('hash-wasm');
  if (!lock) throw new Error(`桌面端 package-lock.json 中找不到 node_modules/hash-wasm（DESKTOP_DIR=${DESKTOP_DIR}）`);
  if (own !== lock) throw new Error(`hash-wasm 版本不一致：本工具 ${own} vs 桌面端 lock ${lock}`);
}

// ---- 输出文件内容 ----
function buildHeader(vectors, plaintexts) {
  const hexArr = (buf, name, perLine = 8) => {
    const bytes = [...buf].map((b) => `0x${b.toString(16).padStart(2, '0')}`);
    const lines = [];
    for (let i = 0; i < bytes.length; i += perLine) lines.push('    ' + bytes.slice(i, i + perLine).join(', '));
    return `inline constexpr std::uint8_t ${name}[${bytes.length}] = {\n${lines.join(',\n')}\n};`;
  };
  const s = (v) => JSON.stringify(v);
  const lines = [];
  lines.push('// S04 桌面端互通向量（由 tools/sync-vectors/generate.mjs 生成，禁止手改）。');
  lines.push('// 固定输入见 generate.mjs 文件头；与 tests/fixtures/sync/desktop-vectors.json 同内容。');
  lines.push('// 用法见 native/tests/desktop_vectors_test.cpp。文件一旦提交禁止修改。');
  lines.push('#pragma once');
  lines.push('');
  lines.push('#include <cstddef>');
  lines.push('#include <cstdint>');
  lines.push('');
  lines.push('namespace sshclient {');
  lines.push('namespace crypto {');
  lines.push('namespace desktop {');
  lines.push('');
  lines.push(`inline constexpr char kHashWasmVersion[] = ${s(vectors.hashWasm)};`);
  lines.push(`inline constexpr char kSyncPassword[] = ${s(vectors.syncPassword)};`);
  lines.push(`inline constexpr char kVaultId[] = ${s(vectors.vaultId)};`);
  lines.push(`inline constexpr int kSchemaVersion = ${vectors.schemaVersion};`);
  lines.push(`inline constexpr int kKeyVersion = ${vectors.keyVersion};`);
  lines.push(`inline constexpr char kKdfAlgorithm[] = ${s(vectors.kdfParameters.algorithm)};`);
  lines.push(`inline constexpr std::uint32_t kKdfMemoryKib = ${vectors.kdfParameters.memory};`);
  lines.push(`inline constexpr std::uint32_t kKdfIterations = ${vectors.kdfParameters.iterations};`);
  lines.push(`inline constexpr std::uint32_t kKdfParallelism = ${vectors.kdfParameters.parallelism};`);
  lines.push('');
  lines.push(hexArr(KDF_SALT, 'kKdfSalt'));
  lines.push(hexArr(PW_NONCE, 'kPasswordWrapNonce'));
  lines.push(hexArr(REC_NONCE, 'kRecoveryWrapNonce'));
  lines.push(hexArr(VAULT_KEY, 'kVaultKey'));
  lines.push(hexArr(RECOVERY_RAW, 'kRecoveryRaw'));
  lines.push(hexArr(DOC_NONCES.empty, 'kDocEmptyNonce'));
  lines.push(hexArr(DOC_NONCES.typical, 'kDocTypicalNonce'));
  lines.push(hexArr(DOC_NONCES.secrets, 'kDocSecretsNonce'));
  lines.push('');
  lines.push(`inline constexpr char kKdfSaltB64[] = ${s(vectors.kdfSalt)};`);
  lines.push(`inline constexpr char kVaultKeyHex[] = ${s(vectors.vaultKeyHex)};`);
  lines.push(`inline constexpr char kRecoveryRawHex[] = ${s(vectors.recoveryRawHex)};`);
  lines.push(`inline constexpr char kRecoveryKey[] = ${s(vectors.recoveryKey)};`);
  lines.push(`inline constexpr char kPasswordWrapNonceB64[] = ${s(vectors.passwordWrapNonce)};`);
  lines.push(`inline constexpr char kRecoveryWrapNonceB64[] = ${s(vectors.recoveryWrapNonce)};`);
  lines.push(`inline constexpr char kPasswordWrappedKeyB64[] = ${s(vectors.envelope.passwordWrappedKey)};`);
  lines.push(`inline constexpr char kRecoveryWrappedKeyB64[] = ${s(vectors.envelope.recoveryWrappedKey)};`);
  lines.push('');
  for (const name of ['empty', 'typical', 'secrets']) {
    const cap = name[0].toUpperCase() + name.slice(1);
    const d = vectors.documents[name];
    lines.push(`inline constexpr char kDoc${cap}NonceB64[] = ${s(d.nonce)};`);
    lines.push(`inline constexpr char kDoc${cap}CiphertextB64[] = ${s(d.ciphertext)};`);
    lines.push(`inline constexpr char kDoc${cap}CiphertextHash[] = ${s(d.ciphertextHash)};`);
    lines.push(`inline constexpr char kDoc${cap}PlaintextSha256[] = ${s(d.plaintextSha256)};`);
    lines.push(`inline constexpr char kDoc${cap}Plaintext[] = R"S04VEC(${plaintexts[name]})S04VEC";`);
    lines.push('');
  }
  lines.push('} // namespace desktop');
  lines.push('} // namespace crypto');
  lines.push('} // namespace sshclient');
  lines.push('');
  return lines.join('\n');
}

// ---- tsx 自检（只读导入桌面端，桌面仓库一行不改） ----
import { unlinkSync as unlinkSyncFn } from 'node:fs';

function findTsx() {
  // 直接用 node 跑 tsx 的 cli 入口，避免经 .cmd 需要 shell:true（DEP0190 告警）。
  const cli = join(scriptDir, 'node_modules', 'tsx', 'dist', 'cli.mjs');
  if (existsSync(cli)) return { node: true, path: cli };
  const local = join(scriptDir, 'node_modules', '.bin', process.platform === 'win32' ? 'tsx.cmd' : 'tsx');
  if (existsSync(local)) return { node: false, path: local };
  return null;
}

function runDesktopSelfCheckSync(vectorsPath) {
  const helper = join(tmpdir(), `s04-selfcheck-${Date.now()}-${Math.floor(Math.random() * 1e6)}.mts`);
  const cryptoUrl = pathToFileURL(join(DESKTOP_DIR, 'src/main/security/crypto-vault.ts')).href;
  writeFileSync(
    helper,
    `import { readFileSync } from 'node:fs';\n` +
      `import { decryptSyncDocument, unwrapVaultKeyWithPassword, unwrapVaultKeyWithRecovery } from ${JSON.stringify(cryptoUrl)};\n` +
      `const vectors = JSON.parse(readFileSync(process.argv[2], 'utf8'));\n` +
      `const docs = { empty: readFileSync(process.argv[3], 'utf8'), typical: readFileSync(process.argv[4], 'utf8'), secrets: readFileSync(process.argv[5], 'utf8') };\n` +
      `const k1 = await unwrapVaultKeyWithPassword(vectors.envelope, vectors.syncPassword);\n` +
      `if (k1.toString('hex') !== vectors.vaultKeyHex) { console.error('password unwrap mismatch'); process.exit(1); }\n` +
      `const k2 = unwrapVaultKeyWithRecovery(vectors.envelope, vectors.recoveryKey);\n` +
      `if (k2.toString('hex') !== vectors.vaultKeyHex) { console.error('recovery unwrap mismatch'); process.exit(1); }\n` +
      `for (const name of ['empty', 'typical', 'secrets']) {\n` +
      `  const pt = decryptSyncDocument(vectors.documents[name].envelope, k1, vectors.vaultId);\n` +
      `  if (pt.toString('utf8') !== docs[name]) { console.error(name + ' decrypt mismatch'); process.exit(1); }\n` +
      `}\n` +
      `console.log('desktop self-check OK: password+recovery unwrap, 3 docs decrypt');\n`,
  );
  const tsx = findTsx();
  try {
    if (tsx && tsx.node) {
      execFileSync(process.execPath, [tsx.path, helper, vectorsPath, join(repoRoot, 'tests/fixtures/sync/desktop-document-empty.json'), join(repoRoot, 'tests/fixtures/sync/desktop-document-typical.json'), join(repoRoot, 'tests/fixtures/sync/desktop-document-secrets.json')], { stdio: 'inherit' });
    } else if (tsx) {
      execFileSync(tsx.path, [helper, vectorsPath, join(repoRoot, 'tests/fixtures/sync/desktop-document-empty.json'), join(repoRoot, 'tests/fixtures/sync/desktop-document-typical.json'), join(repoRoot, 'tests/fixtures/sync/desktop-document-secrets.json')], { stdio: 'inherit', shell: process.platform === 'win32' });
    } else {
      execFileSync('npx', ['--yes', `tsx@${ownDependencyVersion('tsx')}`, helper, vectorsPath, join(repoRoot, 'tests/fixtures/sync/desktop-document-empty.json'), join(repoRoot, 'tests/fixtures/sync/desktop-document-typical.json'), join(repoRoot, 'tests/fixtures/sync/desktop-document-secrets.json')], { stdio: 'inherit', shell });
    }
  } finally {
    try {
      unlinkSyncFn(helper);
    } catch {}
  }
}

// ---- CLI ----
const args = process.argv.slice(2);
const isCheck = args.includes('--check');
const skipSelfCheck = args.includes('--skip-self-check');

const vectorsPath = join(repoRoot, 'tests', 'fixtures', 'sync', 'desktop-vectors.json');
const headerPath = join(repoRoot, 'native', 'tests', 'desktop_vectors.h');
const docPaths = {
  empty: join(repoRoot, 'tests', 'fixtures', 'sync', 'desktop-document-empty.json'),
  typical: join(repoRoot, 'tests', 'fixtures', 'sync', 'desktop-document-typical.json'),
  secrets: join(repoRoot, 'tests', 'fixtures', 'sync', 'desktop-document-secrets.json'),
};

const { vectors, plaintexts } = await generateAll();
const vectorsText = JSON.stringify(vectors, null, 2) + '\n';
const headerText = buildHeader(vectors, plaintexts);

if (isCheck) {
  let failed = 0;
  const expect = (p, text) => {
    if (!existsSync(p)) {
      console.error(`缺失: ${p}`);
      failed++;
      return;
    }
    const cur = readFileSync(p);
    if (!cur.equals(Buffer.from(text, 'utf8'))) {
      console.error(`不一致: ${p}（请运行 node tools/sync-vectors/generate.mjs 重新生成）`);
      failed++;
    } else {
      console.log(`OK: ${p}`);
    }
  };
  expect(vectorsPath, vectorsText);
  expect(headerPath, headerText);
  for (const k of Object.keys(docPaths)) expect(docPaths[k], plaintexts[k]);
  if (failed > 0) process.exit(1);
  console.log('vectors --check: 文件逐字节一致');
} else {
  mkdirSync(dirname(vectorsPath), { recursive: true });
  mkdirSync(dirname(headerPath), { recursive: true });
  writeFileSync(vectorsPath, vectorsText);
  writeFileSync(headerPath, headerText);
  for (const k of Object.keys(docPaths)) writeFileSync(docPaths[k], plaintexts[k]);
  console.log('已生成:');
  console.log(`  ${vectorsPath}`);
  console.log(`  ${headerPath}`);
  for (const k of Object.keys(docPaths)) console.log(`  ${docPaths[k]}`);
  const h = createHash('sha256').update(Buffer.from(vectors.documents.empty.ciphertext, 'base64')).digest('hex');
  console.log(`empty ciphertextHash=${vectors.documents.empty.ciphertextHash} (sha256 重算=${h})`);
}

if (!skipSelfCheck) {
  runDesktopSelfCheckSync(isCheck ? vectorsPath : vectorsPath);
}
