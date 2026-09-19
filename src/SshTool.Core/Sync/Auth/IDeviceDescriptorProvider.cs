namespace SshTool.Core.Sync.Auth
{
    using SshTool.Core.Sync.Api.Dtos;

    // 设备描述供应（03-SYNC-PROTOCOL.md §7.1）。
    // Core 只定义接口；默认名称 / 平台 / 版本由 App 层 UwpDeviceDescriptorProvider 实现，
    // 测试用替身直接返回固定值。
    public interface IDeviceDescriptorProvider
    {
        // nameOverride：用户填写的设备名；null 或空白时用系统默认名。
        // 返回的 Name 保证截断到 ≤255 字符。
        DeviceDescriptorData GetDescriptor(string nameOverride = null);
    }
}
