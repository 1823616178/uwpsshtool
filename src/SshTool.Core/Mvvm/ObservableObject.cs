using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SshTool.Core.Mvvm
{
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // D06/U*：后台线程改属性时把 PropertyChanged 封送回 UI 线程，避免
        // XAML/Win2D 的 STA 对象（如 StatusDot 的 Ellipse、CanvasControl）出现
        // RPC_E_WRONG_THREAD（0x8001010E）。字段同步更新（轮询立即可见），
        // 只有通知走 dispatcher；未设置时保持同步直发（ViewModel/UI 线程路径）。
        private Action<Action> _dispatcherPost;

        public void SetDispatcherPost(Action<Action> post)
        {
            _dispatcherPost = post;
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }
            field = value;
            RaiseOnUi(propertyName);
            return true;
        }

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            RaiseOnUi(propertyName);
        }

        private void RaiseOnUi(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler == null)
            {
                return;
            }
            Action<Action> post = _dispatcherPost;
            if (post == null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
                return;
            }
            try
            {
                post(() => handler(this, new PropertyChangedEventArgs(propertyName)));
            }
            catch (Exception)
            {
                // 封送失败时退化为直发，避免状态永久不可见；调用方仍负责线程安全。
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
