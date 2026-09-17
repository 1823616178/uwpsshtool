using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SshTool.Core.Mvvm
{
    // 异步命令：执行中禁用（IsRunning 防重入），异常交给注入的处理器（记日志/弹窗由调用方决定）。
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private readonly Action<Exception> _onError;

        public AsyncCommand(Func<Task> execute, Func<bool> canExecute = null, Action<Exception> onError = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onError = onError;
        }

        public event EventHandler CanExecuteChanged;

        public bool IsRunning { get; private set; }

        public bool CanExecute(object parameter)
        {
            return !IsRunning && (_canExecute == null || _canExecute());
        }

        public async void Execute(object parameter)
        {
            if (IsRunning)
            {
                return;
            }
            IsRunning = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
            finally
            {
                IsRunning = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
