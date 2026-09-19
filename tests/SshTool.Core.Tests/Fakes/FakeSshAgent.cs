using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Sessions;

namespace SshTool.Core.Tests.Fakes
{
    // K03：ISshAgent 的内存假实现（供 SessionManager agent 流程单测）。
    // 语义对齐 native SshAgent：格式粗检（私钥头前缀）+ 惰性超时（可注入时钟
    // 快进）+ lock 清除。私钥材料只存内存字典，不落盘。
    public sealed class FakeSshAgent : ISshAgent
    {
        private static readonly string[] KeyHeaders =
        {
            "-----BEGIN OPENSSH PRIVATE KEY-----",
            "-----BEGIN RSA PRIVATE KEY-----",
            "-----BEGIN EC PRIVATE KEY-----",
            "-----BEGIN DSA PRIVATE KEY-----",
            "-----BEGIN PRIVATE KEY-----",
            "-----BEGIN ENCRYPTED PRIVATE KEY-----"
        };

        private readonly Dictionary<string, string[]> _keys =
            new Dictionary<string, string[]>(StringComparer.Ordinal);
        private DateTime _activeAt;
        private int _timeoutMinutes;

        // 可注入时钟（默认 UtcNow；测试快进超时用）。
        public Func<DateTime> Now = () => DateTime.UtcNow;

        // 调用记录。
        public readonly List<string> Calls = new List<string>();
        public int UnlockCount;
        public int LockAllCount;

        public FakeSshAgent()
        {
            _activeAt = Now();
        }

        public Task<bool> UnlockAsync(string keyId, string privateKeyText, string passphrase)
        {
            Calls.Add("Unlock:" + keyId);
            UnlockCount++;
            ExpireIfNeeded();
            if (string.IsNullOrEmpty(keyId) || !LooksLikeKey(privateKeyText))
            {
                return Task.FromResult(false);
            }
            _keys[keyId] = new[] { privateKeyText, passphrase ?? string.Empty };
            _activeAt = Now();
            return Task.FromResult(true);
        }

        public bool Lock(string keyId)
        {
            Calls.Add("Lock:" + keyId);
            ExpireIfNeeded();
            return _keys.Remove(keyId);
        }

        public void LockAll()
        {
            Calls.Add("LockAll");
            LockAllCount++;
            _keys.Clear();
        }

        public void SetTimeout(int minutes)
        {
            Calls.Add("SetTimeout:" + minutes);
            _timeoutMinutes = minutes < 0 ? 0 : minutes;
            ExpireIfNeeded();
        }

        public int TimeoutMinutes
        {
            get
            {
                ExpireIfNeeded();
                return _timeoutMinutes;
            }
        }

        public bool IsLocked(string keyId)
        {
            ExpireIfNeeded();
            return !_keys.ContainsKey(keyId);
        }

        public int KeyCount
        {
            get
            {
                ExpireIfNeeded();
                return _keys.Count;
            }
        }

        // 测试 helper：把时钟快进（模拟超时流逝）。
        public void Advance(TimeSpan delta)
        {
            _activeAt = _activeAt - delta;
        }

        private void ExpireIfNeeded()
        {
            if (_timeoutMinutes <= 0 || _keys.Count == 0)
            {
                return;
            }
            if (Now() - _activeAt >= TimeSpan.FromMinutes(_timeoutMinutes))
            {
                _keys.Clear();
            }
        }

        private static bool LooksLikeKey(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            int begin = 0;
            while (begin < text.Length && (text[begin] == ' ' || text[begin] == '\t'
                || text[begin] == '\r' || text[begin] == '\n'))
            {
                begin++;
            }
            for (int i = 0; i < KeyHeaders.Length; i++)
            {
                string header = KeyHeaders[i];
                if (begin + header.Length <= text.Length
                    && string.Compare(text, begin, header, 0, header.Length, StringComparison.Ordinal) == 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
