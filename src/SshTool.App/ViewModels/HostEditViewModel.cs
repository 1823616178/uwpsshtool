using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Validation;

namespace SshTool.App.ViewModels
{
    public sealed class IdNameOption
    {
        public IdNameOption(string id, string name)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
    }

    public sealed class EnvVarItem : ObservableObject
    {
        private string _key = string.Empty;
        private string _value = string.Empty;

        public string Key
        {
            get { return _key; }
            set { SetProperty(ref _key, value ?? string.Empty); }
        }

        public string Value
        {
            get { return _value; }
            set { SetProperty(ref _value, value ?? string.Empty); }
        }
    }

    public sealed class HostEditViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private HostEditState _state;
        private IReadOnlyList<Host> _allHosts = new Host[0];
        private IReadOnlyDictionary<string, string> _errors = new Dictionary<string, string>();
        private string _title = "新建主机";
        private AuthType _loadedAuth;
        private string _loadedKeyId;

        public HostEditViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            _services = services;
            Groups = new ObservableCollection<IdNameOption>();
            Jumps = new ObservableCollection<IdNameOption>();
            Appearances = new ObservableCollection<IdNameOption>();
            TermTypes = new ObservableCollection<IdNameOption>
            {
                new IdNameOption("xterm-256color", "xterm-256color"),
                new IdNameOption("xterm", "xterm"),
                new IdNameOption("vt100", "vt100")
            };
            EnvVars = new ObservableCollection<EnvVarItem>();
            Tunnels = new ObservableCollection<IdNameOption>();
            Keys = new ObservableCollection<IdNameOption>();
            Credentials = CredentialDraft.ForNew();
            SaveCommand = new AsyncCommand(SaveCoreAsync, onError: OnError);
            CancelCommand = new RelayCommand(() => Navigation.GoBack());
            AddEnvCommand = new RelayCommand(AddEnv);
            ClearFingerprintCommand = new RelayCommand(ClearFingerprint);
            AddTunnelCommand = new RelayCommand(() =>
                Navigation.Navigate<PlaceholderPage>(new PlaceholderArgs("添加隧道", "M7")));
        }

        public ICommand SaveCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
        public ICommand AddEnvCommand { get; private set; }
        public ICommand ClearFingerprintCommand { get; private set; }
        public ICommand AddTunnelCommand { get; private set; }

        public ObservableCollection<IdNameOption> Groups { get; private set; }
        public ObservableCollection<IdNameOption> Jumps { get; private set; }
        public ObservableCollection<IdNameOption> Appearances { get; private set; }
        public ObservableCollection<IdNameOption> TermTypes { get; private set; }
        public ObservableCollection<EnvVarItem> EnvVars { get; private set; }
        public ObservableCollection<IdNameOption> Tunnels { get; private set; }
        public ObservableCollection<IdNameOption> Keys { get; private set; }
        public CredentialDraft Credentials { get; private set; }

        public string Title
        {
            get { return _title; }
            private set { SetProperty(ref _title, value); }
        }

        public IReadOnlyDictionary<string, string> Errors
        {
            get { return _errors; }
            private set
            {
                _errors = value ?? new Dictionary<string, string>();
                RaisePropertyChanged();
            }
        }

        public bool IsDirty
        {
            get
            {
                if (_state != null && _state.IsDirty)
                {
                    return true;
                }
                if (Credentials == null)
                {
                    return false;
                }
                return Credentials.PasswordState != CredentialFieldState.Unchanged
                    || Credentials.PassphraseState != CredentialFieldState.Unchanged
                    || Credentials.AuthType != _loadedAuth
                    || !string.Equals(Credentials.KeyId ?? string.Empty, _loadedKeyId ?? string.Empty, StringComparison.Ordinal);
            }
        }

        public string Name { get { return _state == null ? string.Empty : _state.Name; } set { SetField(() => _state.Name = value); } }
        public string HostName { get { return _state == null ? string.Empty : _state.HostName; } set { SetField(() => _state.HostName = value); } }
        public string PortText { get { return _state == null ? string.Empty : _state.PortText; } set { SetField(() => _state.PortText = value); } }
        public string Username { get { return _state == null ? string.Empty : _state.Username; } set { SetField(() => _state.Username = value); } }
        public string GroupId { get { return _state == null ? string.Empty : _state.GroupId; } set { SetField(() => _state.GroupId = value); } }
        public string KeepaliveText { get { return _state == null ? string.Empty : _state.KeepaliveText; } set { SetField(() => _state.KeepaliveText = value); } }
        public string HostFingerprint { get { return _state == null ? string.Empty : _state.HostFingerprint; } }
        public string AppearanceId { get { return _state == null ? string.Empty : _state.AppearanceId; } set { SetField(() => _state.AppearanceId = value); } }
        public string TermType { get { return _state == null ? string.Empty : _state.TermType; } set { SetField(() => _state.TermType = value); } }
        public bool BackspaceSendsCtrlH { get { return _state != null && _state.BackspaceSendsCtrlH; } set { SetField(() => _state.BackspaceSendsCtrlH = value); } }
        public string InitCommandsText { get { return _state == null ? string.Empty : _state.InitCommandsText; } set { SetField(() => _state.InitCommandsText = value); } }
        public bool TmuxAutoAttach { get { return _state != null && _state.TmuxAutoAttach; } set { SetField(() => _state.TmuxAutoAttach = value); } }
        public string TmuxSessionName { get { return _state == null ? string.Empty : _state.TmuxSessionName; } set { SetField(() => _state.TmuxSessionName = value); } }
        public string JumpHostId { get { return _state == null ? string.Empty : _state.JumpHostId; } set { SetField(() => _state.JumpHostId = value); } }

        public async Task LoadAsync(HostEditArgs args)
        {
            HostEditArgs a = args ?? HostEditArgs.New();
            _allHosts = await _services.Hosts.GetAllAsync().ConfigureAwait(true);
            if (a.Mode == HostEditMode.Edit || a.Mode == HostEditMode.Duplicate)
            {
                Host host = await _services.Hosts.GetByIdAsync(a.HostId).ConfigureAwait(true);
                if (host == null)
                {
                    _state = HostEditState.ForNew();
                    Title = "新建主机";
                }
                else if (a.Mode == HostEditMode.Duplicate)
                {
                    _state = HostEditState.ForDuplicate(host);
                    Title = "复制为新主机";
                }
                else
                {
                    _state = HostEditState.ForEdit(host);
                    Title = "编辑主机";
                }
            }
            else
            {
                _state = HostEditState.ForNew();
                Title = "新建主机";
            }

            EnvVars.Clear();
            for (int i = 0; i < _state.EnvVars.Count; i++)
            {
                EnvVars.Add(new EnvVarItem { Key = _state.EnvVars[i].Key, Value = _state.EnvVars[i].Value });
            }

            await LoadLookupsAsync().ConfigureAwait(true);
            await LoadCredentialsAsync().ConfigureAwait(true);
            Revalidate();
            RaiseAll();
        }

        public string FirstErrorField()
        {
            string[] order = { "name", "host", "port", "username", "keepalive", "envVars", "jumpHostId", "termType" };
            for (int i = 0; i < order.Length; i++)
            {
                if (_errors.ContainsKey(order[i]))
                {
                    return order[i];
                }
            }
            foreach (var pair in _errors)
            {
                return pair.Key;
            }
            return null;
        }

        public bool LastSaveHadErrors { get; private set; }

        public async Task<TestConnectResult> TestAsync()
        {
            SyncEnvToState();
            Host draft = _state.ToHost();
            ISshSessionFactory factory;
            if (!ServiceRegistry.TryGet(out factory))
            {
                return new TestConnectResult
                {
                    Success = false,
                    MessageKey = "Error_500",
                    Stage = TestConnectStage.Handshake
                };
            }
            var tester = new ConnectionTester(
                _services.KnownHosts,
                _services.Secrets,
                factory,
                new DialogHostKeyPrompter(),
                new DispatcherUiDispatcher());
            return await tester.RunAsync(draft, Credentials, System.Threading.CancellationToken.None)
                .ConfigureAwait(true);
        }

        public async Task SaveCoreAsync()
        {
            LastSaveHadErrors = false;
            SyncEnvToState();
            Revalidate();
            if (_errors.Count > 0)
            {
                LastSaveHadErrors = true;
                return;
            }
            _state.AuthType = Credentials.AuthType;
            _state.KeyId = Credentials.KeyId;
            Host host = _state.ToHost();
            if (_state.Mode == HostEditMode.Edit)
            {
                await _services.Hosts.UpdateAsync(host, ChangeOrigin.User).ConfigureAwait(true);
            }
            else
            {
                await _services.Hosts.AddAsync(host, ChangeOrigin.User).ConfigureAwait(true);
            }
            await PersistSecretsAsync(host.Id).ConfigureAwait(true);
            Credentials.ClearPlaintext();
            _state.CaptureBaseline();
            Navigation.GoBack();
        }

        public void RemoveEnv(EnvVarItem item)
        {
            EnvVars.Remove(item);
            Revalidate();
        }

        private void AddEnv()
        {
            EnvVars.Add(new EnvVarItem());
            Revalidate();
        }

        private void ClearFingerprint()
        {
            if (_state == null)
            {
                return;
            }
            _state.HostFingerprint = string.Empty;
            RaisePropertyChanged("HostFingerprint");
            Revalidate();
        }

        private async Task LoadLookupsAsync()
        {
            Groups.Clear();
            Groups.Add(new IdNameOption(string.Empty, "未分组"));
            IReadOnlyList<HostGroup> groups = await _services.Groups.GetAllAsync().ConfigureAwait(true);
            for (int i = 0; i < groups.Count; i++)
            {
                Groups.Add(new IdNameOption(groups[i].Id, groups[i].Name));
            }

            Tunnels.Clear();
            if (_state.Mode == HostEditMode.Edit && !string.IsNullOrEmpty(_state.HostId))
            {
                IReadOnlyList<Tunnel> tunnels = await _services.Tunnels.GetAllAsync().ConfigureAwait(true);
                for (int i = 0; i < tunnels.Count; i++)
                {
                    if (tunnels[i].ServerId == _state.HostId || tunnels[i].DestServerId == _state.HostId)
                    {
                        Tunnels.Add(new IdNameOption(tunnels[i].Id, tunnels[i].Name));
                    }
                }
            }

            await ReloadAppearancesAsync().ConfigureAwait(true);

            await ReloadKeysAsync().ConfigureAwait(true);

            RefreshJumps();
        }

        // K02：导入/生成密钥后刷新私钥下拉（保留已选）。
        public async Task ReloadKeysAsync()
        {
            Keys.Clear();
            Keys.Add(new IdNameOption(string.Empty, "（未选择）"));
            IReadOnlyList<KeyEntry> keys = await _services.Keys.GetAllAsync().ConfigureAwait(true);
            for (int i = 0; i < keys.Count; i++)
            {
                KeyEntry key = keys[i];
                string tail = CredentialDraft.FingerprintTail(key.FingerprintSha256);
                string label = (key.Name ?? "密钥") + " · " + (key.KeyType ?? "") + " · " + tail;
                Keys.Add(new IdNameOption(key.Id, label));
            }
        }

        // A03：从外观管理页返回后刷新下拉（保留已选）。
        public async Task ReloadAppearancesAsync()
        {
            Appearances.Clear();
            Appearances.Add(new IdNameOption(string.Empty, "跟随默认"));
            IReadOnlyList<AppearanceProfile> builtIns = BuiltInThemes.All;
            for (int i = 0; i < builtIns.Count; i++)
            {
                Appearances.Add(new IdNameOption(builtIns[i].Id, builtIns[i].Name));
            }
            IReadOnlyList<AppearanceProfile> user = await _services.Appearances.GetAllAsync().ConfigureAwait(true);
            for (int i = 0; i < user.Count; i++)
            {
                Appearances.Add(new IdNameOption(user[i].Id, user[i].Name));
            }
        }

        private async Task LoadCredentialsAsync()
        {
            Credentials = CredentialDraft.ForNew();
            Credentials.AuthType = _state.AuthType;
            Credentials.KeyId = _state.KeyId;
            if (!string.IsNullOrEmpty(_state.HostId) && _state.Mode == HostEditMode.Edit)
            {
                string savedPassword = await _services.Secrets.GetAsync(SecretKeys.HostPassword(_state.HostId)).ConfigureAwait(true);
                string savedPass = await _services.Secrets.GetAsync(SecretKeys.HostPassphrase(_state.HostId)).ConfigureAwait(true);
                Credentials.HasSavedPassword = !string.IsNullOrEmpty(savedPassword);
                Credentials.HasSavedPassphrase = !string.IsNullOrEmpty(savedPass);
                Credentials.RememberPassword = Credentials.HasSavedPassword;
                Credentials.RememberPassphrase = Credentials.HasSavedPassphrase;
            }
            _loadedAuth = Credentials.AuthType;
            _loadedKeyId = Credentials.KeyId;
            RaisePropertyChanged("Credentials");
            RaiseCredentialStates();
        }

        public string PasswordPlaceholder
        {
            get { return Credentials != null && Credentials.HasSavedPassword ? "已保存" : string.Empty; }
        }

        public string PassphrasePlaceholder
        {
            get { return Credentials != null && Credentials.HasSavedPassphrase ? "已保存" : string.Empty; }
        }

        // UI 走查（凭据三态可见化）：状态语义仍完全由 CredentialDraft 持有，这里只做只读派生 +
        // 通知。文案集中在此（后续可整段换 resw），颜色交给 XAML 的 ThemeResource 跟随主题。
        public CredentialFieldState PasswordCredentialState
        {
            get { return Credentials == null ? CredentialFieldState.Unchanged : Credentials.PasswordState; }
        }

        public CredentialFieldState PassphraseCredentialState
        {
            get { return Credentials == null ? CredentialFieldState.Unchanged : Credentials.PassphraseState; }
        }

        public string PasswordCredentialStateText
        {
            get { return DescribeCredentialState(PasswordCredentialState, HasSavedPassword); }
        }

        public string PassphraseCredentialStateText
        {
            get { return DescribeCredentialState(PassphraseCredentialState, HasSavedPassphrase); }
        }

        // 三态各一个可见性开关（XAML 用 x:Bind 的 bool→Visibility 隐式转换）。
        // Unchanged 且本机没有已存凭据时不提示，避免对新建主机谎称「已保存」。
        public bool IsPasswordCredentialSaved
        {
            get { return PasswordCredentialState == CredentialFieldState.Unchanged && HasSavedPassword; }
        }

        public bool IsPasswordCredentialNew
        {
            get { return PasswordCredentialState == CredentialFieldState.Modified; }
        }

        public bool IsPasswordCredentialCleared
        {
            get { return PasswordCredentialState == CredentialFieldState.Cleared; }
        }

        public bool IsPassphraseCredentialSaved
        {
            get { return PassphraseCredentialState == CredentialFieldState.Unchanged && HasSavedPassphrase; }
        }

        public bool IsPassphraseCredentialNew
        {
            get { return PassphraseCredentialState == CredentialFieldState.Modified; }
        }

        public bool IsPassphraseCredentialCleared
        {
            get { return PassphraseCredentialState == CredentialFieldState.Cleared; }
        }

        private bool HasSavedPassword
        {
            get { return Credentials != null && Credentials.HasSavedPassword; }
        }

        private bool HasSavedPassphrase
        {
            get { return Credentials != null && Credentials.HasSavedPassphrase; }
        }

        private static string DescribeCredentialState(CredentialFieldState state, bool hasSaved)
        {
            if (state == CredentialFieldState.Modified)
            {
                return "新凭据，保存后生效";
            }
            if (state == CredentialFieldState.Cleared)
            {
                return "将清除已保存凭据";
            }
            return hasSaved ? "已保存" : string.Empty;
        }

        // 凭据状态被改动后由页面调用（SetPassword / SetRememberPassword / ApplySwitch …）。
        public void RaiseCredentialStates()
        {
            RaisePropertyChanged("PasswordCredentialState");
            RaisePropertyChanged("PassphraseCredentialState");
            RaisePropertyChanged("PasswordCredentialStateText");
            RaisePropertyChanged("PassphraseCredentialStateText");
            RaisePropertyChanged("IsPasswordCredentialSaved");
            RaisePropertyChanged("IsPasswordCredentialNew");
            RaisePropertyChanged("IsPasswordCredentialCleared");
            RaisePropertyChanged("IsPassphraseCredentialSaved");
            RaisePropertyChanged("IsPassphraseCredentialNew");
            RaisePropertyChanged("IsPassphraseCredentialCleared");
            RaisePropertyChanged("PasswordPlaceholder");
            RaisePropertyChanged("PassphrasePlaceholder");
            RaisePropertyChanged("IsDirty");
        }

        public async Task ReloadGroupsAsync()
        {
            Groups.Clear();
            Groups.Add(new IdNameOption(string.Empty, "未分组"));
            IReadOnlyList<HostGroup> groups = await _services.Groups.GetAllAsync().ConfigureAwait(true);
            for (int i = 0; i < groups.Count; i++)
            {
                Groups.Add(new IdNameOption(groups[i].Id, groups[i].Name));
            }
        }

        public async Task PersistSecretsAsync(string hostId)
        {
            IReadOnlyList<SecretMutation> mutations = Credentials.BuildMutations(hostId);
            for (int i = 0; i < mutations.Count; i++)
            {
                SecretMutation m = mutations[i];
                if (m.Value == null)
                {
                    await _services.Secrets.RemoveAsync(m.Key).ConfigureAwait(true);
                }
                else
                {
                    await _services.Secrets.SetAsync(m.Key, m.Value).ConfigureAwait(true);
                }
            }
        }

        private void RefreshJumps()
        {
            Jumps.Clear();
            Jumps.Add(new IdNameOption(string.Empty, "无"));
            List<Host> eligible = JumpChainValidator.EligibleJumps(_state == null ? null : _state.HostId, _allHosts);
            for (int i = 0; i < eligible.Count; i++)
            {
                Jumps.Add(new IdNameOption(eligible[i].Id, eligible[i].Name));
            }
        }

        private void SyncEnvToState()
        {
            if (_state == null)
            {
                return;
            }
            var rows = new List<EnvVarRow>();
            for (int i = 0; i < EnvVars.Count; i++)
            {
                rows.Add(new EnvVarRow(EnvVars[i].Key, EnvVars[i].Value));
            }
            _state.EnvVars = rows;
        }

        private void Revalidate()
        {
            if (_state == null)
            {
                return;
            }
            SyncEnvToState();
            ValidationResult result = _state.Validate(_allHosts);
            Errors = result.Errors;
        }

        private void SetField(Action assign)
        {
            if (_state == null)
            {
                return;
            }
            assign();
            Revalidate();
            RaisePropertyChanged("IsDirty");
        }

        private void RaiseAll()
        {
            RaisePropertyChanged("Name");
            RaisePropertyChanged("HostName");
            RaisePropertyChanged("PortText");
            RaisePropertyChanged("Username");
            RaisePropertyChanged("GroupId");
            RaisePropertyChanged("KeepaliveText");
            RaisePropertyChanged("HostFingerprint");
            RaisePropertyChanged("AppearanceId");
            RaisePropertyChanged("TermType");
            RaisePropertyChanged("BackspaceSendsCtrlH");
            RaisePropertyChanged("InitCommandsText");
            RaisePropertyChanged("TmuxAutoAttach");
            RaisePropertyChanged("TmuxSessionName");
            RaisePropertyChanged("JumpHostId");
            RaisePropertyChanged("IsDirty");
            RaisePropertyChanged("Title");
        }

        private void OnError(Exception ex)
        {
            if (ex != null)
            {
                Logger.Log(LogLevel.Error, "HostEdit", ex.GetType().Name);
            }
        }

        private sealed class DialogHostKeyPrompter : IHostKeyPrompter
        {
            public async Task<bool> PromptUnknownAsync(HostKeyInfo info, string hostDisplay)
            {
                HostKeyDialogResult r = await HostKeyDialog.ShowAsync(
                    hostDisplay, info.KeyType, info.FingerprintSha256, info.RandomArt).ConfigureAwait(true);
                return r.Trusted;
            }

            public async Task<bool> PromptMismatchAsync(HostKeyInfo info, string hostDisplay, string previousFingerprint)
            {
                await HostKeyMismatchDialog.ShowAsync(
                    hostDisplay, previousFingerprint ?? string.Empty, info.FingerprintSha256).ConfigureAwait(true);
                return false;
            }
        }
    }
}
