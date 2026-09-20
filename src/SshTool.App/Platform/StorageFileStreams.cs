using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SshTool.App.Platform
{
    // F02：IRandomAccessStream 的 Stream 适配（Core ISftpClient 传输泵只认
    // System.IO.Stream；F03 用 FileOpenPicker/FileSavePicker 拿 StorageFile
    // 后经本类接入队列）。
    //
    // 语义：CanSeek 恒真（SFTP 续传要定位）；Length/Position 直通 Size/Position；
    // SetLength 改 Size（下载 resume==0 时截断）；Flush 刷底层；Dispose 关底层流。
    // 读写经 IInputStream.ReadAsync / IOutputStream.WriteAsync（15063 可用），
    // 单次上限 32 KiB 由调用方保证（SftpConstants.ChunkSize）。
    public sealed class RandomAccessStreamAdapter : Stream
    {
        private readonly IRandomAccessStream _stream;
        private readonly bool _writable;
        private bool _disposed;

        public RandomAccessStreamAdapter(IRandomAccessStream stream, bool writable)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }
            _stream = stream;
            _writable = writable;
        }

        // 上传用：只读打开 StorageFile。
        public static async Task<RandomAccessStreamAdapter> OpenForUploadAsync(StorageFile file)
        {
            if (file == null)
            {
                throw new ArgumentNullException("file");
            }
            IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read)
                .AsTask().ConfigureAwait(false);
            return new RandomAccessStreamAdapter(stream, false);
        }

        // 下载用：读写打开（不存在由调用方先创建；截断由泵按 resumeOffset 决定）。
        public static async Task<RandomAccessStreamAdapter> OpenForDownloadAsync(StorageFile file)
        {
            if (file == null)
            {
                throw new ArgumentNullException("file");
            }
            IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite)
                .AsTask().ConfigureAwait(false);
            return new RandomAccessStreamAdapter(stream, true);
        }

        public override bool CanRead
        {
            get { return !_disposed; }
        }

        public override bool CanSeek
        {
            get { return !_disposed; }
        }

        public override bool CanWrite
        {
            get { return _writable && !_disposed; }
        }

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return (long)_stream.Size;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return (long)_stream.Position;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException("value");
                }
                _stream.Seek((ulong)value);
            }
        }

        public override void Flush()
        {
            // 同步刷盘在 UWP 上无可靠实现：本适配要求调用方用 FlushAsync
            // 落盘（下载泵收尾即如此）；此处 no-op 以免破坏 Stream 契約调用方。
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _stream.FlushAsync().AsTask(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                                                  CancellationToken cancellationToken)
        {
            ValidateBuffer(buffer, offset, count);
            ThrowIfDisposed();
            if (count == 0)
            {
                return 0;
            }
            IBuffer result = await _stream.ReadAsync(new Windows.Storage.Streams.Buffer((uint)count), (uint)count,
                InputStreamOptions.None).AsTask(cancellationToken).ConfigureAwait(false);
            if (result == null || result.Length == 0)
            {
                return 0;
            }
            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(result, out bytes);
            int got = Math.Min(bytes.Length, count);
            Array.Copy(bytes, 0, buffer, offset, got);
            return got;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count,
                                              CancellationToken cancellationToken)
        {
            ValidateBuffer(buffer, offset, count);
            ThrowIfDisposed();
            if (!_writable)
            {
                throw new NotSupportedException("Stream is read-only.");
            }
            if (count == 0)
            {
                return;
            }
            byte[] slice;
            if (offset == 0 && count == buffer.Length)
            {
                slice = buffer;
            }
            else
            {
                slice = new byte[count];
                Array.Copy(buffer, offset, slice, 0, count);
            }
            IBuffer winrt = CryptographicBuffer.CreateFromByteArray(slice);
            await _stream.WriteAsync(winrt).AsTask(cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    target = offset;
                    break;
                case SeekOrigin.Current:
                    target = Position + offset;
                    break;
                case SeekOrigin.End:
                    target = Length + offset;
                    break;
                default:
                    throw new ArgumentException("origin");
            }
            if (target < 0)
            {
                throw new ArgumentOutOfRangeException("offset");
            }
            Position = target;
            return target;
        }

        public override void SetLength(long value)
        {
            ThrowIfDisposed();
            if (!_writable)
            {
                throw new NotSupportedException("Stream is read-only.");
            }
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException("value");
            }
            _stream.Size = (ulong)value;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    _stream.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(offset < 0 ? "offset" : "count");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("RandomAccessStreamAdapter");
            }
        }
    }
}
