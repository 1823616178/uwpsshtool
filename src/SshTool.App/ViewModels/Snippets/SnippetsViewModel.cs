using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;

namespace SshTool.App.ViewModels.Snippets
{
    // U13：片段管理页 ViewModel（02-UI-DESIGN.md §5.9）。
    // 可选绑定一个会话（SnippetsArgs.SessionId）：有会话时行内「发送」可用，
    // 无会话时发送入口禁用，只做管理（新建/编辑/删除）。
    public sealed class SnippetsViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private IReadOnlyList<Snippet> _all = new Snippet[0];
        private string _search = string.Empty;
        private SessionInfo _session;

        public SnippetsViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _services = services;
            Groups = new ObservableCollection<SnippetGroupVm>();
            NewCommand = new RelayCommand(OpenNew);
            RefreshAsync().Forget("SnippetsViewModel.Refresh", AppLog.Logger);
        }

        public ObservableCollection<SnippetGroupVm> Groups { get; private set; }

        public ICommand NewCommand { get; private set; }

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

        public bool IsEmpty
        {
            get { return Groups.Count == 0 && _search.Trim().Length == 0; }
        }

        public bool HasNoMatches
        {
            get { return Groups.Count == 0 && _search.Trim().Length > 0; }
        }

        public bool CanSend
        {
            get { return _session != null && _session.NativeSession != null; }
        }

        public SessionInfo Session
        {
            get { return _session; }
        }

        public async Task RefreshAsync()
        {
            _all = await _services.Snippets.GetAllAsync().ConfigureAwait(true);
            ApplyFilter();
        }

        // SnippetsPage 导航进入时调用：解析可选的会话上下文。
        public void AttachSession(string sessionId)
        {
            _session = null;
            if (!string.IsNullOrEmpty(sessionId) && _services.Sessions != null)
            {
                IReadOnlyList<SessionInfo> all = _services.Sessions.Sessions;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].SessionId == sessionId)
                    {
                        _session = all[i];
                        break;
                    }
                }
            }
            RaisePropertyChanged("CanSend");
            ApplyFilter();
        }

        public void OpenNew()
        {
            Navigation.Navigate<SnippetEditPage>(SnippetEditArgs.New());
        }

        public void OpenEdit(SnippetRowVm row)
        {
            if (row == null)
            {
                return;
            }
            Navigation.Navigate<SnippetEditPage>(SnippetEditArgs.Edit(row.Id));
        }

        public async Task SendAsync(SnippetRowVm row)
        {
            if (row == null || row.Source == null || !CanSend)
            {
                return;
            }
            await SnippetSendHelper.SendWithPromptsAsync(
                row.Source, _session, Logger).ConfigureAwait(true);
        }

        public async Task DeleteAsync(SnippetRowVm row)
        {
            if (row == null)
            {
                return;
            }
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                "删除片段？",
                "删除「" + (row.Name ?? string.Empty) + "」后无法恢复。",
                "删除", "取消", true).ConfigureAwait(true);
            if (!confirm.Confirmed)
            {
                return;
            }
            await _services.Snippets.RemoveAsync(row.Id, ChangeOrigin.User).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }

        private void ApplyFilter()
        {
            List<SnippetGroupVm> grouped = SnippetGrouping.Group(_all, _search);
            bool canSend = CanSend;
            for (int g = 0; g < grouped.Count; g++)
            {
                for (int r = 0; r < grouped[g].Rows.Count; r++)
                {
                    grouped[g].Rows[r].SendEnabled = canSend;
                }
            }
            Groups.Clear();
            for (int i = 0; i < grouped.Count; i++)
            {
                Groups.Add(grouped[i]);
            }
            RaisePropertyChanged("IsEmpty");
            RaisePropertyChanged("HasNoMatches");
        }
    }
}
