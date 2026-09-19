using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using NativeBridge = SshTool.Native.Bridge;

namespace SshTool.App.Platform
{
    // K03：ISshAgent 的原生适配（01-DESIGN.md §4.1：Core 不引用 Native，
    // WinRT 类型转换与 IAsyncOperation→Task 都在本层；模式见 NativeKeyTool）。
    //
    // 单例语义：AppServices 持有单个实例并注入 SessionManager，多会话复用同一
    // 份 native 内存（01-DESIGN.md §12.1）。超时由 SessionManager 每次认证前按
    // 设置项对齐；挂起清除走 SessionManager.LockAgentKeys。
    // 线程：native 方法经 create_async 已在后台线程执行，本层只做转换，
    // 全程 ConfigureAwait(false)，绝不回 UI 线程。
    // 日志脱敏：只记方法名 + 成功与否 + 耗时毫秒；绝不记私钥、短语内容
    // （keyId 与计数非敏感，可记）。
    public sealed class NativeSshAgent : ISshAgent
    {
        private readonly NativeBridge.SshAgent _agent = new NativeBridge.SshAgent();
        private readonly ILogger _logger;

        public NativeSshAgent(ILogger logger)
        {
            _logger = logger;
        }

        public NativeSshAgent()
            : this(null)
        {
        }

        // K03：供 NativeSshSession 直通 native（同一程序集内可见，不进 Core）。
        internal NativeBridge.SshAgent Native
        {
            get { return _agent; }
        }

        public async Task<bool> UnlockAsync(string keyId, string privateKeyText, string passphrase)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(privateKeyText))
                {
                    Log("UnlockAsync", "拒绝", watch);
                    return false;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(privateKeyText);
                bool ok = await _agent.UnlockAsync(
                    keyId, bytes, passphrase ?? string.Empty).AsTask().ConfigureAwait(false);
                Array.Clear(bytes, 0, bytes.Length);
                Log("UnlockAsync", ok ? "成功" : "拒绝", watch);
                return ok;
            }
            catch (Exception ex)
            {
                Log("UnlockAsync", "异常 " + ex.GetType().Name, watch);
                return false;
            }
        }

        public bool Lock(string keyId)
        {
            try
            {
                return _agent.Lock(keyId);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void LockAll()
        {
            try
            {
                _agent.LockAll();
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.Log(LogLevel.Warning, "Agent", "lockAll failed " + ex.GetType().Name);
                }
            }
        }

        public void SetTimeout(int minutes)
        {
            try
            {
                _agent.SetTimeout(minutes < 0 ? 0 : minutes);
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.Log(LogLevel.Warning, "Agent", "setTimeout failed " + ex.GetType().Name);
                }
            }
        }

        public int TimeoutMinutes
        {
            get
            {
                try
                {
                    return _agent.TimeoutMinutes;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public bool IsLocked(string keyId)
        {
            try
            {
                return _agent.IsLocked(keyId);
            }
            catch (Exception)
            {
                return true;
            }
        }

        public int KeyCount
        {
            get
            {
                try
                {
                    return _agent.KeyCount;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        // 只记相位与耗时：LogRedactor 会二次兜底，但本层消息本来就不含敏感材料。
        private void Log(string method, string outcome, Stopwatch watch)
        {
            ILogger logger = _logger;
            if (logger == null)
            {
                return;
            }
            watch.Stop();
            logger.Log(LogLevel.Debug, "Agent",
                method + " " + outcome + " " + watch.ElapsedMilliseconds + "ms");
        }
    }
}
