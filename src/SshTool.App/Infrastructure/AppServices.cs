using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SshTool.App.Platform;
using SshTool.Core.Common;
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
        public KnownHostRepository KnownHosts { get; private set; }
        public ISecretStore Secrets { get; private set; }
        public ConfigService Config { get; private set; }
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
            var sshFactory = new NativeSshSessionFactory();
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
            Time("Theme", () =>
            {
                ThemeService.UseSystemAccent = Settings.UseSystemAccent;
                ThemeService.Initialize();
                ThemeService.Apply(ParseTheme(Settings.ThemeMode));
            });
            Logger.Log(LogLevel.Info, "App",
                "启动完成 " + total.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms");
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
