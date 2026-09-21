using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using SshTool.Core.Sftp;
using SshTool.Core.Mvvm;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;

namespace SshTool.App.ViewModels
{
    public sealed class SftpArgs
    {
        // 终端菜单入口：绑定该会话的连接；主机菜单入口：复用该主机已连接（有 shell）
        // 的会话，没有则新建一条无 shell 的专用连接（01-DESIGN.md §11.1）。
        public string SessionId { get; set; }
        public string HostId { get; set; }
    }

    // 文件列表行（ListView 数据模板；Entry 为 Core RemoteEntry 原件）。
    // Glyph 是 Token 键值（§2.3 图标），XAML 里 FontIcon.Glyph 绑定它。
    // 行对象在目录刷新时按「目录+文件名」复用（SftpViewModel.RebuildRows），
    // 故为 INPC：只推变化的展示字段，避免整表重建的闪动。
    public sealed class SftpRowVm : ObservableObject
    {
        private static readonly ResourceLoader RowLoader = ResourceLoader.GetForCurrentView();

        private RemoteEntry _entry;
        private string _name;
        private string _sizeText;
        private string _mtimeText;
        private string _linkText;

        public SftpRowVm(RemoteEntry entry, string sizeText, string mtimeText, string linkText)
        {
            _entry = entry;
            _name = entry != null ? entry.Name : string.Empty;
            _sizeText = sizeText;
            _mtimeText = mtimeText;
            _linkText = linkText;
        }

        public RemoteEntry Entry
        {
            get { return _entry; }
        }

        public string Name
        {
            get { return _name; }
        }

        public string SizeText
        {
            get { return _sizeText; }
        }

        public string MtimeText
        {
            get { return _mtimeText; }
        }

        public string LinkText
        {
            get { return _linkText; }
        }

        public string Glyph
        {
            get
            {
                object token = Entry != null && Entry.IsDirectory && !Entry.IsSymlink
                    ? Application.Current.Resources["IconFolder"]
                    : Application.Current.Resources["IconDocument"];
                return token as string ?? string.Empty;
            }
        }

        public bool IsDirectory
        {
            get { return Entry != null && Entry.IsDirectory && !Entry.IsSymlink; }
        }

        // §6.5：AppListRow 副标题。符号链接显示目标路径；普通条目显示「大小 · 修改时间」。
        public string SubtitleText
        {
            get
            {
                if (!string.IsNullOrEmpty(_linkText))
                {
                    return _linkText;
                }
                var parts = new List<string>(2);
                if (!string.IsNullOrEmpty(_sizeText))
                {
                    parts.Add(_sizeText);
                }
                if (!string.IsNullOrEmpty(_mtimeText))
                {
                    parts.Add(_mtimeText);
                }
                return string.Join(" · ", parts);
            }
        }

        // §6.5：行尾「更多」按钮的无障碍名（x:Uid 不适用于模板实例，故由行提供）。
        public string MoreButtonName
        {
            get { return RowLoader.GetString("Sftp_RowMoreButton_A11yName"); }
        }

        // 复用行对象刷新：字段值不变就不发通知（SetProperty 内部比对）。
        public void Update(RemoteEntry entry, string sizeText, string mtimeText, string linkText)
        {
            if (!ReferenceEquals(entry, _entry))
            {
                _entry = entry;
                RaisePropertyChanged("Entry");
                RaisePropertyChanged("Glyph");
                RaisePropertyChanged("IsDirectory");
            }
            SetProperty(ref _name, entry != null ? entry.Name : string.Empty);
            SetProperty(ref _sizeText, sizeText);
            SetProperty(ref _mtimeText, mtimeText);
            SetProperty(ref _linkText, linkText);
            RaisePropertyChanged("SubtitleText");
        }
    }

    // 传输面板行（§5.17：名称、方向、进度、速率、[取消]/[重试]）。
    // TransferItem 是无通知的可变对象：ItemChanged 到达（已封送 UI 线程）时整体刷新。
    public sealed class TransferRowVm : ObservableObject
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

        private string _name = string.Empty;
        private string _stateText = string.Empty;
        private string _rateText = string.Empty;
        private double _percent = -1;
        private bool _isRunning;
        private bool _canRetry;
        private string _errorText = string.Empty;
        private bool _isUpload;

        private static readonly string _uploadText = Loader.GetString("Sftp_TransferUpload");
        private static readonly string _downloadText = Loader.GetString("Sftp_TransferDownload");

        public string Id { get; private set; }

        public string Name
        {
            get { return _name; }
            private set { SetProperty(ref _name, value); }
        }

        public string DirectionText
        {
            get { return _isUpload ? _uploadText : _downloadText; }
        }

        public string StateText
        {
            get { return _stateText; }
            private set { SetProperty(ref _stateText, value); }
        }

        public string RateText
        {
            get { return _rateText; }
            private set { SetProperty(ref _rateText, value); }
        }

        // 总量未知时为 -1：不画进度条，只显示字节。
        public double Percent
        {
            get { return _percent; }
            private set { SetProperty(ref _percent, value); }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            private set { SetProperty(ref _isRunning, value); }
        }

        public bool CanRetry
        {
            get { return _canRetry; }
            private set { SetProperty(ref _canRetry, value); }
        }

        public string ErrorText
        {
            get { return _errorText; }
            private set { SetProperty(ref _errorText, value); }
        }

