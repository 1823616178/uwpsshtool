using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Models;

namespace SshTool.App.ViewModels.Snippets
{
    // U13：终端内片段选择器 ViewModel（SnippetPickerFlyout 用）。
    // 只读：搜索 + 按分组展示，点击即发送（待填变量弹窗由 SnippetSendHelper 拉起）。
    public sealed class SnippetPickerViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private IReadOnlyList<Snippet> _all = new Snippet[0];
        private string _search = string.Empty;

        public SnippetPickerViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _services = services;
            Groups = new ObservableCollection<SnippetGroupVm>();
            RefreshAsync().Forget("SnippetPickerViewModel.Refresh", AppLog.Logger);
        }

        public ObservableCollection<SnippetGroupVm> Groups { get; private set; }

        public string Search
        {
            get { return _search; }
            set
            {
                if (SetProperty(ref _search, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public bool HasNoMatches
        {
            get { return Groups.Count == 0; }
        }

        public async Task RefreshAsync()
        {
            _all = await _services.Snippets.GetAllAsync().ConfigureAwait(true);
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            List<SnippetGroupVm> grouped = SnippetGrouping.Group(_all, _search);
            Groups.Clear();
            for (int i = 0; i < grouped.Count; i++)
            {
                Groups.Add(grouped[i]);
            }
            RaisePropertyChanged("HasNoMatches");
        }
    }
}
