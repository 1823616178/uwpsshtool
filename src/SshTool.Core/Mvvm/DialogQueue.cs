using System.Collections.Generic;

namespace SshTool.Core.Mvvm
{
    // W10M 同一时刻只能显示一个 ContentDialog：纯逻辑队列（02-UI-DESIGN.md §4 前置）。
    // 入队得 id；Current 为可显示者；CompleteCurrent 出队并提拔下一个；Cancel 取消指定 id。
    public sealed class DialogQueue
    {
        private readonly Queue<string> _pending = new Queue<string>();

        public string Current { get; private set; }

        public int PendingCount
        {
            get { return _pending.Count; }
        }

        public string Enqueue(string id)
        {
            if (Current == null)
            {
                Current = id;
            }
            else
            {
                _pending.Enqueue(id);
            }
            return id;
        }

        // 当前对话框完成：提拔下一个排队者（无则 null），返回新的 Current。
        public string CompleteCurrent()
        {
            Current = _pending.Count > 0 ? _pending.Dequeue() : null;
            return Current;
        }

        // 取消：排队中则移除；若是当前显示者则等同于 CompleteCurrent。返回是否取消了某项。
        public bool Cancel(string id)
        {
            if (Current == id)
            {
                CompleteCurrent();
                return true;
            }
            if (_pending.Contains(id))
            {
                var kept = new Queue<string>();
                foreach (var item in _pending)
                {
                    if (item != id)
                    {
                        kept.Enqueue(item);
                    }
                }
                _pending.Clear();
                foreach (var item in kept)
                {
                    _pending.Enqueue(item);
                }
                return true;
            }
            return false;
        }
    }
}
