namespace SshTool.Core.Sync.Protocol
{
    // §4.1 servers[].secrets：字段为 null 表示「键不存在」（下行时保留本机对应凭据），
    // 非 null（含空串）表示一个确定的值。privateKey* 四键要么全部出现要么全部不出现（§4.1 私钥元数据规则）。
    public sealed class ServerSecrets
    {
        public string Password { get; set; }
        public string Passphrase { get; set; }
        public string PrivateKey { get; set; }
        public string PrivateKeyEncoding { get; set; }   // 字面量 "base64"
        public string PrivateKeyFormat { get; set; }     // "openssh" / "pem"，与 header 一致
        public string PrivateKeyFingerprint { get; set; } // "SHA256:<43 位标准 base64 无填充>"

        public ServerSecrets Clone()
        {
            return new ServerSecrets
            {
                Password = Password,
                Passphrase = Passphrase,
                PrivateKey = PrivateKey,
                PrivateKeyEncoding = PrivateKeyEncoding,
                PrivateKeyFormat = PrivateKeyFormat,
                PrivateKeyFingerprint = PrivateKeyFingerprint
            };
        }
    }
}
