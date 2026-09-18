using System;
using System.Collections.Generic;
using System.Threading;

namespace SshTool.Core.Sessions
{
    // N09b：AuthPrompt 事件参数（keyboard-interactive，镜像 native
    // Bridge.AuthPromptEventArgs）。处理方就地 Respond/Cancel，或存下 args
    // 转 UI 线程异步答复（任意线程安全）。未答复由 core 120 s 硬窗口兜底
    // 空答复（不因异步编排暂停）。答复幂等：先到先赢。事件在会话 I/O 线程触发。
    //
    // 注意：N05 的 core sink 接口不携带 Name/Instruction，暂为空串
    // （02-UI-DESIGN 的对话框只展示 Prompts）。
    public sealed class AuthPromptEventArgs : EventArgs
    {
        private readonly Action<bool, IReadOnlyList<string>> _decision;
        private int _decided;

        public AuthPromptEventArgs(string name, string instruction,
                                   IReadOnlyList<string> prompts, IReadOnlyList<bool> echo,
                                   Action<bool, IReadOnlyList<string>> decision)
        {
            Name = name ?? string.Empty;
            Instruction = instruction ?? string.Empty;
            Prompts = prompts ?? new string[0];
            Echo = echo ?? new bool[0];
            _decision = decision ?? throw new ArgumentNullException(nameof(decision));
        }

        public string Name { get; }
        public string Instruction { get; }
        public IReadOnlyList<string> Prompts { get; }
        public IReadOnlyList<bool> Echo { get; }

        public bool IsDecided
        {
            get { return Volatile.Read(ref _decided) != 0; }
        }

        // answers 长度应对齐 Prompts。
        public void Respond(IReadOnlyList<string> answers)
        {
            Decide(true, answers ?? new string[0]);
        }

        // 等价于空答复（服务器会拒绝本轮）。
        public void Cancel()
        {
            Decide(false, new string[0]);
        }

        private void Decide(bool answered, IReadOnlyList<string> answers)
        {
            if (Interlocked.CompareExchange(ref _decided, 1, 0) != 0)
            {
                return;
            }
            _decision(answered, answers);
        }
    }
}
