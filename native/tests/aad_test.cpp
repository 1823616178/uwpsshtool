// S03: AAD 编码单测（doc/03-SYNC-PROTOCOL.md §3.2）。
//
// 断言规则：<utf8字节长度>:<值> 编码、| 连接、<domain>| 前缀。
// 期望值与桌面端 crypto-vault.ts 的 aad() 及鸿蒙端 aad_test.cpp 一致，
// 是 AAD 黄金格式的回归基线（改动会使旧保险库解不开）。
#include <gtest/gtest.h>

#include "crypto/aad.hpp"

using sshclient::crypto::EncodeAad;
using sshclient::crypto::SyncDocumentAad;
using sshclient::crypto::VaultKeyPasswordAad;
using sshclient::crypto::VaultKeyRecoveryAad;

// 三个域的完整拼接结果，逐字节对齐 §3.2
TEST(AadTest, SyncDocumentAadMatchesProtocol)
{
    EXPECT_EQ(SyncDocumentAad("vault-abc", "1", "7"),
              "ssh-port-mapper/sync-document/v1|9:vault-abc|1:1|1:7");
}

TEST(AadTest, VaultKeyPasswordAadMatchesProtocol)
{
    EXPECT_EQ(VaultKeyPasswordAad("7"), "ssh-port-mapper/vault-key/password/v1|1:7");
}

TEST(AadTest, VaultKeyRecoveryAadMatchesProtocol)
{
    EXPECT_EQ(VaultKeyRecoveryAad("12"), "ssh-port-mapper/vault-key/recovery/v1|2:12");
}

// 长度前缀是 UTF-8 字节数，不是字符数："主机" 二字共 6 字节
TEST(AadTest, LengthPrefixCountsUtf8Bytes)
{
    EXPECT_EQ(EncodeAad("d", {"主机"}), "d|6:主机");
}

// 长度前缀存在的意义：消除字段边界歧义（§3.2「不能省」）
TEST(AadTest, LengthPrefixDisambiguatesFieldBoundaries)
{
    EXPECT_NE(EncodeAad("d", {"12", "3"}), EncodeAad("d", {"1", "23"}));
    EXPECT_EQ(EncodeAad("d", {"12", "3"}), "d|2:12|1:3");
}

// 边界：空字段编码为 0:；无字段时只有 domain，不留尾部分隔符
TEST(AadTest, EmptyFieldAndNoFields)
{
    EXPECT_EQ(EncodeAad("d", {""}), "d|0:");
    EXPECT_EQ(EncodeAad("d", {}), "d");
}
