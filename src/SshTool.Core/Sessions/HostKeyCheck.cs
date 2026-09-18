using System;
using System.Threading;

namespace SshTool.Core.Sessions
{
    // N09b：对端主机密钥信息（镜像 native Bridge.HostKeyInfo，N04 产出）。
    public sealed class HostKeyInfo
    {
        public HostKeyInfo(string keyType, string fingerprintSha256, string randomArt)
        {
            KeyType = keyType ?? string.Empty;
            FingerprintSha256 = fingerprintSha256 ?? string.Empty;
            RandomArt = randomArt ?? string.Empty;
        }

        public string KeyType { get; }
        public string FingerprintSha256 { get; }
        public string RandomArt { get; }
    }

    // N09b：HostKeyCheck 事件参数。处理方就地 Accept/Reject，或先存下 args
    // 转 UI 线程异步作答（决策窗 60 s 由 native DecisionGate 承担；适配器在
    // 作答前持有 Deferral，挂起期间不计时）。作答幂等：先到先赢，二次忽略。
    // 超时/取消 = Reject（fail-closed 303）。事件在会话 I/O 线程触发。
    public sealed class HostKeyCheckEventArgs : EventArgs
    {
        private readonly Action<bool> _decision;
        private int _decided;

        public HostKeyCheckEventArgs(HostKeyInfo info, Action<bool> decision)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            _decision = decision ?? throw new ArgumentNullException(nameof(decision));
        }

        public HostKeyInfo Info { get; }

        public bool IsDecided
        {
            get { return Volatile.Read(ref _decided) != 0; }
        }

        public void Accept()
        {
            Decide(true);
        }

        public void Reject()
        {
            Decide(false);
        }

        private void Decide(bool accept)
        {
            if (Interlocked.CompareExchange(ref _decided, 1, 0) != 0)
            {
                return; // 二次作答忽略
            }
            _decision(accept);
        }
    }
}