        // Token 图标键值：方向图标（§2.3）。
        public string Glyph
        {
            get
            {
                object token = _isUpload
                    ? Application.Current.Resources["IconUpload"]
                    : Application.Current.Resources["IconDownload"];
                return token as string ?? string.Empty;
            }
        }

        // 数据模板内的按钮文案（x:Uid 不适用于模板实例，故由行提供）。
        public string CancelText
        {
            get { return Loader.GetString("Sftp_Cancel"); }
        }

        public string RetryText
        {
            get { return Loader.GetString("Sftp_RetryTransfer"); }
        }

        public void UpdateFrom(TransferItem item)
        {
            if (item == null)
            {
                return;
            }
            Id = item.Id;
            bool wasUpload = _isUpload;
            _isUpload = item.Direction == TransferDirection.Upload;
            if (wasUpload != _isUpload)
            {
                RaisePropertyChanged("DirectionText");
                RaisePropertyChanged("Glyph");
            }
            Name = item.LocalFileName;
            StateText = Loader.GetString(StateKey(item.State));
            Percent = item.TotalBytes > 0
                ? Clamp(item.BytesDone * 100.0 / item.TotalBytes)
                : -1;
            RateText = item.State == TransferState.Running && item.BytesPerSecond > 1
                ? SftpListing.FormatSize((long)item.BytesPerSecond) + "/s"
                : string.Empty;
            IsRunning = item.State == TransferState.Queued || item.State == TransferState.Running;
            CanRetry = item.State == TransferState.Failed || item.State == TransferState.Cancelled;
            // 错误文案统一走 SftpViewModel.ErrorCodeText（静态共用入口）：这里原先直接
            // Loader.GetString("Error_N")，未登记码值时空串/键名上屏，用户看到「失败」却没原因。
            ErrorText = item.State == TransferState.Failed
                ? SftpViewModel.ErrorCodeText(item.ErrorCode)
                : string.Empty;
        }

        private static double Clamp(double value)
        {
            if (value < 0)
            {
                return 0;
            }
            return value > 100 ? 100 : value;
        }

