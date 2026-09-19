// S04 夹具校验：validate-fixtures.mjs
//
// 用 tsx 只读导入桌面端 E:\code\ssh-tool\src\shared\sync-schemas.ts 的
// syncDocumentV1Schema，对 SyncDocumentV1 JSON 做严格校验（桌面端 zod 即权威）。
// 桌面仓库一行不改；本文件只读 desktop-vectors.json / desktop-document-*.json
// 或 Core Reader→Writer 的重写输出。
//
// 用法：
//   node tools/sync-vectors/validate-fixtures.mjs
//     校验全部 3 份 tests/fixtures/sync/desktop-document-*.json
//   node tools/sync-vectors/validate-fixtures.mjs --file <path>
//     校验单个文件（verify.ps1 -Interop 用它校验 Core 重写输出）
//   node tools/sync-vectors/validate-fixtures.mjs --file a.json --file b.json
//     校验多个文件

import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { unlinkSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, '..', '..');
const DESKTOP_DIR = process.env.SSH_TOOL_DESKTOP_DIR || 'E:/code/ssh-tool';

function ownDependencyVersion(name) {
  const pkg = JSON.parse(readFileSync(join(scriptDir, 'package.json'), 'utf8'));
  return pkg.dependencies[name];
}

function parseArgs(argv) {
  const files = [];
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--file') {
      const v = argv[++i];
      if (!v) throw new Error('--file 缺少路径参数');
      files.push(resolve(v));
    } else if (argv[i] === '--help' || argv[i] === '-h') {
      console.log('用法: node validate-fixtures.mjs [--file <path> ...]');
      process.exit(0);
    } else {
      throw new Error(`未知参数: ${argv[i]}（仅支持 --file <path>）`);
    }
  }
  if (files.length === 0) {
    for (const n of ['empty', 'typical', 'secrets']) {
      files.push(join(repoRoot, 'tests', 'fixtures', 'sync', `desktop-document-${n}.json`));
    }
  }
  return files;
}

function findTsx() {
  // 直接用 node 跑 tsx 的 cli 入口，避免经 .cmd 需要 shell:true（DEP0190 告警）。
  const cli = join(scriptDir, 'node_modules', 'tsx', 'dist', 'cli.mjs');
  if (existsSync(cli)) return { node: true, path: cli };
  const local = join(scriptDir, 'node_modules', '.bin', process.platform === 'win32' ? 'tsx.cmd' : 'tsx');
  if (existsSync(local)) return { node: false, path: local };
  return null;
}

function validateWithDesktop(files) {
  for (const f of files) {
    if (!existsSync(f)) throw new Error(`文件不存在: ${f}`);
    // 先确认是有效 JSON，避免把 JSON 语法错误误报为 schema 错误
    JSON.parse(readFileSync(f, 'utf8'));
  }
  const schemaUrl = pathToFileURL(join(DESKTOP_DIR, 'src/shared/sync-schemas.ts')).href;
  const helper = join(tmpdir(), `s04-validate-${Date.now()}-${Math.floor(Math.random() * 1e6)}.mts`);
  writeFileSync(
    helper,
    `import { readFileSync } from 'node:fs';\n` +
      `import { syncDocumentV1Schema } from ${JSON.stringify(schemaUrl)};\n` +
      `let failed = 0;\n` +
      `for (const f of process.argv.slice(2)) {\n` +
      `  let data;\n` +
      `  try { data = JSON.parse(readFileSync(f, 'utf8')); }\n` +
      `  catch (e) { console.error('JSON 解析失败: ' + f + ': ' + e.message); failed++; continue; }\n` +
      `  const r = syncDocumentV1Schema.safeParse(data);\n` +
      `  if (r.success) { console.log('OK: ' + f); }\n` +
      `  else {\n` +
      `    failed++;\n` +
      `    console.error('SCHEMA 拒绝: ' + f);\n` +
      `    for (const issue of r.error.issues) { console.error('  - ' + (issue.path || []).join('.') + ': ' + issue.message); }\n` +
      `  }\n` +
      `}\n` +
      `process.exit(failed === 0 ? 0 : 1);\n`,
  );
  try {
    const tsx = findTsx();
    if (tsx && tsx.node) {
      execFileSync(process.execPath, [tsx.path, helper, ...files], { stdio: 'inherit' });
    } else if (tsx) {
      execFileSync(tsx.path, [helper, ...files], { stdio: 'inherit', shell: process.platform === 'win32' });
    } else {
      execFileSync('npx', ['--yes', `tsx@${ownDependencyVersion('tsx')}`, helper, ...files], {
        stdio: 'inherit',
        shell,
      });
    }
  } finally {
    try {
      unlinkSync(helper);
    } catch {}
  }
}

const files = parseArgs(process.argv.slice(2));
validateWithDesktop(files);
