using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Vault;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.ViewModels.Sync
{
    // U19 同步冲突页 ViewModel（02-UI-DESIGN.md §5.14）。
    // 读取 SyncCoordinator 的当前冲突摘要（SyncState.Conflict），经 ConflictPresenter 解析
    // 标题/说明/按钮文案与字段列表（敏感字段显示「有变更」）。按钮调用 ResolveConflict。
    // 脱敏：字段值绝不进入 VM；仅展示实体名、字段名与是否敏感。
    public sealed class SyncConflictViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private readonly ConflictPresenter _presenter;
        private readonly ResourceLoader _loader;
        private readonly IEntityNameLookup _lookup;

        private SyncConflictSummary _conflict;
        private string _errorMessage = string.Empty;
        private bool _isResolving;

        public SyncConflictViewModel(SyncCoordinator sync, IEntityNameLookup lookup)
        {
            _sync = sync;
            _lookup = lookup;
            _presenter = new ConflictPresenter();
            _loader = ResourceLoader.GetForCurrentView();
            SetDispatcherPost(action => DispatcherHelper.Post(action));

            KeepLocalCommand = new AsyncCommand(
                () => ResolveAsync(SyncNowStrategy.KeepLocal),
                () => CanResolve(), OnCommandError);
            UseRemoteCommand = new AsyncCommand(
                () => ResolveAsync(SyncNowStrategy.UseRemote),
                () => CanResolve(), OnCommandError);

            if (sync != null)
            {
                _conflict = sync.State.Conflict;
            }
            RefreshAll();
        }

        public AsyncCommand KeepLocalCommand { get; private set; }
        public AsyncCommand UseRemoteCommand { get; private set; }

        // ---- 远端摘要 ----

        public bool HasConflict
        {
            get { return _conflict != null; }
        }

        public string Title
        {
            get { return ResolveText(_presenter.ResolveTitle(Reason)); }
        }

        public string Description
        {
            get { return ResolveText(_presenter.ResolveDescription(Reason)); }
        }

        public string PrimaryButtonText
        {
            get { return ResolveText(_presenter.ResolvePrimaryButton(Reason)); }
        }

        public string SecondaryButtonText
        {
            get { return ResolveText(_presenter.ResolveSecondaryButton(Reason)); }
        }

        public SyncConflictReason Reason
        {
            get { return _conflict == null ? SyncConflictReason.MergeConflict : _conflict.Reason; }
        }

        public string RemoteSummaryText
        {
            get
            {
                if (_conflict == null || _conflict.RemoteSummary == null)
                {
                    return string.Empty;
                }
                var s = _conflict.RemoteSummary;
                string baseText = GetString("Conflict_RemoteSummary",
                    "云端：{0} 台主机 · {1} 条隧道 · {2} 分组",
                    s.Servers.ToString(), s.Tunnels.ToString(), s.Groups.ToString());
                var parts = new List<string>();
                if (s.IncludesPasswords)
                {
                    parts.Add(GetString("Conflict_IncludesPasswords", "含已同步密码"));
                }
                if (s.IncludesPrivateKeys)
                {
                    parts.Add(GetString("Conflict_IncludesPrivateKeys", "含已同步私钥"));
                }
                if (parts.Count == 0)
                {
                    return baseText;
                }
                return baseText + " · " + string.Join(" · ", parts);
            }
        }

        public string RemoteUpdatedAt
        {
            get
            {
                if (_conflict == null)
                {
                    return string.Empty;
                }
                return _conflict.RemoteUpdatedAt ?? string.Empty;
            }
        }

        public string LocalUpdatedAt
        {
            get
            {
                if (_conflict == null)
                {
                    return string.Empty;
                }
                return _conflict.LocalUpdatedAt ?? string.Empty;
            }
        }

        // ---- 字段列表（脱敏） ----

        public List<ConflictFieldSpec> Fields
        {
            get
            {
                return _presenter.FieldDisplayNames(
                    _conflict == null ? null : _conflict.Fields, _lookup);
            }
        }

        public bool HasFields
        {
            get
            {
                var fields = _conflict == null ? null : _conflict.Fields;
                return fields != null && fields.Count > 0;
            }
        }

        // ---- 状态 ----

        public bool IsResolving
        {
            get { return _isResolving; }
            private set { SetProperty(ref _isResolving, value); }
        }

        public string ErrorMessage
        {
            get { return _errorMessage; }
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    RaisePropertyChanged("HasError");
                }
            }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorMessage); }
        }

        private bool CanResolve()
        {
            return !_isResolving && HasConflict;
        }

        private async Task ResolveAsync(SyncNowStrategy strategy)
        {
            if (_sync == null)
            {
                return;
            }
            ErrorMessage = null;
            IsResolving = true;
            try
            {
                await _sync.ResolveConflictAsync(strategy).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ErrorMessage = GetString("Conflict_ResolveFailed", "解决冲突失败，请稍后重试");
                LogWarning("解决冲突失败 " + ex.GetType().Name);
            }
            finally
            {
                IsResolving = false;
                RaisePropertyChanged("IsResolving");
            }
        }

        private void RefreshAll()
        {
            RaisePropertyChanged("HasConflict");
            RaisePropertyChanged("Title");
            RaisePropertyChanged("Description");
            RaisePropertyChanged("PrimaryButtonText");
            RaisePropertyChanged("SecondaryButtonText");
            RaisePropertyChanged("RemoteSummaryText");
            RaisePropertyChanged("RemoteUpdatedAt");
            RaisePropertyChanged("LocalUpdatedAt");
            RaisePropertyChanged("Fields");
            RaisePropertyChanged("HasFields");
            KeepLocalCommand.RaiseCanExecuteChanged();
            UseRemoteCommand.RaiseCanExecuteChanged();
        }

        private string ResolveText(string key)
        {
            return GetString(key, key);
        }

        private string GetString(string key, string fallback, params object[] args)
        {
            try
            {
                string value = _loader.GetString(key);
                if (string.IsNullOrEmpty(value))
                {
                    value = fallback;
                }
                if (args != null && args.Length > 0)
                {
                    return string.Format(value, args);
                }
                return value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private void OnCommandError(Exception ex)
        {
            ErrorMessage = GetString("Conflict_ResolveFailed", "解决冲突失败，请稍后重试");
            LogWarning("解决冲突异常 " + ex.GetType().Name);
        }

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "SyncConflict", message);
            }
            catch (Exception)
            {
            }
        }
    }
}