        private static string StateKey(TransferState state)
        {
            switch (state)
            {
                case TransferState.Running:
                    return "Sftp_StateRunning";
                case TransferState.Completed:
                    return "Sftp_StateCompleted";
                case TransferState.Failed:
                    return "Sftp_StateFailed";
                case TransferState.Cancelled:
                    return "Sftp_StateCancelled";
                default:
                    return "Sftp_StateQueued";
            }
        }
    }

    public sealed class SftpViewModel : ViewModelBase
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

        private readonly SftpArgs _args;
        private readonly ObservableCollection<SftpRowVm> _rows;
        private readonly ObservableCollection<SftpBreadcrumb> _breadcrumbs;
        private readonly ObservableCollection<TransferRowVm> _transferRows;
        private readonly Dictionary<string, TransferRowVm> _transferById;

        // 行复用索引：_rows 所属目录 + 「条目路径（目录+文件名）→ 行 VM」。
        private string _rowsPath;
        private Dictionary<string, SftpRowVm> _rowsByPath =
            new Dictionary<string, SftpRowVm>(StringComparer.Ordinal);

        private ISftpClient _client;
        private NativeSshSession _clientNative;
        private SessionInfo _session;
        private bool _ownsSession;
        private TransferQueue _queue;
        private string _currentPath = RemotePath.Root;
        private string _title = string.Empty;
        private SftpSortMode _sortMode = SftpSortMode.Name;
        private bool _showHidden;
        private bool _isBusy;
        private bool _hasError;
        private string _statusText = string.Empty;
        private bool _transfersExpanded;
        private bool _connectStarted;
        private bool _leaving;
        private IReadOnlyList<RemoteEntry> _entries = new List<RemoteEntry>();
        private int _activeTransfers;

        // 轻提示（复制/删除/重命名完成等）；页面订阅后经 TransientToast 显示。
        public event EventHandler<string> NotifyRequested;

        public SftpViewModel(SftpArgs args)
        {
            _args = args ?? new SftpArgs();
            _rows = new ObservableCollection<SftpRowVm>();
            _breadcrumbs = new ObservableCollection<SftpBreadcrumb>();
            _transferRows = new ObservableCollection<TransferRowVm>();
            _transferById = new Dictionary<string, TransferRowVm>(StringComparer.Ordinal);

            // R03 (C-05)：无参命令改 AsyncCommand——执行中 CanExecute=false 防重入，异常走 onError 记日志。
            RefreshCommand = new AsyncCommand(() => RefreshAsync(),
                onError: ex => AppLog.Error("Sftp.Refresh", "refresh", ex));
            UpCommand = new RelayCommand(GoUp, () => !IsBusy && _currentPath != RemotePath.Root);
            NavigateCommand = new RelayCommand<SftpBreadcrumb>(NavigateBreadcrumb);
            EnterRowCommand = new RelayCommand<SftpRowVm>(EnterRow);
            // 行命令带参数（AsyncCommand 无泛型版），改用 .Forget(context, logger) 统一观察异常。
            DownloadRowCommand = new RelayCommand<SftpRowVm>(
                row => DownloadRow(row).Forget("Sftp.Download", AppLog.Logger));
            RenameRowCommand = new RelayCommand<SftpRowVm>(
                row => RenameRow(row).Forget("Sftp.Rename", AppLog.Logger));
            PermissionsRowCommand = new RelayCommand<SftpRowVm>(
                row => PermissionsRow(row).Forget("Sftp.Permissions", AppLog.Logger));
            DeleteRowCommand = new RelayCommand<SftpRowVm>(
                row => DeleteRow(row).Forget("Sftp.Delete", AppLog.Logger));
            CopyPathCommand = new RelayCommand<SftpRowVm>(CopyPath);
            UploadCommand = new AsyncCommand(() => UploadAsync(),
                onError: ex => AppLog.Error("Sftp.Upload", "upload", ex));
            NewFolderCommand = new AsyncCommand(() => NewFolderAsync(),
                onError: ex => AppLog.Error("Sftp.NewFolder", "create folder", ex));
            SetSortCommand = new RelayCommand<SftpSortMode>(SetSort);
            ToggleHiddenCommand = new RelayCommand(ToggleHidden);
            ToggleTransfersCommand = new RelayCommand(() => TransfersExpanded = !TransfersExpanded);
            CancelTransferCommand = new RelayCommand<TransferRowVm>(CancelTransfer);
            RetryTransferCommand = new RelayCommand<TransferRowVm>(RetryTransfer);
            ClearFinishedCommand = new RelayCommand(ClearFinished);
        }

        public SftpViewModel()
            : this(null)
        {
        }

        public ObservableCollection<SftpRowVm> Rows
        {
            get { return _rows; }
        }

        public ObservableCollection<SftpBreadcrumb> Breadcrumbs
        {
            get { return _breadcrumbs; }
        }

        public ObservableCollection<TransferRowVm> TransferRows
        {
            get { return _transferRows; }
        }

        public ICommand RefreshCommand { get; private set; }
        public ICommand UpCommand { get; private set; }
        public ICommand NavigateCommand { get; private set; }
        public ICommand EnterRowCommand { get; private set; }
        public ICommand DownloadRowCommand { get; private set; }
        public ICommand RenameRowCommand { get; private set; }
        public ICommand PermissionsRowCommand { get; private set; }
        public ICommand DeleteRowCommand { get; private set; }
        public ICommand CopyPathCommand { get; private set; }
        public ICommand UploadCommand { get; private set; }
        public ICommand NewFolderCommand { get; private set; }
        public ICommand SetSortCommand { get; private set; }
        public ICommand ToggleHiddenCommand { get; private set; }
        public ICommand ToggleTransfersCommand { get; private set; }
        public ICommand CancelTransferCommand { get; private set; }
        public ICommand RetryTransferCommand { get; private set; }
        public ICommand ClearFinishedCommand { get; private set; }

        public string Title
        {
            get { return _title; }
            private set { SetProperty(ref _title, value); }
        }

        public SftpSortMode SortMode
        {
            get { return _sortMode; }
            set
            {
                if (SetProperty(ref _sortMode, value))
                {
                    RebuildRows();
                }
            }
        }

        public bool ShowHidden
        {
            get { return _showHidden; }
            set
            {
                if (SetProperty(ref _showHidden, value))
                {
                    RebuildRows();
                }
            }
        }

        public string CurrentPath
        {
            get { return _currentPath; }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set { SetProperty(ref _isBusy, value); }
        }

        public string BusyMessage
        {
            get { return Loader.GetString("Sftp_Connecting"); }
        }

        public bool HasError
        {
            get { return _hasError; }
            private set { SetProperty(ref _hasError, value); }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set { SetProperty(ref _statusText, value); }
        }

        public bool IsEmpty
        {
            get { return !HasError && _rows.Count == 0; }
        }

        public bool TransfersExpanded
        {
            get { return _transfersExpanded; }
            set { SetProperty(ref _transfersExpanded, value); }
        }

        public int ActiveTransferCount
        {
            get { return _activeTransfers; }
            private set
            {
                if (SetProperty(ref _activeTransfers, value))
                {
                    RaisePropertyChanged("TransferHeader");
                    RaisePropertyChanged("HasActiveTransfers");
                }
            }
        }

        public string TransferHeader
        {
            get
            {
                return string.Format(CultureInfo.CurrentCulture,
                    Loader.GetString("Sftp_TransferPanelTitle"),
                    ActiveTransferCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        public bool HasActiveTransfers
        {
            get { return ActiveTransferCount > 0; }
        }

        // 面板条显示条件：有活动传输或面板里有行（含刚完成的）。页面按此折叠整条面板。
        public bool ShowTransferBar
        {
            get { return _showTransferBar; }
            private set { SetProperty(ref _showTransferBar, value); }
        }

        private bool _showTransferBar;

        // ---- 连接与会话 ----

        public async Task LoadAsync()
        {
            if (_connectStarted)
            {
                return;
            }
            _connectStarted = true;
            SessionManager manager = AppServices.Current != null ? AppServices.Current.Sessions : null;
            if (manager == null || (_args.SessionId == null && _args.HostId == null))
            {
                ShowError(ErrorCodeText(SshErrorCode.InternalError));
                return;
            }
            IsBusy = true;
            try
            {
                SessionInfo info = null;
                _ownsSession = false;
                if (!string.IsNullOrEmpty(_args.SessionId))
                {
                    info = FindSession(manager, _args.SessionId);
                    if (info == null)
                    {
                        ShowError(Loader.GetString("Sftp_ErrSessionGone"));
                        return;
                    }
                    if (info.State != SessionUiState.Connected)
                    {
                        ShowError(Loader.GetString("Sftp_ErrNotConnected"));
                        return;
                    }
                }
                else
                {
                    info = FindConnectedForHost(manager, _args.HostId);
                    if (info == null)
                    {
                        // 专用连接：连接 + 认证后不开 shell、不发 AutoRun（F03）。
                        info = await manager.OpenAsync(new SessionOpenRequest
                        {
                            HostId = _args.HostId,
                            OpenShell = false
                        }).ConfigureAwait(true);
                        if (info.State != SessionUiState.Connected)
                        {
                            ShowError(ErrorCodeText(info.ErrorCode));
                            return;
                        }
                        _ownsSession = true;
                    }
                }

                _session = info;
                Title = (info.Title ?? string.Empty) + " · SFTP";
                info.PropertyChanged += OnSessionPropertyChanged;

                if (!await ConnectClientAsync().ConfigureAwait(true))
                {
                    return;
                }
                _queue = new TransferQueue();
                _queue.ItemChanged += OnQueueItemChanged;
                RefreshAsync().Forget("Sftp.Refresh", AppLog.Logger);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<bool> ConnectClientAsync()
        {
            NativeSshSession native = _session == null ? null : _session.NativeSession as NativeSshSession;
            if (native == null)
            {
                ShowError(ErrorCodeText(SshErrorCode.InternalError));
                return false;
            }
            if (ReferenceEquals(native, _clientNative) && _client != null)
            {
                return true;
            }
            ISftpClient old = _client;
            _client = null;
            _clientNative = null;
            if (old != null)
            {
                old.Close();
                old.Dispose();
            }
            var fresh = new NativeSftpClient(native, Logger);
            SftpResult opened = await fresh.OpenAsync().ConfigureAwait(true);
            if (!opened.Ok)
            {
                fresh.Dispose();
                ShowError(ErrorCodeText(opened.Code));
                return false;
            }
            _client = fresh;
            _clientNative = native;
            return true;
        }

        // 重连（断线自动重连 / 换 native 句柄）后：SFTP 挂到新连接上并重载当前目录。
        // 传输中的项目此刻会失败，可「重试」续传。
        private async void OnSessionPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != "NativeSession"
                && e.PropertyName != "State")
            {
                return;
            }
            SessionInfo info = _session;
            if (info == null || _leaving)
            {
                return;
            }
            // SessionInfo 的通知已由 SessionManager 经 IUiDispatcher 封送到 UI 线程。
            if (info.State == SessionUiState.Connected && info.NativeSession != null
                && !ReferenceEquals(info.NativeSession, _clientNative))
            {
                ReconnectClientAsync().Forget("Sftp.Reconnect", AppLog.Logger);
            }
            else if (info.State == SessionUiState.Error || info.State == SessionUiState.Disconnected
                || info.State == SessionUiState.Reconnecting)
            {
                HasError = true;
                StatusText = ErrorCodeText(info.ErrorCode);
            }
        }

        private async Task ReconnectClientAsync()
        {
            if (_leaving || _session == null)
            {
                return;
            }
            IsBusy = true;
            try
            {
                if (await ConnectClientAsync().ConfigureAwait(true))
                {
                    HasError = false;
                    RefreshAsync().Forget("Sftp.Refresh", AppLog.Logger);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ---- 目录浏览 ----

        // 刷新请求序号：每次 RefreshAsync 自增，await 回来后只有「仍是最新 + 路径未变」
        // 的响应才允许落地（过期响应整段丢弃，成功与失败分支都丢）。
        private int _refreshSeq;

        public async Task RefreshAsync()
        {
            ISftpClient client = _client;
            if (client == null || _leaving)
            {
                return;
            }
            // 请求发出前先固定这两项：ListDirectoryAsync 期间用户可能已进/出目录，
            // 或又点了一次刷新。旧目录的列表若继续落地，会把新目录的内容覆盖回去；
            // 旧请求的失败更会把「已加载好的新目录」顶成错误页。
            string path = _currentPath;
            int seq = Interlocked.Increment(ref _refreshSeq);
            try
            {
                SftpDirResult listing = await client.ListDirectoryAsync(path).ConfigureAwait(true);
                if (IsStaleRefresh(seq, path))
                {
                    return;
                }
                if (!listing.Ok)
                {
                    ShowError(ErrorCodeText(listing.Code));
                    return;
                }
                HasError = false;
                StatusText = string.Empty;
                // 存服务端原始列表，过滤+排序只在 RebuildRows 一处做：
                // 这里先按 ShowHidden 剪掉隐藏项的话，「显示隐藏文件」开关就地
                // 重排时已无隐藏项可用（要等下次刷新才见效）。
                _entries = listing.Entries ?? new List<RemoteEntry>();
                RebuildRows();
                RebuildBreadcrumbs();
            }
            catch (Exception ex)
            {
                // 过期请求的异常同样丢弃（旧目录的超时不该顶掉新目录的状态），不记日志。
                if (IsStaleRefresh(seq, path))
                {
                    return;
                }
                // 只记类型名（脱敏）。
                Logger.Log(LogLevel.Error, "SftpPage", "refresh " + ex.GetType().Name);
                ShowError(ErrorCodeText(SshErrorCode.SftpTransferFailed));
            }
            finally
            {
                RaisePropertyChanged("IsEmpty");
            }
        }

        // 本次响应是否已过期：离开中、被更新的刷新取代、或期间目录已切走。
        private bool IsStaleRefresh(int seq, string path)
        {
            if (_leaving || seq != Volatile.Read(ref _refreshSeq))
            {
                return true;
            }
            return !string.Equals(path, _currentPath, StringComparison.Ordinal);
        }

        // 目录列表 → 展示行（UI 走查：整表 Clear+Add 会让列表每次刷新都闪一下）。
        // 同目录内按「条目路径＝目录+文件名」复用行 VM：只更新变化的展示字段，
        // 新增/移除差异项，再把顺序就地对齐；排序规则与展示内容一字未改。
        // 跨目录（含首帧）仍整表重建——同名文件在两个目录里不是同一个条目。
        private void RebuildRows()
        {
            IReadOnlyList<RemoteEntry> entries = SftpListing.Sort(_entries, _sortMode, _showHidden);
            if (!string.Equals(_rowsPath, _currentPath, StringComparison.Ordinal))
            {
                var fresh = new Dictionary<string, SftpRowVm>(StringComparer.Ordinal);
                _rows.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    SftpRowVm row = MakeRow(entries[i]);
                    fresh[RowKey(entries[i])] = row;
                    _rows.Add(row);
                }
                _rowsByPath = fresh;
                _rowsPath = _currentPath;
                RaisePropertyChanged("IsEmpty");
                return;
            }

            Dictionary<string, SftpRowVm> previous = _rowsByPath;
            var next = new Dictionary<string, SftpRowVm>(StringComparer.Ordinal);
            var ordered = new List<SftpRowVm>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                RemoteEntry entry = entries[i];
                string key = RowKey(entry);
                SftpRowVm row = null;
                if (!next.ContainsKey(key))
                {
                    previous.TryGetValue(key, out row);
                }
                if (row == null)
                {
                    row = MakeRow(entry);
                }
                else
                {
                    UpdateRow(row, entry);
                }
                next[key] = row;
                ordered.Add(row);
            }

            // 消失的条目先摘掉（倒序删，避免下标漂移）。
            var alive = new HashSet<SftpRowVm>(ordered);
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (!alive.Contains(_rows[i]))
                {
                    _rows.RemoveAt(i);
                }
            }

            // 就地对齐目标顺序：新行插入、错位行 Move；顺序没变时这一段零通知。
            for (int i = 0; i < ordered.Count; i++)
            {
                SftpRowVm row = ordered[i];
                int at = _rows.IndexOf(row);
                if (at < 0)
                {
                    _rows.Insert(i, row);
                }
                else if (at != i)
                {
                    _rows.Move(at, i);
                }
            }

            _rowsByPath = next;
            RaisePropertyChanged("IsEmpty");
        }

        // 行身份＝远端完整路径（同目录内与文件名一一对应）。
        private static string RowKey(RemoteEntry entry)
        {
            return entry != null ? (entry.Path ?? string.Empty) : string.Empty;
        }

        private static SftpRowVm MakeRow(RemoteEntry entry)
        {
            string size;
            string mtime;
            string link;
            FormatRow(entry, out size, out mtime, out link);
            return new SftpRowVm(entry, size, mtime, link);
        }

        private static void UpdateRow(SftpRowVm row, RemoteEntry entry)
        {
            string size;
            string mtime;
            string link;
            FormatRow(entry, out size, out mtime, out link);
            row.Update(entry, size, mtime, link);
        }

        private static void FormatRow(RemoteEntry entry,
            out string size, out string mtime, out string link)
        {
            size = entry.IsDirectory && !entry.IsSymlink
                ? string.Empty
                : SftpListing.FormatSize(entry.Size);
            mtime = FormatMtime(entry);
            link = string.Empty;
            if (entry.IsSymlink && !string.IsNullOrEmpty(entry.LinkTarget))
            {
                link = string.Format(CultureInfo.InvariantCulture,
                    Loader.GetString("Sftp_SymlinkTo"), entry.LinkTarget);
            }
        }

        private static string FormatMtime(RemoteEntry entry)
        {
            if (!entry.HasMtime)
            {
                return string.Empty;
            }
            int minutes = SftpListing.MinutesAgo(entry.MtimeUtc, DateTime.Now);
            if (minutes == 0)
            {
                return Loader.GetString("Sftp_JustNow");
            }
            if (minutes > 0)
            {
                return string.Format(CultureInfo.CurrentCulture,
                    Loader.GetString("Sftp_MinutesAgo"),
                    minutes.ToString(CultureInfo.InvariantCulture));
            }
            return SftpListing.FormatMtime(entry.MtimeUtc);
        }

        private void RebuildBreadcrumbs()
        {
            _breadcrumbs.Clear();
            IReadOnlyList<SftpBreadcrumb> items = SftpListing.Breadcrumb(_currentPath);
            for (int i = 0; i < items.Count; i++)
            {
                _breadcrumbs.Add(items[i]);
            }
        }

        private void EnterRow(SftpRowVm row)
        {
            if (row == null || row.Entry == null || IsBusy)
            {
                return;
            }
            if (row.IsDirectory)
            {
                NavigateTo(row.Entry.Path);
            }
            // 文件/链接点行不做默认动作（下载在行菜单里）。
        }

        private void NavigateTo(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            string normalized = RemotePath.Normalize(path);
            if (!RemotePath.IsAbsolute(normalized))
            {
                return;
            }
            _currentPath = normalized;
            RaisePropertyChanged("CurrentPath");
            RefreshAsync().Forget("Sftp.Refresh", AppLog.Logger);
        }

        private void NavigateBreadcrumb(SftpBreadcrumb item)
        {
            if (item != null)
            {
                NavigateTo(item.Path);
            }
        }

        private void GoUp()
        {
            if (_currentPath == RemotePath.Root || IsBusy)
            {
                return;
            }
            NavigateTo(RemotePath.GetDirectoryName(_currentPath));
        }

        private void SetSort(SftpSortMode mode)
        {
            SortMode = mode;
        }

        private void ToggleHidden()
        {
            ShowHidden = !ShowHidden;
        }

        // ---- 行操作（新建文件夹 / 重命名 / 权限 / 删除 / 复制路径） ----

        private async Task NewFolderAsync()
        {
            if (_client == null || IsBusy)
            {
                return;
            }
            RenameDialogResult pick = await RenameDialog.ShowAsync(
                Loader.GetString("Sftp_MkdirTitle"), string.Empty).ConfigureAwait(true);
            if (!pick.Confirmed || !IsValidName(pick.Name))
            {
                return;
            }
            IsBusy = true;
            try
            {
                string path = RemotePath.Combine(_currentPath, pick.Name);
                SftpResult created = await _client.CreateDirectoryAsync(
                    path, SftpConstants.DefaultRemoteMode).ConfigureAwait(true);
                if (!created.Ok)
                {
                    ShowError(ErrorCodeText(created.Code));
                    return;
                }
                Notify(Loader.GetString("Sftp_FolderCreated"));
                RefreshAsync().Forget("Sftp.Refresh", AppLog.Logger);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RenameRow(SftpRowVm row)
        {
            if (_client == null || row == null || row.Entry == null || IsBusy)
            {
                return;
            }
            RenameDialogResult pick = await RenameDialog.ShowAsync(
                Loader.GetString("Sftp_RenameTitle"), row.Entry.Name).ConfigureAwait(true);
            if (!pick.Confirmed || !IsValidName(pick.Name)
                || string.Equals(pick.Name, row.Entry.Name, StringComparison.Ordinal))
            {
                return;
            }
            IsBusy = true;
            try
            {
                string target = RemotePath.Combine(_currentPath, pick.Name);
                SftpResult renamed = await _client.RenameAsync(row.Entry.Path, target)
                    .ConfigureAwait(true);
                if (!renamed.Ok)
                {
                    ShowError(ErrorCodeText(renamed.Code));
                    return;
                }
                Notify(Loader.GetString("Sftp_Renamed"));
                RefreshAsync().Forget("Sftp.Refresh", AppLog.Logger);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task PermissionsRow(SftpRowVm row)
        {
            if (_client == null || row == null || row.Entry == null || IsBusy)
            {
                return;
            }
            int initial = row.Entry.HasPermissions
                ? row.Entry.Permissions
                : SftpConstants.DefaultRemoteMode;
            PermissionsDialogResult pick = await PermissionsDialog.ShowAsync(
                Loader.GetString("Sftp_PermissionsTitle"), initial).ConfigureAwait(true);
            if (!pick.Confirmed)
            {
                return;
            }
            IsBusy = true;
            try
            {
                SftpResult applied = await _client.SetPermissionsAsync(row.Entry.Path, pick.Mode)
                    .ConfigureAwait(true);
                if (!applied.Ok)
                {
                    ShowError(ErrorCodeText(applied.Code));
                    return;
                }
                Notify(Loader.GetString("Sftp_PermissionsChanged"));
                RefreshAsync().Forget("Sftp.Refresh", AppLog.Logger);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DeleteRow(SftpRowVm row)
        {
            if (_client == null || row == null || row.Entry == null || IsBusy)
            {
                return;
            }
            bool recursive = row.IsDirectory;
            string template = recursive ? "Sftp_DeleteDirConfirm" : "Sftp_DeleteFileConfirm";
            string message = string.Format(CultureInfo.CurrentCulture,
                Loader.GetString(template), row.Entry.Name);
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                Loader.GetString("Sftp_DeleteTitle"),
                message,
                Loader.GetString("Sftp_DeleteButtonText"),
                Loader.GetString("Dialog_Cancel"),
                recursive).ConfigureAwait(true);
            if (!confirm.Confirmed)
            {
                return;
            }
            IsBusy = true;
            try
            {
                SftpResult removed = await SftpRecursiveDelete.DeleteAsync(
                    _client, row.Entry.Path, CancellationToken.None).ConfigureAwait(true);
                if (!removed.Ok)
                {
                    string text = ErrorCodeText(removed.Code);
                    if (recursive)
                    {
                        text = text + " " + Loader.GetString("Sftp_DeletePartialNote");
                    }
                    ShowError(text);
                    return;
                }
                Notify(Loader.GetString("Sftp_Deleted"));
                RefreshAsync().Forget("Sftp.Refresh", AppLog.Logger);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void CopyPath(SftpRowVm row)
        {
            if (row == null || row.Entry == null)
            {
                return;
            }
            ClipboardService.SetText(row.Entry.Path);
            Notify(Loader.GetString("Sftp_PathCopied"));
        }

        private static bool IsValidName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf('/') < 0
                && name != "." && name != "..";
        }

        // ---- 传输（上传 / 下载 / 面板） ----

        private async Task UploadAsync()
        {
            if (_client == null || IsBusy)
            {
                return;
            }
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.FileTypeFilter.Add("*");
            IReadOnlyList<StorageFile> files =
                await picker.PickMultipleFilesAsync().AsTask().ConfigureAwait(true);
            if (files == null || files.Count == 0)
            {
                return;
            }
            int queued = 0;
            for (int i = 0; i < files.Count; i++)
            {
                StorageFile file = files[i];
                long size = await ReadFileSizeAsync(file).ConfigureAwait(true);
                string remote = RemotePath.Combine(_currentPath, file.Name);
                TransferItem item = TransferItem.CreateUpload(remote, file.Name, size,
                    (progress, token) => ExecuteUploadAsync(file, remote, progress, token));
                _queue.Enqueue(item);
                queued++;
            }
            TransfersExpanded = true;
            Notify(string.Format(CultureInfo.CurrentCulture,
                Loader.GetString("Sftp_UploadStarted"),
                queued.ToString(CultureInfo.InvariantCulture)));
        }

        private static async Task<long> ReadFileSizeAsync(StorageFile file)
        {
            try
            {
                BasicProperties props =
                    await file.GetBasicPropertiesAsync().AsTask().ConfigureAwait(true);
                return props != null ? (long)props.Size : -1;
            }
            catch (Exception)
            {
                // 取不到大小按未知传输（-1，不画进度条）。
                return -1;
            }
        }

        // 执行体在线程池运行：解析「当前」客户端——断线重连后旧引用失效，
        // 重试会拿新客户端接续。
        private async Task<SftpResult> ExecuteUploadAsync(
            StorageFile file, string remotePath, IProgress<SftpProgress> progress, CancellationToken token)
        {
            ISftpClient client = _client;
            if (client == null)
            {
                return SftpResult.Fail(SshErrorCode.SocketError, "not connected");
            }
            long resume = 0;
            SftpEntryResult stat = await client.StatAsync(remotePath, true).ConfigureAwait(false);
            if (stat.Ok && stat.Entry != null && stat.Entry.HasSize)
            {
                resume = stat.Entry.Size;
            }
            Stream stream = null;
            try
            {
                stream = await RandomAccessStreamAdapter.OpenForUploadAsync(file).ConfigureAwait(false);
                if (resume > stream.Length)
                {
                    resume = 0;
                }
                return await client.UploadAsync(remotePath, stream, progress, token, resume,
                    SftpConstants.DefaultRemoteMode).ConfigureAwait(false);
            }
            finally
            {
                if (stream != null)
                {
                    stream.Dispose();
                }
            }
        }

        private async Task DownloadRow(SftpRowVm row)
        {
            if (_client == null || row == null || row.Entry == null || IsBusy)
            {
                return;
            }
            RemoteEntry entry = row.Entry;
            string extension = FileExtensionFor(entry.Name);
            var picker = new FileSavePicker();
            picker.SuggestedFileName = entry.Name;
            picker.FileTypeChoices.Add(extension, new List<string> { extension });
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            StorageFile file =
                await picker.PickSaveFileAsync().AsTask().ConfigureAwait(true);
            if (file == null)
            {
                return;
            }
            long total = entry.HasSize ? entry.Size : -1;
            TransferItem item = TransferItem.CreateDownload(entry.Path, entry.Name, total,
                (progress, token) => ExecuteDownloadAsync(file, entry.Path, progress, token));
            _queue.Enqueue(item);
            TransfersExpanded = true;
        }

        // 断点续传按「本地已写大小」续写（下载入参 resumeOffset 语义见 F02 ISftpClient）。
        private async Task<SftpResult> ExecuteDownloadAsync(
            StorageFile file, string remotePath, IProgress<SftpProgress> progress, CancellationToken token)
        {
            ISftpClient client = _client;
            if (client == null)
            {
                return SftpResult.Fail(SshErrorCode.SocketError, "not connected");
            }
            Stream stream = null;
            try
            {
                stream = await RandomAccessStreamAdapter.OpenForDownloadAsync(file).ConfigureAwait(false);
                long resume = stream.Length;
                return await client.DownloadAsync(remotePath, stream, progress, token, resume)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (stream != null)
                {
                    stream.Dispose();
                }
            }
        }

        // FileSavePicker 必须给至少一个具体类型（不允许 "*"）；取 1–5 位字母数字扩展名，
        // 其余给 .dat 兜底。
        private static string FileExtensionFor(string name)
        {
            int dot = name == null ? -1 : name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1)
            {
                string ext = name.Substring(dot);
                bool ok = ext.Length <= 6;
                for (int i = 1; i < ext.Length && ok; i++)
                {
                    if (!char.IsLetterOrDigit(ext[i]))
                    {
                        ok = false;
                    }
                }
                if (ok)
                {
                    return ext;
                }
            }
            return ".dat";
        }

        private void CancelTransfer(TransferRowVm row)
        {
            if (row != null && _queue != null)
            {
                _queue.Cancel(row.Id);
            }
        }

        private void RetryTransfer(TransferRowVm row)
        {
            if (row != null && _queue != null)
            {
                _queue.Retry(row.Id);
            }
        }

        private void ClearFinished()
        {
            if (_queue == null)
            {
                return;
            }
            _queue.ClearFinished();
            HashSet<string> alive = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<TransferItem> snapshot = _queue.Snapshot();
            for (int i = 0; i < snapshot.Count; i++)
            {
                alive.Add(snapshot[i].Id);
            }
            for (int i = _transferRows.Count - 1; i >= 0; i--)
            {
                if (!alive.Contains(_transferRows[i].Id))
                {
                    _transferRows.RemoveAt(i);
                }
            }
            RefreshTransferCounts();
        }

        private void OnQueueItemChanged(object sender, TransferItem item)
        {
            // 队列泵在线程池触发；碰 UI 集合一律封送。
            DispatcherHelper.Post(() => ApplyItem(item));
        }

        private void ApplyItem(TransferItem item)
        {
            if (item == null || _leaving)
            {
                return;
            }
            TransferRowVm row;
            if (!_transferById.TryGetValue(item.Id, out row))
            {
                row = new TransferRowVm();
                _transferById[item.Id] = row;
                _transferRows.Add(row);
            }
            row.UpdateFrom(item);
            RefreshTransferCounts();
            if (item.State == TransferState.Completed && item.Direction == TransferDirection.Upload
                && RemotePath.GetDirectoryName(item.RemotePath) == _currentPath)
            {
                // 新文件落在当前目录：刷新可见。
                RefreshAsync().Forget("Sftp.Refresh", AppLog.Logger);
            }
        }

        private void RefreshTransferCounts()
        {
            int active = 0;
            IReadOnlyList<TransferItem> snapshot = _queue != null ? _queue.Snapshot() : null;
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (snapshot[i].State == TransferState.Queued
                        || snapshot[i].State == TransferState.Running)
                    {
                        active++;
                    }
                }
            }
            ActiveTransferCount = active;
            SetProperty(ref _showTransferBar, active > 0 || _transferRows.Count > 0, "ShowTransferBar");
        }

        // ---- 离开（OnNavigatedFrom 调用；不阻塞 UI 线程） ----

        public void Leave()
        {
            _leaving = true;
            SessionInfo info = _session;
            if (info != null)
            {
                info.PropertyChanged -= OnSessionPropertyChanged;
            }
            TransferQueue queue = _queue;
            _queue = null;
            ISftpClient client = _client;
            _client = null;
            if (queue != null)
            {
                IReadOnlyList<TransferItem> snapshot = queue.Snapshot();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (snapshot[i].State == TransferState.Queued
                        || snapshot[i].State == TransferState.Running)
                    {
                        queue.Cancel(snapshot[i].Id);
                    }
                }
                queue.Dispose();
            }
            if (client != null)
            {
                client.Close();
                client.Dispose();
            }
            if (_ownsSession && info != null && AppServices.Current != null
                && AppServices.Current.Sessions != null)
            {
                // 专用连接随页面离开一起关（SFTP 会话独占连接，见 01-DESIGN §11.1）。
                AppServices.Current.Sessions.Close(info.SessionId);
            }
        }

        // ---- 工具 ----

        private void ShowError(string text)
        {
            HasError = true;
            StatusText = text ?? string.Empty;
        }

        private void Notify(string message)
        {
            EventHandler<string> handler = NotifyRequested;
            if (handler != null && !string.IsNullOrEmpty(message))
            {
                handler(this, message);
            }
        }

        // 错误码 → 界面文案。静态：传输面板行（TransferRowVm）不是 ViewModel，拿不到
        // ViewModelBase.Logger，两处共用这一份兜底逻辑。
        // 未在 resw 登记码值时不得把英文枚举名上屏：界面给「传输失败（错误码 N）」，
        // 枚举名只写日志。日志走 FileLogger.Instance（组合根注册的正是它，且静态可用）。
        internal static string ErrorCodeText(SshErrorCode code)
        {
            if (code == SshErrorCode.None)
            {
                return string.Empty;
            }
            string number = ((int)code).ToString(CultureInfo.InvariantCulture);
            string text = Loader.GetString("Error_" + number);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
            FileLogger.Instance.Log(LogLevel.Warning, "SftpPage",
                "unmapped error code " + number + " (" + code + ")");
            return string.Format(CultureInfo.CurrentCulture,
                Loader.GetString("Sftp_ErrCodeFallback"), number);
        }

        private static SessionInfo FindSession(SessionManager manager, string sessionId)
        {
            for (int i = 0; i < manager.Sessions.Count; i++)
            {
                if (string.Equals(manager.Sessions[i].SessionId, sessionId, StringComparison.Ordinal))
                {
                    return manager.Sessions[i];
                }
            }
            return null;
        }

        // 只把「有 shell」的会话复用给 SFTP——免得挑走某条 SFTP 专用连接。
        private static SessionInfo FindConnectedForHost(SessionManager manager, string hostId)
        {
            for (int i = 0; i < manager.Sessions.Count; i++)
            {
                SessionInfo info = manager.Sessions[i];
                if (string.Equals(info.HostId, hostId, StringComparison.Ordinal)
                    && info.ShellOpened
                    && info.State == SessionUiState.Connected)
                {
                    return info;
                }
            }
            return null;
        }
    }
}
