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
    }
}
