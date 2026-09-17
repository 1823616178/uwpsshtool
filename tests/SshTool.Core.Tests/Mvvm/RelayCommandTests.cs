using SshTool.Core.Mvvm;
using Xunit;

namespace SshTool.Core.Tests.Mvvm
{
    public class RelayCommandTests
    {
        [Fact]
        public void Execute_InvokesAction()
        {
            int calls = 0;
            var cmd = new RelayCommand(() => calls++);
            cmd.Execute(null);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void CanExecute_ReflectsPredicate()
        {
            bool gate = false;
            var cmd = new RelayCommand(() => { }, () => gate);
            Assert.False(cmd.CanExecute(null));
            gate = true;
            Assert.True(cmd.CanExecute(null));
        }

        [Fact]
        public void RaiseCanExecuteChanged_FiresEvent()
        {
            var cmd = new RelayCommand(() => { });
            bool fired = false;
            cmd.CanExecuteChanged += (s, e) => fired = true;
            cmd.RaiseCanExecuteChanged();
            Assert.True(fired);
        }

        [Fact]
        public void Generic_PassesParameter()
        {
            string got = null;
            var cmd = new RelayCommand<string>(p => got = p);
            cmd.Execute("host-1");
            Assert.Equal("host-1", got);
        }
    }
}
