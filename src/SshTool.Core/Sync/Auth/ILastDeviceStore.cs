namespace SshTool.Core.Sync.Auth
{
    // fix/auth-audit：记住本机上一次登录得到的设备记录（userId + deviceId，均非机密）。
    // 服务端每次登录都会新建设备记录，退出登录只吊销 refreshToken、不撤销设备（api-v1 §2.2 / §2.4），
    // 账号设备配额 10 台：退出后再登录就多一条死记录，攒满后本机再也登录不上
    // （DEVICE_QUOTA_EXCEEDED，只能去别的客户端撤销）。登录成功后用新会话撤销上一条本机记录即可止住堆积。
    // 实现必须不抛：读失败等同于「没有记录」，写失败静默忽略（只影响清理，不影响登录）。
    public interface ILastDeviceStore
    {
        string Read();

        // value 为 null 表示清除。
        void Write(string value);
    }
}
