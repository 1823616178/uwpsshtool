using System;
using System.Collections.Generic;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Infrastructure
{
    // 02-UI-DESIGN.md §4：硬件返回键优先级 1-5 由 IBackHandler 链承担（弹层 → 页面），
    // 6 为 Frame.CanGoBack，7 交给系统。后注册的处理器优先（栈式）。
    public sealed class NavigationService
    {
        private readonly List<IBackHandler> _backHandlers = new List<IBackHandler>();
        private Frame _frame;

        public void Initialize(Frame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }
            // 幂等：重复 Initialize（如 OnLaunched 再次进入）不能重复订阅，否则一次返回被处理两次
            if (_frame == null)
            {
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            }
            _frame = frame;
        }

        public bool Navigate<TPage>(object parameter = null)
        {
            return _frame.Navigate(typeof(TPage), parameter);
        }

        public bool CanGoBack
        {
            get { return _frame != null && _frame.CanGoBack; }
        }

        public void GoBack()
        {
            if (CanGoBack)
            {
                _frame.GoBack();
            }
        }

        public void RegisterBackHandler(IBackHandler handler)
        {
            if (handler != null && !_backHandlers.Contains(handler))
            {
                _backHandlers.Add(handler);
            }
        }

        public void UnregisterBackHandler(IBackHandler handler)
        {
            _backHandlers.Remove(handler);
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }
            for (int i = _backHandlers.Count - 1; i >= 0; i--)
            {
                if (_backHandlers[i].HandleBack())
                {
                    e.Handled = true;
                    return;
                }
            }
            if (CanGoBack)
            {
                _frame.GoBack();
                e.Handled = true;
            }
            // 否则 e.Handled 保持 false，交给系统（退出应用；§4-7 的退出确认由 MainPage 的处理器负责）
        }
    }
}
