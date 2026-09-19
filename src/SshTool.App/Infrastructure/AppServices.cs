using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SshTool.App.Platform;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

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
            Secrets = new DpapiSecretStore();
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
            var sshFactory = new NativeSshSessionFactory(services.Agent);
            ServiceRegistry.Register(sshFactory);
            ServiceRegistry.Register<SshTool.Core.Sessions.ISshSessionFactory>(sshFactory);
            return services;
        }

        public async Task StartAsync()
        {
            var total = Stopwatch.StartNew();
            await TimeAsync("AppConfig", () => AppConfig.LoadAsync()).ConfigureAwait(true);
            Time("Settings", () => Settings.EnsureDefaults());
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
            Logger.Log(LogLevel.Info, "App",
                "启动完成 " + total.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
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
