#pragma once

// K01：原生密钥工具 WinRT 桥（01-DESIGN.md §6.2 KeyTool；04-TASKS K01）。
//
// 底层是 native/core/crypto/keytool（OpenSSL + 手写 openssh-key-v1 编解码）。
// 线程与失败语义：
//   - 所有方法经 concurrency::create_async 在后台线程执行（RSA-4096 生成约
//     数秒，绝不能占 UI 线程）；
//   - 任何失败（非法输入、短语错误、OpenSSL 失败）一律返回 nullptr，不抛异常、
//     不区分失败原因；
//   - 本文件不记录任何日志，调用方只记相位与耗时，绝不记短语/私钥/公钥内容
//     （日志脱敏）。
//   - 私钥/短语的本地拷贝用完即清零（crypto::SecureClear）。

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            ref class KeyInfo;

            public ref class KeyTool sealed
            {
            public:
                // 生成 ed25519 openssh-key-v1 未加密私钥；成功返回私钥全文 UTF-8
                // 字节（IBuffer），失败返回 nullptr。
                static Windows::Foundation::IAsyncOperation<
                    Windows::Storage::Streams::IBuffer^>^
                    GenerateEd25519Async(Platform::String^ comment);

                // 生成 RSA 私钥（PKCS#8 PEM）；bits 仅接受 3072/4096。
                static Windows::Foundation::IAsyncOperation<
                    Windows::Storage::Streams::IBuffer^>^
                    GenerateRsaAsync(int bits, Platform::String^ comment);

                // 解析私钥；privateKey 为文件全文 UTF-8 字节。解析失败返回
                // nullptr；加密且无短语时返回 Encrypted=true 的部分信息。
                static Windows::Foundation::IAsyncOperation<KeyInfo^>^ InspectAsync(
                    const Platform::Array<uint8>^ privateKey, Platform::String^ passphrase);
            };
        }
    }
}
