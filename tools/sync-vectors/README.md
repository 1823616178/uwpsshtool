# S04 跨端测试向量工具（tools/sync-vectors）

用桌面端相同原语生成固定向量，证明 Lumia 端（native + Core）与桌面端
`E:\code\ssh-tool` 双向互解。桌面仓库只读，一行不改。

## 工具链（版本固定）

| 组件 | 版本 | 来源 |
|---|---|---|
| node | >=20 <25（实测 24.15.0） | `package.json engines` |
| hash-wasm | 4.12.0（精确） | 与桌面端 `package-lock.json` 的 `node_modules/hash-wasm` 一致，`generate.mjs` 启动即断言 |
| tsx | 4.23.13（精确） | 仅用于只读导入桌面端 `.ts` 做自检/校验，不参与生成 |
| zod | 桌面端自带（lock 4.4.3） | `validate-fixtures.mjs` 经 tsx 导入桌面端 `syncDocumentV1Schema`，不用本工具自带版本 |

安装：`npm --prefix tools/sync-vectors install`（或在该目录 `npm install`）。

## 固定输入（写死，永不改）

- 同步密码 `S04-desktop-sync-password`；vaultId `s04-desktop-vault`；keyVersion/schemaVersion 1
- KDF argon2id 65536/3/1（桌面端 `DEFAULT_KDF_PARAMETERS`）
- kdfSalt `A0..AF`（16B）；vaultKey `40..5F`（32B）；recoveryRaw `60..7F`（32B）
- 密码包裹 nonce `B0..BB`；恢复包裹 nonce `C0..CB`；文档 nonce empty `D0..DB` / typical `E0..EB` / secrets `F0..FB`（各 12B）
- 明文 = 3 份 canonical `SyncDocumentV1`（canonical 规则与 Core `SyncDocumentWriter` 逐字节一致）：
  - `desktop-document-empty.json`：空文档
  - `desktop-document-typical.json`：2 主机 + 2 分组 + local/relay 各一条隧道（含 relay 引用与分组引用）
  - `desktop-document-secrets.json`：1 台主机带 `secrets.password` + `passphrase`

## 生成原语（与 crypto-vault.ts 逐行对齐）

`generate.mjs` 独立实现（非导入桌面端生成，避免循环论证）：
AAD 编码、argon2id（hash-wasm）、HKDF-SHA256（salt 空）、AES-256-GCM（tag 拼密文末尾）、
SPM1 恢复密钥（base64url 无填充 + `SHA256("SPM1"||raw)[0..12]` 大写校验）、规范 Base64、
`ciphertextHash = hex(sha256(ct||tag))`。

自检（只读导入桌面端，只用解密侧，避免循环论证）：
`unwrapVaultKeyWithPassword`、`unwrapVaultKeyWithRecovery`、`decryptSyncDocument`
对生成的信封 + 3 份文档逐一验证，失败即非 0 退出。

## 输出（确定性字节，提交后冻结）

- `tests/fixtures/sync/desktop-document-{empty,typical,secrets}.json`
  明文 canonical JSON，单行、无 BOM、无尾换行（即 `SyncDocumentWriter.Write` 原样）。
- `tests/fixtures/sync/desktop-vectors.json`
  信封 + 3 份文档信封（nonce/ciphertext/ciphertextHash/sha256），2 空格缩进。
  明文不重复存放（以文档夹具为准，`plaintextSha256` 交叉核对）。
- `native/tests/desktop_vectors.h`
  同内容的 C++ 头（`sshclient::crypto::desktop`），明文用 `R"S04VEC(...)S04VEC"` 原始串。

向量文件一旦提交**禁止修改**（03-SYNC-PROTOCOL.md §3.5 第 3 条）。

## 命令

```powershell
npm --prefix tools/sync-vectors install
node tools/sync-vectors/generate.mjs            # 生成全部文件（含桌面端自检）
node tools/sync-vectors/generate.mjs --check    # 逐字节校验（不写文件；verify 用）
node tools/sync-vectors/validate-fixtures.mjs                       # 校验 3 份文档夹具
node tools/sync-vectors/validate-fixtures.mjs --file <path>         # 校验单个文件（verify 用它校验 Core 重写输出）
pwsh scripts/verify.ps1 -Quick -Interop         # 全部门禁（含 native 向量 + Core 重写校验）
```

`verify.ps1 -Interop` 步骤：`generate.mjs --check` → native ctest（含 `desktop_vectors_test`）
→ `dotnet test`（含 `DesktopFixtureTests`，顺带落盘 Core 重写输出到 `artifacts/sync-interop/`）
→ `validate-fixtures.mjs` 校验 3 份原文 + 3 份重写输出。

## 遗留偏差（S03 范围外，未改协议文档）

- `doc/03-SYNC-PROTOCOL.md §3.5` 写的 `kCiphertextHash = c74d2505…4274` 是重录前旧值；
  冻结头 `native/tests/vault_golden_vectors.h` 的 `7642985e…9a73c8` 才是桌面端实际生成值，
  以后者为准（见该头文件头注）。本工具新向量以桌面端实际生成值为准，不跟旧值。
