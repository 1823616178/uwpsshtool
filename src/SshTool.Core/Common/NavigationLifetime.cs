using System;
using System.Collections.Generic;
using System.Threading;

namespace SshTool.Core.Common
{
    // R01 (C-02)：页面导航世代生命周期。OnNavigatedTo 调 Begin 开新世代（旧异步链随之
    // 失效），OnNavigatedFrom 调 End：取消 Token、按注册反序执行 Track 的拆除动作并清空。
    // End 幂等（重复 GoBack / Frame 重复回调下安全）；Begin 自带一次 End，重复进入安全。
    public sealed class NavigationLifetime
    {
        private readonly List<Action> _teardowns = new List<Action>();
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private int _generation;
        // 初始即「未在世代中」：首次 Begin 前 IsCurrent 恒 false，End 为空操作。
        private bool _ended = true;

        public int Begin()
        {
            End();
            _cts = new CancellationTokenSource();
            _ended = false;
            _generation++;
            return _generation;
        }

        public void End()
        {
            if (_ended)
            {
                return;
            }
            _ended = true;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            // 反序：后订阅的先拆，与「成对资源后申请先释放」一致。
            for (int i = _teardowns.Count - 1; i >= 0; i--)
            {
                try
                {
                    _teardowns[i]();
                }
                catch (Exception)
                {
                    // 单个拆除失败不拖垮其余（解订阅本身应幂等，异常只可能来自页面自登记动作）。
                }
            }
            _teardowns.Clear();
        }

        public bool IsCurrent(int generation)
        {
            return !_ended && generation == _generation;
        }

        public CancellationToken Token
        {
            get { return _cts.Token; }
        }

        // 世代结束后才登记的动作立即执行：绝不留下永不拆除的订阅。
        public void Track(Action teardown)
        {
            if (teardown == null)
            {
                return;
            }
            if (_ended)
            {
                teardown();
                return;
            }
            _teardowns.Add(teardown);
        }
    }
}
