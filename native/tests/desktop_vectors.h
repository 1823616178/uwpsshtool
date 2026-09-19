// S04 桌面端互通向量（由 tools/sync-vectors/generate.mjs 生成，禁止手改）。
// 固定输入见 generate.mjs 文件头；与 tests/fixtures/sync/desktop-vectors.json 同内容。
// 用法见 native/tests/desktop_vectors_test.cpp。文件一旦提交禁止修改。
#pragma once

#include <cstddef>
#include <cstdint>

namespace sshclient {
namespace crypto {
namespace desktop {

inline constexpr char kHashWasmVersion[] = "4.12.0";
inline constexpr char kSyncPassword[] = "S04-desktop-sync-password";
inline constexpr char kVaultId[] = "s04-desktop-vault";
inline constexpr int kSchemaVersion = 1;
inline constexpr int kKeyVersion = 1;
inline constexpr char kKdfAlgorithm[] = "argon2id";
inline constexpr std::uint32_t kKdfMemoryKib = 65536;
inline constexpr std::uint32_t kKdfIterations = 3;
inline constexpr std::uint32_t kKdfParallelism = 1;

inline constexpr std::uint8_t kKdfSalt[16] = {
    0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
    0xa8, 0xa9, 0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf
};
inline constexpr std::uint8_t kPasswordWrapNonce[12] = {
    0xb0, 0xb1, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7,
    0xb8, 0xb9, 0xba, 0xbb
};
inline constexpr std::uint8_t kRecoveryWrapNonce[12] = {
    0xc0, 0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7,
    0xc8, 0xc9, 0xca, 0xcb
};
inline constexpr std::uint8_t kVaultKey[32] = {
    0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47,
    0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f,
    0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57,
    0x58, 0x59, 0x5a, 0x5b, 0x5c, 0x5d, 0x5e, 0x5f
};
inline constexpr std::uint8_t kRecoveryRaw[32] = {
    0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67,
    0x68, 0x69, 0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f,
    0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77,
    0x78, 0x79, 0x7a, 0x7b, 0x7c, 0x7d, 0x7e, 0x7f
};
inline constexpr std::uint8_t kDocEmptyNonce[12] = {
    0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7,
    0xd8, 0xd9, 0xda, 0xdb
};
inline constexpr std::uint8_t kDocTypicalNonce[12] = {
    0xe0, 0xe1, 0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7,
    0xe8, 0xe9, 0xea, 0xeb
};
inline constexpr std::uint8_t kDocSecretsNonce[12] = {
    0xf0, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7,
    0xf8, 0xf9, 0xfa, 0xfb
};

inline constexpr char kKdfSaltB64[] = "oKGio6SlpqeoqaqrrK2urw==";
inline constexpr char kVaultKeyHex[] = "404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f";
inline constexpr char kRecoveryRawHex[] = "606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f";
inline constexpr char kRecoveryKey[] = "SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E";
inline constexpr char kPasswordWrapNonceB64[] = "sLGys7S1tre4ubq7";
inline constexpr char kRecoveryWrapNonceB64[] = "wMHCw8TFxsfIycrL";
inline constexpr char kPasswordWrappedKeyB64[] = "wvfApdXNSXei/V00tGz/lpdCC7/VmTwq37RcduvBsbMfF04eFeLcItWGCRAm6TYN";
inline constexpr char kRecoveryWrappedKeyB64[] = "TGiVQLsaYBaOX01iN2sEX4XZkZBBAd0MGn9lBY7t0THGgnxFddb5IQz/mzQ0EhQQ";

inline constexpr char kDocEmptyNonceB64[] = "0NHS09TV1tfY2drb";
inline constexpr char kDocEmptyCiphertextB64[] = "pO6ltJ4yiDKnLlj8ySKLWJaQ8JQxUV6KqCfmDfNs/FlWqAodaI4dysT4AM/LJkaVXMbQM4/oDVkGm3uWzTTtP+ztNaRW4X9ePdVJpOqKvp6dnzNovcALXU4uHgSC3Sptac/dczPSDWsQqzklyxvqQDb8RqBzPfvd8ER2bQzZo3G85iUuK8O2fbF4tNcnKjx5z8iezGkFQpHAhW7o7wYCZXmmb94BwcamhO2dbDEe";
inline constexpr char kDocEmptyCiphertextHash[] = "b130f1a8caabd61fdbbcacb8e60837b58d628295e61e3e679856ac9725487ddd";
inline constexpr char kDocEmptyPlaintextSha256[] = "ab4f1ed7c1dd3ab0ca881ea8cb046bd10a976176653243ea0f07952e24bfdccc";
inline constexpr char kDocEmptyPlaintext[] = R"S04VEC({"schemaVersion":1,"updatedAt":"2026-09-17T08:00:00.000Z","preferences":{"syncPasswords":false,"syncPrivateKeys":false},"servers":[],"tunnels":[],"groups":[]})S04VEC";

inline constexpr char kDocTypicalNonceB64[] = "4OHi4+Tl5ufo6err";
inline constexpr char kDocTypicalCiphertextB64[] = "1Pq6CrJq2bHDpoSUo9B5SrEHzL2zRd6e4nvMbRS90E7KcJaWT6QkvbJ5sXj1huSwJ1Qwr8yotg6mTOB2F2pR/E0LAE8xw4r83+yUcUFpXj9BER9lN4Wmh+6735vzISrv+r1ZP6JaMDiWEAEE901d9qtXzFofEXLUXbIjNdjqcYTp+Y+qt0k/MirzM+YAf3DpjqBRxGRE31V3ANAeEeD7uBb2Nf3Ea7As5XvHJJiIn+CmMZKqPvvr1rNbE1QAkMo7yiuIrucSEouKb9cisjCKXCFjUXrTm5w59b7MzI7o+hSyZCpNAEeQJJj7liEVAkzDPptklw8kk4xjjsx9FoTnCmDf2GF/RX6xDewG3rHtVvl2KyTDgtcwm1IigfezFNZ4sekG1OUhAQzzE+hGrNoPXAqIXh91UM9PoRCvnR+CK5d+XmkgJaT1KJx2TidQxeuHRZTQzEAsOPMFySz0KlnDbZJ2sX/nIPvIlZ1PR6R41t49nRqW2Um3xT0e0LIFFH3awtjUpginKJUl5L4FxMyVyZxrachrJxVTuJ5UoojGimYHd4TffO8qniTJ1Cbm9Bsew2zAz9jgbAx3tI+Au8h0GDiIL5pRvQLQGWgdmqJbI0rIaeB/LXGH4z2Ti9Ma3w7jd9idZLwyifnlR6uLibWiONax6reW7YUTsshJlJy+SH2BDev5GdAbYjw+Km9ZdYJ85Eqg7t2dbbs2gLSGM7s2NCh6l4X1kXyK/Y4MB/dseae6EhNEYT34knldww/lmf/XOrnHaYzwalDQOAp8I+eBo55mIlwOsTTm2trRBzEkzumxd/XX7tkF7fVNJiaS+TusEQcGUZSwm3FEptn+8+/qA2Zp222DZJd0OZPI/Ds9JYf6ooie26rD0z2+MFb0wdSuCJi2YzswelBebePKgeOaed036wgRXN2Yl2mwu/DTYEr/x7X2vEvjCUM8LBTloruTFk/lC8WWyFoD0PcRSdo/D6R56JAZ2MBsObnBME39gl3/6ACVwTLZHNWDFtpAMLv3B2dlSwwLQ6ZYz+cLXDb7u+anQBkOUVp6XMGKxoFdldpoQeiJ7TXKZpVsLPD0icvPspzxB+yIEC0t60NUCnAVlbnc+TaBYuPMFxMGrdSllkUGX4834tAvZ8qt+ztIgQXRnxZinPKIEM2UQOIaxJhwCzLMTi7wVnylaiuiTnd2I0udu0pU49GRpb7qCRHoptSqiSUHywepUJRrVXa4BlQWCvPq1ty6LVaDfGtBDtzTFYxjUsaJOwkdkHGzGB/cvYtONERe5FTuJl/ek4s0rgq3mz3tflcs2sAkboONjirHK6+S119MHlF/MkwMZQb2k1E4s6JdcYoMLxPJTFEI3+e9ijIY5MYzicAjurKOJVx7UyI+";
inline constexpr char kDocTypicalCiphertextHash[] = "87f89566cc61bec292877f4870abb814dd36a367315b3f93d0535f86efa3678c";
inline constexpr char kDocTypicalPlaintextSha256[] = "87ca0ffa7fa5ead331df52f83cd85dcc72f9f7c93258a825b2b85a3c3131bf1b";
inline constexpr char kDocTypicalPlaintext[] = R"S04VEC({"schemaVersion":1,"updatedAt":"2026-09-17T08:00:00.000Z","preferences":{"syncPasswords":false,"syncPrivateKeys":false},"servers":[{"profile":{"id":"srv-01","name":"jump","host":"jump.example.com","port":22,"username":"jump","authType":"agent","hostFingerprint":"","keepalive":30}},{"profile":{"id":"srv-02","name":"prod","host":"prod.internal","port":2222,"username":"deploy","authType":"password","hostFingerprint":"SHA256:s04-desktop-fingerprint","keepalive":60}}],"tunnels":[{"id":"tun-01","name":"web","serverId":"srv-01","groupId":"grp-01","type":"local","listenHost":"127.0.0.1","listenPort":8080,"destHost":"10.0.0.5","destPort":80,"destServerId":"","autoReconnect":true,"enabled":true},{"id":"tun-02","name":"relay-prod","serverId":"srv-02","groupId":"grp-02","type":"relay","listenHost":"0.0.0.0","listenPort":7000,"destHost":"","destPort":0,"destServerId":"srv-01","autoReconnect":false,"enabled":true}],"groups":[{"id":"grp-01","name":"default","color":"#4F8CFF"},{"id":"grp-02","name":"prod","color":"#16A34A"}]})S04VEC";

inline constexpr char kDocSecretsNonceB64[] = "8PHy8/T19vf4+fr7";
inline constexpr char kDocSecretsCiphertextB64[] = "qB8FYZc3vJkm2OqKxNfAeNVGnnkPhDViIFKh2virfkxhrlWlSuQaQ+SV8MImzzODe8hFA9qhzL42ESV1gxGd9860yBj4v9+BpJqKpjKeDnqnBt3qxOJwsig34pJn8ptqiQ7JwAilYL9A8iGaXWEYuijoSon4X9010gOVATWutV8/h3rL5yyMTeIeVALtm1a6G+/LA3r+0e59AlOryKZFgqoyxO9/xAFOg1FV6fhQPaKrVZHnECAOP1zWzDZNWabVVgUydsXQolDxFgHj8GoSxjPg3lriQOey23Ux4BFATM+cNCaXNEO8kLQPY6k8/QrCKwLn0m5kwym8vwAUuSHI5vhLCHKgB0ITzagY/w7jl9O7ex8eeIAWALSkeThEcMuE0dIk4E830GOI0YkiYlJG8wZxjz18nZ3X1kMNbbVgvsYVVQXM4q/DZ4eFQf5CRl3u7kBzEOJTbFQx35y9i1gm5xOZSYQ24KVLYc1c6Fn43OmmCd46qMwRvPRBVt3ba4Va6iR1UI3VShWgE9H2P3RVboX+cMIXmgV7yHLY0CL625MbgoExgOGRBzx0tzIh";
inline constexpr char kDocSecretsCiphertextHash[] = "293f170e392e8ecb77bca3f8fe0a3e2af7d678ac40c0a827fa14d26abf1e3473";
inline constexpr char kDocSecretsPlaintextSha256[] = "6582875b136ae13f6fa13ee83a460dc8546474311507ccb50f0f6c9a3d699edc";
inline constexpr char kDocSecretsPlaintext[] = R"S04VEC({"schemaVersion":1,"updatedAt":"2026-09-17T08:00:00.000Z","preferences":{"syncPasswords":true,"syncPrivateKeys":false},"servers":[{"profile":{"id":"srv-secret-01","name":"secret-host","host":"secret.example.com","port":22,"username":"admin","authType":"password","hostFingerprint":"","keepalive":30},"secrets":{"password":"S04-Test-Password-123","passphrase":"S04-Test-Passphrase-456"}}],"tunnels":[],"groups":[]})S04VEC";

} // namespace desktop
} // namespace crypto
} // namespace sshclient
