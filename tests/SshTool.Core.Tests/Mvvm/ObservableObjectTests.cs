using System;
using System.ComponentModel;
using SshTool.Core.Mvvm;
using Xunit;

namespace SshTool.Core.Tests.Mvvm
{
    public class ObservableObjectTests
    {
        private sealed class Vm : ObservableObject
        {
            private string _name;
            public string Name
            {
                get { return _name; }
                set { SetProperty(ref _name, value); }
            }
        }

        [Fact]
        public void SetProperty_RaisesPropertyChanged()
        {
            var vm = new Vm();
            string raised = null;
            vm.PropertyChanged += (s, e) => raised = e.PropertyName;
            vm.Name = "kim";
            Assert.Equal("kim", vm.Name);
            Assert.Equal("Name", raised);
        }

        [Fact]
        public void SetProperty_SameValue_NoRaise()
        {
            var vm = new Vm { Name = "a" };
            PropertyChangedEventHandler h = (s, e) => Assert.True(false, "不应触发");
            vm.PropertyChanged += h;
            vm.Name = "a";
            vm.PropertyChanged -= h;
        }

        [Fact]
        public void SetProperty_WithDispatcherPost_FieldSyncNotifyViaPost()
        {
            var vm = new Vm();
            int postCount = 0;
            vm.SetDispatcherPost(a => { postCount++; a(); });
            string raised = null;
            vm.PropertyChanged += (s, e) => raised = e.PropertyName;
            vm.Name = "kim";
            // 字段同步更新，通知走 dispatcher（后台线程改 UI 绑定属性时不直接跨线程抛 0x8001010E）
            Assert.Equal("kim", vm.Name);
            Assert.Equal(1, postCount);
            Assert.Equal("Name", raised);
        }

        [Fact]
        public void SetProperty_WithoutDispatcher_SyncRaise()
        {
            var vm = new Vm();
            string raised = null;
            vm.PropertyChanged += (s, e) => raised = e.PropertyName;
            vm.Name = "x";
            Assert.Equal("Name", raised);
        }
    }
}
