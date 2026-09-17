using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    // 测试用内存文件系统；支持注入下一次写/移动失败（JsonStore 原子写验收用例）。
    public sealed class InMemoryFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.Ordinal);
        private Exception _nextWriteFailure;
        private Exception _nextMoveFailure;

        public IReadOnlyDictionary<string, string> Files
        {
            get { return _files; }
        }

        public void FailNextWrite(Exception ex)
        {
            _nextWriteFailure = ex;
        }

        public void FailNextMove(Exception ex)
        {
            _nextMoveFailure = ex;
        }

        public Task<bool> ExistsAsync(string path)
        {
            return Task.FromResult(_files.ContainsKey(path));
        }

        public Task<string> ReadAllTextAsync(string path)
        {
            string contents;
            if (!_files.TryGetValue(path, out contents))
            {
                throw new IOException("文件不存在: " + path);
            }
            return Task.FromResult(contents);
        }

        public Task WriteAllTextAsync(string path, string contents)
        {
            if (_nextWriteFailure != null)
            {
                var ex = _nextWriteFailure;
                _nextWriteFailure = null;
                throw ex;
            }
            _files[path] = contents;
            return Task.CompletedTask;
        }

        public Task MoveAndReplaceAsync(string sourcePath, string targetPath)
        {
            if (_nextMoveFailure != null)
            {
                var ex = _nextMoveFailure;
                _nextMoveFailure = null;
                throw ex;
            }
            string contents;
            if (!_files.TryGetValue(sourcePath, out contents))
            {
                throw new IOException("文件不存在: " + sourcePath);
            }
            _files.Remove(sourcePath);
            _files[targetPath] = contents;
            return Task.CompletedTask;
        }
    }
}
