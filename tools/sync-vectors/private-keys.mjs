// S15 私钥指纹向量工具：private-keys.mjs
//
// 对 native/tests/fixtures/keys 下每个测试专用私钥，用桌面端
// E:/code/ssh-tool 的 node_modules/ssh2 的 utils.parseKey(...).getPublicSSH()
// 计算公钥 wire blob，再 SHA256 → "SHA256:<base64 无填充>"（与桌面端
// sync-serializer.ts 的 publicKeyFingerprint 逐行对齐），输出期望指纹。
// 桌面仓库只读，一行不改。
//
// 固定输入（写死；夹具与短语与 K01/N05 一致）：
//   夹具目录 native/tests/fixtures/keys（11 个私钥 + 11 个 .pub，不含 .pub 输出）；
//   文件名以 _enc 结尾的 5 个加密夹具使用测试短语 "n05-test-passphrase" 解析
//   （与 native/tests/keytool_test.cpp 的 kFixturePassphrase、N05 一致；
//   测试专用密钥，不含真实秘密）；其余 6 个未加密夹具无短语解析。
//
// 已知互通限制（实测 ssh2@1.17，见本仓库 README）：
//   ssh2 parseKey 不支持 PKCS#8（"-----BEGIN PRIVATE KEY-----" 与
//   "-----BEGIN ENCRYPTED PRIVATE KEY-----" 报 Unsupported key format）。
//   这两个夹具（rsa3072_pkcs8、rsa3072_pkcs8_enc）的期望指纹改取自同目录
//   ssh-keygen 生成的 .pub 文件的 wire blob（getPublicSSH 与 .pub 是同一 blob，
//   已对 ed25519/rsa-pem/ecdsa-pem 抽查验证逐字节一致；且与 K01 的 ssh-keygen -lf
//   期望值一致）。JSON 中记 fingerprintSource 以示区别（ssh2 / ssh-keygen-pub）。
//   —— 因此 Lumia 端出站跳过 PKCS#8（见 Core PrivateKeySyncCodec），否则桌面端
//   整份文档无法解析；本地登录不受影响（K01/N05 支持 PKCS#8）。
//
// 输出（确定性字节，--check 逐字节比对，提交后冻结；只追加，不动 S04 向量文件）：
//   tests/fixtures/sync/private-key-vectors.json（2 空格缩进；C# 测试读）
//   native/tests/private_key_vectors.h（C++ 头；keytool_test 互通测试读）
//
// 用法：
//   node tools/sync-vectors/private-keys.mjs            生成两个文件
//   node tools/sync-vectors/private-keys.mjs --check    只校验（不写文件；verify 用）

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, '..', '..');
const DESKTOP_DIR = process.env.SSH_TOOL_DESKTOP_DIR || 'E:/code/ssh-tool';

// 与 K01/N05 一致的测试短语（测试专用；向量 JSON 只记录 encrypted 布尔值，不记录短语本身）。
const FIXTURE_PASSPHRASE = 'n05-test-passphrase';
const FIXTURE_DIR = join(repoRoot, 'native', 'tests', 'fixtures', 'keys');

const sshRequire = createRequire(import.meta.url);
const { utils } = sshRequire(join(DESKTOP_DIR, 'node_modules', 'ssh2'));
const desktopSsh2Version = sshRequire(join(DESKTOP_DIR, 'package.json')).dependencies.ssh2;

// 与桌面端 sync-private-key.ts privateKeyFormatOf 同正则（Lumia 端 PrivateKeyFormat.Detect 已移植）。
function headerFormat(text) {
  if (/^-----BEGIN OPENSSH PRIVATE KEY-----\r?\n/.test(text)) return 'openssh';
  if (/^-----BEGIN (?:RSA |EC |DSA |ENCRYPTED )?PRIVATE KEY-----\r?\n/.test(text)) return 'pem';
  return null;
}

function fingerprintOfBlob(blob) {
  return 'SHA256:' + createHash('sha256').update(blob).digest('base64').replace(/=+$/, '');
}

function buildEntries() {
  const files = readdirSync(FIXTURE_DIR).filter((f) => !f.endsWith('.pub')).sort();
  if (files.length === 0) throw new Error('夹具目录为空：' + FIXTURE_DIR);
  return files.map((file) => {
    const raw = readFileSync(join(FIXTURE_DIR, file));
    const text = raw.toString('utf8');
    const format = headerFormat(text);
    if (!format) throw new Error(file + '：header 无法判定格式');
    const encrypted = file.endsWith('_enc');
    const sha256Hex = createHash('sha256').update(raw).digest('hex');
    // keyType 取自 ssh-keygen .pub 首段（与 native keytool 的 keyType 口径一致）。
    const pubLine = readFileSync(join(FIXTURE_DIR, file + '.pub'), 'utf8').trim().split(/\s+/);
    if (pubLine.length < 2) throw new Error(file + '.pub：内容异常');
    const keyType = pubLine[0];

    let fingerprint;
    let fingerprintSource;
    const parsed = utils.parseKey(raw, encrypted ? FIXTURE_PASSPHRASE : undefined);
    if (!(parsed instanceof Error) && parsed.isPrivateKey()) {
      fingerprint = fingerprintOfBlob(Buffer.from(parsed.getPublicSSH()));
      fingerprintSource = 'ssh2';
    } else {
      // ssh2 不支持的格式（实测仅 PKCS#8 两类）：回退到 .pub 的 wire blob。
      const reason = parsed instanceof Error ? parsed.message : 'not-a-private-key';
      const pubBlob = Buffer.from(pubLine[1], 'base64');
      if (pubBlob.length === 0) throw new Error(file + '：.pub base64 为空');
      fingerprint = fingerprintOfBlob(pubBlob);
      fingerprintSource = 'ssh-keygen-pub';
      console.log(`提示：${file} ssh2 无法解析（${reason.slice(0, 60)}），指纹取自 .pub`);
    }
    if (!/^SHA256:[A-Za-z0-9+/]{43}$/.test(fingerprint)) {
      throw new Error(file + '：指纹形状异常：' + fingerprint);
    }
    return { file, keyType, format, encrypted, ssh2Parseable: fingerprintSource === 'ssh2', fingerprint, fingerprintSource, sha256Hex };
  });
}

