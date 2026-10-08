using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SshTool.App.Platform;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Forwarding;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using Windows.Storage;

namespace SshTool.App.Infrastructure
{
    // D05：组合根。OnLaunched 调 Initialize + StartAsync；Suspending 调 FlushAsync。
    public sealed class AppServices
    {
        public static AppServices Current { get; private set; }

        private AppServices()
        {
            Logger = FileLogger.Instance;
            FileSystem = new UwpFileSystem();
            Settings = new SettingsRepository(new LocalSettingsStore());
            Hosts = new HostRepository(FileSystem, Logger);
            Groups = new GroupRepository(FileSystem, Logger);
            Tunnels = new TunnelRepository(FileSystem, Logger);
            Keys = new KeyRepository(FileSystem, Logger);
            Snippets = new SnippetRepository(FileSystem, Logger);
            Appearances = new AppearanceRepository(FileSystem, Logger);
            KnownHosts = new KnownHostRepository(FileSystem, Logger);
            _secretStore = new DpapiSecretStore();
            Secrets = _secretStore;
            Config = new ConfigService(Hosts, Groups, Tunnels, Keys, Secrets);
            LoadWarnings = new List<string>();
        }

        public ILogger Logger { get; private set; }
        public IFileSystem FileSystem { get; private set; }
        public SettingsRepository Settings { get; private set; }
        public HostRepository Hosts { get; private set; }
        public GroupRepository Groups { get; private set; }
        public TunnelRepository Tunnels { get; private set; }
        public KeyRepository Keys { get; private set; }
        public SnippetRepository Snippets { get; private set; }
        public AppearanceRepository Appearances { get; private set; }
        public AppearanceService AppearanceService { get; private set; }
        public KnownHostRepository KnownHosts { get; private set; }
        public ISecretStore Secrets { get; private set; }
        public ConfigService Config { get; private set; }
        public SessionManager Sessions { get; private set; }
        public NativeSshAgent Agent { get; private set; }
        public KeepAwakeService KeepAwake { get; private set; }
        public LifecycleService Lifecycle { get; private set; }
        public NetworkMonitor Network { get; private set; }
        // F05：多隧道管理器与转发运行时。
        public TunnelManager TunnelManager { get; private set; }
        // S14：同步栈（任一环节构造失败时保持 null，本地 SSH 功能不受影响；
        // MainViewModel 与触发器调用方一律做空判断）。
        public AuthStore Auth { get; private set; }
        public VaultCacheStore VaultCache { get; private set; }
        public ApiClient SyncApi { get; private set; }
        public SyncLocalAdapter SyncLocal { get; private set; }
        public SyncCoordinator Sync { get; private set; }
        public SyncTriggers SyncTriggers { get; private set; }
        // U17/U18：设备重命名/撤销（SyncCoordinator 未封装这两项，直接消费 AuthService）。
        public AuthService AuthService { get; private set; }
        public List<string> LoadWarnings { get; private set; }

        public static AppServices Initialize()
        {
            if (Current != null)
            {
                return Current;
            }
            var services = new AppServices();
            Current = services;
            ServiceRegistry.Register(services);
            ServiceRegistry.Register(services.Logger);
            ServiceRegistry.Register(services.Settings);
            ServiceRegistry.Register(services.Config);
            ServiceRegistry.Register(services.Secrets);
            ServiceRegistry.Register(services.Hosts);
            ServiceRegistry.Register(services.FileSystem);
            // K03：应用内 agent（单个 native 实例，多会话复用；超时由设置项驱动，
            // 挂起清除走 LifecycleService→SessionManager.LockAgentKeys）。
            services.Agent = new NativeSshAgent(services.Logger);
            services.Agent.SetTimeout(ReadAgentTimeout(services.Settings));
            var sshFactory = new NativeSshSessionFactory(services.Agent, services.Settings);
            ServiceRegistry.Register(sshFactory);
            ServiceRegistry.Register<SshTool.Core.Sessions.ISshSessionFactory>(sshFactory);
            return services;
        }

