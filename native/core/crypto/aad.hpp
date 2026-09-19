// S03: AAD（附加认证数据）构造 —— doc/03-SYNC-PROTOCOL.md §3.2 的 native 实现。
//
// 编码规则（与桌面端 crypto-vault.ts 的 aad() 逐字节一致）：
//   每个字段编码为 <utf8字节长度>:<值>，字段间用 | 连接，整体前缀 <domain>|。
//   长度前缀用于消除字段边界歧义，不能省。
//   数字字段按十进制字符串传入。
//
// 纯逻辑：只依赖 C++ 标准库。UWP（NativeCore.vcxitems）与宿主机测试
// （native/tests/CMakeLists.txt）两处编译，行为必须一致。
#pragma once

#include "sync_params.h"

#include <string>
#include <string_view>
#include <vector>

namespace sshclient {
namespace crypto {

// 三个 AAD 域字符串的唯一出处是 sync_params.h。此处不再重复定义。

// 通用编码：<domain>|<len1>:<value1>|<len2>:<value2>...
// fields 为空时返回 domain 本身（无尾部分隔符）。长度取 UTF-8 字节数（string_view::size）。
std::string EncodeAad(std::string_view domain, const std::vector<std::string_view> &fields);

// 文档 AAD：fields = [vaultId, schemaVersion, keyVersion]（§3.2）。
// schemaVersion / keyVersion 按十进制字符串传入（如 "1"）。
std::string SyncDocumentAad(std::string_view vaultId, std::string_view schemaVersion,
                            std::string_view keyVersion);

// 密码包裹 AAD：fields = [keyVersion]。
std::string VaultKeyPasswordAad(std::string_view keyVersion);

// 恢复包裹 AAD：fields = [keyVersion]。
std::string VaultKeyRecoveryAad(std::string_view keyVersion);

} // namespace crypto
} // namespace sshclient