function buildJson(entries) {
  return {
    generator: 'tools/sync-vectors/private-keys.mjs (S15)',
    desktopSsh2Version,
    fixtureDir: 'native/tests/fixtures/keys',
    fixturePassphraseHint: '文件名以 _enc 结尾的加密夹具使用 K01/N05 测试短语解析（测试专用）；JSON 不记录短语本身',
    pkcs8Note: 'ssh2@1.17 不支持 PKCS#8（见本工具文件头），rsa3072_pkcs8[_enc] 的指纹取自 ssh-keygen .pub；Lumia 出站跳过 PKCS#8',
    entries,
  };
}

function buildHeader(entries) {
  const s = (v) => JSON.stringify(v);
  const lines = [];
  lines.push('// S15 私钥指纹向量（由 tools/sync-vectors/private-keys.mjs 生成，禁止手改）。');
  lines.push('// fingerprint 经桌面端 node_modules/ssh2 的 utils.parseKey(...).getPublicSSH() + SHA256 计算；');
  lines.push('// PKCS#8 两类 ssh2 不支持，指纹取自 ssh-keygen .pub 的 wire blob（与 getPublicSSH 同物）。');
  lines.push('// 与 tests/fixtures/sync/private-key-vectors.json 同内容；用法见 keytool_test.cpp KeytoolInteropTest。');
  lines.push('#pragma once');
  lines.push('');
  lines.push('#include <cstddef>');
  lines.push('');
  lines.push('namespace sshclient {');
  lines.push('namespace crypto {');
  lines.push('namespace privkey_vectors {');
  lines.push('');
  lines.push('struct Entry {');
  lines.push('    const char* file;');
  lines.push('    const char* keyType;');
  lines.push('    const char* format;');
  lines.push('    bool encrypted;');
  lines.push('    const char* fingerprint;');
  lines.push('};');
  lines.push('');
  lines.push('inline constexpr Entry kEntries[] = {');
  for (const e of entries) {
    lines.push(`    {${s(e.file)}, ${s(e.keyType)}, ${s(e.format)}, ${e.encrypted ? 'true' : 'false'}, ${s(e.fingerprint)}},`);
  }
  lines.push('};');
  lines.push('');
  lines.push(`inline constexpr std::size_t kEntryCount = ${entries.length};`);
  lines.push('');
  lines.push('} // namespace privkey_vectors');
  lines.push('} // namespace crypto');
  lines.push('} // namespace sshclient');
  lines.push('');
  return lines.join('\n');
}

const vectorsPath = join(repoRoot, 'tests', 'fixtures', 'sync', 'private-key-vectors.json');
const headerPath = join(repoRoot, 'native', 'tests', 'private_key_vectors.h');

const entries = buildEntries();
const jsonText = JSON.stringify(buildJson(entries), null, 2) + '\n';
const headerText = buildHeader(entries);

if (process.argv.includes('--check')) {
  let failed = 0;
  const expect = (p, text) => {
    if (!existsSync(p)) {
      console.error(`缺失: ${p}`);
      failed++;
      return;
    }
    const cur = readFileSync(p);
    if (!cur.equals(Buffer.from(text, 'utf8'))) {
      console.error(`不一致: ${p}（请运行 node tools/sync-vectors/private-keys.mjs 重新生成）`);
      failed++;
    } else {
      console.log(`OK: ${p}`);
    }
  };
  expect(vectorsPath, jsonText);
  expect(headerPath, headerText);
  console.log(`private-keys --check: ${entries.length} 个夹具（ssh2 直接 ${entries.filter((e) => e.ssh2Parseable).length}，.pub 回退 ${entries.filter((e) => !e.ssh2Parseable).length}）`);
  if (failed > 0) process.exit(1);
  console.log('private-keys --check: 文件逐字节一致');
} else {
  mkdirSync(dirname(vectorsPath), { recursive: true });
  mkdirSync(dirname(headerPath), { recursive: true });
  writeFileSync(vectorsPath, jsonText);
  writeFileSync(headerPath, headerText);
  console.log('已生成:');
  console.log(`  ${vectorsPath}`);
  console.log(`  ${headerPath}`);
  for (const e of entries) console.log(`  ${e.file} ${e.format}${e.encrypted ? ' enc' : ''} ${e.fingerprint} (${e.fingerprintSource})`);
}
