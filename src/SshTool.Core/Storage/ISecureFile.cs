using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    // D03：受保护字节流（明文进、明文出）。DPAPI 实现把整段加密后落盘。
    public interface ISecureFile
    {
        Task<byte[]> ReadAsync();
        Task WriteAsync(byte[] plaintext);
    }
}
