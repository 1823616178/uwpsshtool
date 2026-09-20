using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Sftp;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sftp
{
    // F02：队列 + ISftpClient 集成（执行体经假客户端真实走块循环与进度）。
    public class SftpQueueIntegrationTests
    {
        [Fact]
        public async Task UploadThroughQueue_LandsInFakeRemote()
        {
            var client = new FakeSftpClient();
            var queue = new TransferQueue();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(new string('a', 100000));
                TransferItem item = TransferItem.CreateUpload("/up/big.bin", "big.bin", payload.Length,
                    (progress, ct) =>
                    {
                        var source = new MemoryStream(payload, false);
                        return client.UploadAsync("/up/big.bin", source, progress, ct, 0,
                            SftpConstants.DefaultRemoteMode);
                    });
                queue.Enqueue(item);
                Task completed = await Task.WhenAny(item.Completion, Task.Delay(5000))
                    ;
                Assert.Same(item.Completion, completed);
                SftpResult result = await item.Completion;
                Assert.True(result.Ok);
                Assert.Equal(TransferState.Completed, item.State);
                Assert.Equal(payload.Length, item.BytesDone);
                Assert.True(client.Files.ContainsKey("/up/big.bin"));
                Assert.Equal(payload, client.Files["/up/big.bin"]);
                Assert.True(client.ProgressReports > 0);
            }
            finally
            {
                queue.Dispose();
                client.Dispose();
            }
        }
    }
}