        public async Task StartAsync()
        {
            var total = Stopwatch.StartNew();
            await TimeAsync("AppConfig", () => AppConfig.LoadAsync()).ConfigureAwait(true);
            Time("Settings", () => Settings.EnsureDefaults());
            // fix/functional-pass（P1-4）：首个页面创建前应用界面语言设置。
            LanguageOverride.Apply(Settings.Language);
            // 修（2026-09-29 排查真机输入问题时发现）：AppConfig.LoadAsync 刚把 MinLevel 设成
            // 打包默认值（info），而用户在设置页选的日志级别只在当场生效、重启即被上面这行盖掉——
            // 「调成调试、重启、日志里还是什么都没有」。设置是用户的显式选择，启动时必须复原。
            ApplyLogLevelFromSettings();
            await TimeAsync("Hosts", () => Hosts.LoadAsync()).ConfigureAwait(true);
            await TimeAsync("Groups", () => Groups.LoadAsync()).ConfigureAwait(true);
            await TimeAsync("Tunnels", () => Tunnels.LoadAsync()).ConfigureAwait(true);
            await TimeAsync("Keys", () => Keys.LoadAsync()).ConfigureAwait(true);
            await TimeAsync("Snippets", () => Snippets.LoadAsync()).ConfigureAwait(true);
            await TimeAsync("Appearances", () => Appearances.LoadAsync()).ConfigureAwait(true);
            await TimeAsync("KnownHosts", () => KnownHosts.LoadAsync()).ConfigureAwait(true);
            CollectWarnings(Hosts.LoadWarnings);
            CollectWarnings(Groups.LoadWarnings);
            CollectWarnings(Tunnels.LoadWarnings);
            CollectWarnings(Keys.LoadWarnings);
            CollectWarnings(Snippets.LoadWarnings);
            CollectWarnings(Appearances.LoadWarnings);
            CollectWarnings(KnownHosts.LoadWarnings);
            await TimeAsync("Secrets", () => PreloadSecretsAsync()).ConfigureAwait(true);
            Time("Appearance", () =>
            {
                AppearanceService = new AppearanceService(Appearances, Hosts, Settings, BuiltInThemes.All);
                ServiceRegistry.Register(AppearanceService);
            });
            Time("Theme", () =>
            {
                ThemeService.UseSystemAccent = Settings.UseSystemAccent;
                ThemeService.Initialize();
                ThemeService.Apply(ParseTheme(Settings.ThemeMode));
            });
            Time("Sessions", () =>
            {
                ISshSessionFactory factory;
                ServiceRegistry.TryGet(out factory);
                Sessions = new SessionManager(
                    Hosts, KnownHosts, Keys, Secrets, Settings, factory,
                    new UwpHostKeyPrompter(), new UwpCredentialPrompter(),
                    new DispatcherTimerFactory(), new DispatcherUiDispatcher(), Logger, Agent);
                ServiceRegistry.Register(Sessions);
                // K03：设置页改动保留时间后即时对齐 agent 超时（下次认证前
                // SessionManager 也会再次对齐，此处覆盖「改完不连」的空窗）。
                Settings.Changed += OnAgentTimeoutChanged;
            });
            // P01：屏幕常亮与生命周期服务（依赖 Sessions；Start 由 App.OnLaunched
            // 在启动完成后调用，以接入 Application 级前后台事件）。
            // P02：网络监听（依赖 Sessions；就地 Start：NetworkStatusChanged 与
            // Application 事件无关，早订早得基线）。
            Time("Lifecycle", () =>
            {
                KeepAwake = new KeepAwakeService(Settings, Sessions, Logger);
                ServiceRegistry.Register(KeepAwake);
                Lifecycle = new LifecycleService(Sessions, Settings, KeepAwake, Logger);
                ServiceRegistry.Register(Lifecycle);
                Network = new NetworkMonitor(Logger);
                ServiceRegistry.Register(Network);
                Lifecycle.WatchNetwork(Network);
                Network.Start();
            });
            // F05：多隧道管理器与原生转发运行时（01-DESIGN.md §11.2；04-TASKS F05）。
            Time("Tunnels", () =>
            {
                try
                {
                    var runtime = new NativeForwarder(Hosts, KnownHosts, Keys, Secrets, Settings, Agent, Logger);
                    TunnelManager = new TunnelManager(
                        runtime,
                        new DispatcherTimerFactory(),
                        new DispatcherUiDispatcher(),
                        Logger);
                    ServiceRegistry.Register(TunnelManager);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Warning, "Tunnels", "隧道管理器构造失败 " + ex.GetType().Name);
                }
            });
            // S14：同步栈与触发器接线（03-SYNC-PROTOCOL.md §7.5）。
            // 构造失败（配置/DPAPI 异常）只记日志：同步是增值功能，绝不阻断本地启动。
            // 初始化失败同样不阻断：触发器按当前状态门控，未登录/未启用时静默跳过。
            Time("Sync", () =>
            {
                try
                {
                    BuildSyncStack();
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Warning, "Sync", "同步栈构造失败 " + ex.GetType().Name);
                }
            });
            if (Sync != null)
            {
                try
                {
                    await TimeAsync("SyncInit", () => Sync.InitializeAsync()).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Warning, "Sync", "同步初始化失败 " + ex.GetType().Name);
                }
            }
            if (SyncTriggers != null)
            {
                SyncTriggers.NotifyStartup();
                SyncTriggers.NotifyForeground();
            }
            if (TunnelManager != null && Tunnels != null)
            {
                try
                {
                    var allTunnels = await Tunnels.GetAllAsync().ConfigureAwait(true);
                    TunnelManager.ApplyConfig(allTunnels);
                    Tunnels.Changed += (s, e) =>
                    {
                        Tunnels.GetAllAsync().ContinueWith(t =>
                        {
                            if (!t.IsFaulted && t.Result != null)
                            {
                                TunnelManager.ApplyConfig(t.Result);
                            }
                        });
                    };
                    await TunnelManager.StartAutoStartAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Warning, "Tunnels", "隧道初始化失败 " + ex.GetType().Name);
                }
            }
            Logger.Log(LogLevel.Info, "App",
                "启动完成 " + total.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
        }

        // S14：构造同步栈并接入触发器。调用方已保证 AppConfig 已加载、仓库已就绪。
        private void BuildSyncStack()
        {
            StorageFolder root = ApplicationData.Current.LocalFolder;
            Auth = new AuthStore(new DpapiSecureFile(root, AuthStore.RelativePath), logger: Logger);
            ServiceRegistry.Register(Auth);
            VaultCache = new VaultCacheStore(new DpapiSecureFile(root, VaultCacheStore.RelativePath), Logger);
            ServiceRegistry.Register(VaultCache);
            SyncApi = new ApiClient(
                AppConfig.Current.SyncApiBaseUrl, Auth, new UwpHttpTransport(), AppConfig.Current.AllowHttp,
                httpFallback: AppConfig.Current.HttpFallback);
            // https → http 降级只记一行日志（不含任何凭据），便于真机排查「为何仍是明文」。
            SyncApi.HttpFallbackActivated += (s, e) => Logger.Log(
                LogLevel.Warning, "SyncApi", "HTTPS 不可用，已降级为明文 HTTP（服务器未启用 TLS？）");
            ServiceRegistry.Register(SyncApi);
            SyncLocal = new SyncLocalAdapter(Hosts, Groups, Tunnels, Keys, Secrets, TunnelManager);
            ServiceRegistry.Register(SyncLocal);
            Sync = new SyncCoordinator(
                Auth, VaultCache, SyncApi, new NativeVaultCrypto(Logger),
                new UwpDeviceDescriptorProvider(), Logger, timers: new DispatcherTimerFactory());
            ServiceRegistry.Register(Sync);
            AuthService = new AuthService(Auth, SyncApi, new UwpDeviceDescriptorProvider(), Logger);
            SyncTriggers = new SyncTriggers(
                Sync, Hosts, Groups, Tunnels, Secrets, Settings, new DispatcherTimerFactory(), null, Logger);
            ServiceRegistry.Register(SyncTriggers);
            SyncTriggers.Start();
            if (Lifecycle != null)
            {
                Lifecycle.ReturnedToForeground += OnSyncForeground;
                Lifecycle.EnteredBackground += OnSyncBackground;
            }
            if (Network != null)
            {
                Network.NetworkChanged += OnSyncNetworkRestored;
            }
        }

        private void OnSyncForeground(object sender, EventArgs e)
        {
            if (SyncTriggers != null)
            {
                SyncTriggers.NotifyForeground();
            }
        }

        private void OnSyncBackground(object sender, EventArgs e)
        {
            if (SyncTriggers != null)
            {
                SyncTriggers.NotifyBackground();
            }
        }

        private void OnSyncNetworkRestored(object sender, EventArgs e)
        {
            if (SyncTriggers != null)
            {
                SyncTriggers.NotifyNetworkRestored();
            }
        }

        // U09：挂起前写 state/sessions.json（未关闭会话的 hostId 列表）。
        public async Task SaveSessionSnapshotAsync()
        {
            if (Sessions == null || FileSystem == null)
            {
                return;
            }
            var snap = new SessionSnapshot();
            IReadOnlyList<SessionInfo> all = Sessions.Sessions;
            for (int i = 0; i < all.Count; i++)
            {
                if (!string.IsNullOrEmpty(all[i].HostId) && all[i].State != SessionUiState.Closed)
                {
                    snap.HostIds.Add(all[i].HostId);
                }
            }
            await new SessionSnapshotStore(FileSystem).SaveAsync(snap).ConfigureAwait(false);
        }

        public async Task FlushAsync()
        {
            await Hosts.FlushAsync().ConfigureAwait(false);
            await Groups.FlushAsync().ConfigureAwait(false);
            await Tunnels.FlushAsync().ConfigureAwait(false);
            await Keys.FlushAsync().ConfigureAwait(false);
            await Snippets.FlushAsync().ConfigureAwait(false);
            await Appearances.FlushAsync().ConfigureAwait(false);
            await KnownHosts.FlushAsync().ConfigureAwait(false);
            await FileLogger.Instance.FlushAsync().ConfigureAwait(false);
        }

        // K03：agent 超时设置变更即时对齐 + 读取兜底（非法/缺失 → 默认 15）。
        private void OnAgentTimeoutChanged(object sender, SettingChangedEventArgs e)
        {
            if (e == null || !string.Equals(e.Key, "agentKeyTimeoutMinutes", StringComparison.Ordinal))
            {
                return;
            }
            try
            {
                if (Agent != null)
                {
                    Agent.SetTimeout(ReadAgentTimeout(Settings));
                }
            }
            catch (Exception)
            {
            }
        }

        private void ApplyLogLevelFromSettings()
        {
            try
            {
                Platform.FileLogger.Instance.MinLevel =
                    ViewModels.SettingsViewModel.ParseLogLevel(Settings.LogLevel);
                // 这行同时充当构建标记：日志里有它，就说明跑的是带输入诊断的这一版
                // （inputDiag 随诊断埋点一起删）。
                Logger.Log(LogLevel.Info, "App", "日志级别 effective="
                    + Platform.FileLogger.Instance.MinLevel + " inputDiag=1");
            }
            catch (Exception)
            {
                // 取不到就沿用 AppConfig 的值：日志级别不值得拖垮启动。
            }
        }

        private static int ReadAgentTimeout(SettingsRepository settings)
        {
            try
            {
                int minutes = settings.AgentKeyTimeoutMinutes;
                return minutes < 0 ? 15 : minutes;
            }
            catch (Exception)
            {
                return 15;
            }
        }

        private readonly DpapiSecretStore _secretStore;

        // fix/functional-pass（P2-3）：凭据表被隔离 / 丢弃时放在 Banner 第一条（比 JSON 存量告警更要紧）。
        // DPAPI 暂时性错误照旧上抛：这里只记日志，之后首次取凭据时会再试。
        private async Task PreloadSecretsAsync()
        {
            try
            {
                await _secretStore.PreloadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Warning, "Secrets", "preload failed " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
                return;
            }
            string text = SecretIssueText(_secretStore.LoadIssue);
            if (!string.IsNullOrEmpty(text))
            {
                LoadWarnings.Insert(0, text);
            }
        }

        internal static string SecretIssueText(SecretLoadIssue issue)
        {
            switch (issue)
            {
                case SecretLoadIssue.ContentCorruptQuarantined:
                    return Localized.Get("Main_SecretsIssue_ContentCorruptQuarantined", "Saved credentials were damaged and moved aside; re-enter passwords and passphrases");
                case SecretLoadIssue.ContentCorruptNotQuarantined:
                    return Localized.Get("Main_SecretsIssue_ContentCorruptNotQuarantined", "Saved credentials were damaged and will be overwritten; re-enter passwords and passphrases");
                case SecretLoadIssue.DecryptFailedQuarantined:
                    return Localized.Get("Main_SecretsIssue_DecryptFailedQuarantined", "Saved credentials could not be decrypted and were moved aside; re-enter passwords and passphrases");
                default:
                    return null;
            }
        }

        private void CollectWarnings(IReadOnlyList<string> warnings)
        {
            if (warnings == null)
            {
                return;
            }
            for (int i = 0; i < warnings.Count; i++)
            {
                LoadWarnings.Add(warnings[i]);
                Logger.Log(LogLevel.Warning, "Store", warnings[i]);
            }
        }

        private async Task TimeAsync(string phase, Func<Task> work)
        {
            var sw = Stopwatch.StartNew();
            await work().ConfigureAwait(true);
            Logger.Log(LogLevel.Info, "Startup",
                phase + " " + sw.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
        }

        private void Time(string phase, Action work)
        {
            var sw = Stopwatch.StartNew();
            work();
            Logger.Log(LogLevel.Info, "Startup",
                phase + " " + sw.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
        }

        private static AppThemeMode ParseTheme(string mode)
        {
            if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase))
            {
                return AppThemeMode.Light;
            }
            if (string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase))
            {
                return AppThemeMode.Dark;
            }
            return AppThemeMode.System;
        }
    }
}
