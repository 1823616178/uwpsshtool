using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    public sealed class InMemorySecureFile : ISecureFile
    {
        private byte[] _data = new byte[0];

        public Task<byte[]> ReadAsync()
        {
            var copy = new byte[_data.Length];
            if (_data.Length > 0)
            {
                System.Array.Copy(_data, copy, _data.Length);
            }
            return Task.FromResult(copy);
        }

        public Task WriteAsync(byte[] plaintext)
        {
            if (plaintext == null)
            {
                _data = new byte[0];
            }
            else
            {
                _data = new byte[plaintext.Length];
                System.Array.Copy(plaintext, _data, plaintext.Length);
            }
            return Task.CompletedTask;
        }
    }
}
