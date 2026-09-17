using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Mvvm;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Infrastructure
{
    // W10M 同一时刻只能显示一个 ContentDialog：基于 Core 的 DialogQueue 串行化。
    public sealed class DialogService
    {
        private readonly object _gate = new object();
        private readonly DialogQueue _queue = new DialogQueue();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _turns =
            new Dictionary<string, TaskCompletionSource<bool>>();

        public bool IsDialogOpen
        {
            get { lock (_gate) { return _queue.Current != null; } }
        }

        public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
        {
            var id = Guid.NewGuid().ToString("N");
            var turn = new TaskCompletionSource<bool>();
            lock (_gate)
            {
                _turns[id] = turn;
                _queue.Enqueue(id);
                if (_queue.Current == id)
                {
                    turn.TrySetResult(true);
                }
            }
            await turn.Task;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                Release(id);
            }
        }

        // 取消某个还未显示的请求（如页面已销毁）；当前显示中的请直接关闭对话框。
        public bool Cancel(string id)
        {
            TaskCompletionSource<bool> turn = null;
            bool cancelled = false;
            lock (_gate)
            {
                if (_queue.Current != id && _queue.Cancel(id))
                {
                    cancelled = _turns.TryGetValue(id, out turn);
                    _turns.Remove(id);
                }
            }
            if (turn != null)
            {
                turn.TrySetCanceled();
            }
            return cancelled;
        }

        private void Release(string id)
        {
            string next = null;
            TaskCompletionSource<bool> nextTurn = null;
            lock (_gate)
            {
                _turns.Remove(id);
                next = _queue.CompleteCurrent();
                if (next != null && _turns.TryGetValue(next, out nextTurn))
                {
                    _turns.Remove(next);
                }
            }
            nextTurn?.TrySetResult(true);
        }
    }
}
