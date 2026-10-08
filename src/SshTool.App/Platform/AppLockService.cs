using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Lifecycle;
using SshTool.Core.Storage;
using Windows.Foundation;
using Windows.Security.Credentials.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace SshTool.App.Platform
{
    // W04（01-DESIGN §16.4）：应用锁。冷启动与「后台停留 ≥ 60 s 再返回」时盖一层全屏锁定页，
    // 经 UserConsentVerifier（Windows Hello / PIN，10240 起可用）验证后移除。
    // 锁定期间注册为最高优先级的返回键处理器，吞掉返回键，避免绕到下面的页面。
    public sealed class AppLockService : IBackHandler
    {
        public static readonly AppLockService Instance = new AppLockService();

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly AppLockPolicy _policy = new AppLockPolicy();
        private SettingsRepository _settings;
        private Popup _popup;
        private Grid _layer;
        private bool _started;
        private bool _coldStartHandled;
        private bool _verifying;

        private AppLockService()
        {
        }

        public static async Task<bool> IsAvailableAsync()
        {
            try
            {
                UserConsentVerifierAvailability availability = await UserConsentVerifier.CheckAvailabilityAsync();
                return availability == UserConsentVerifierAvailability.Available;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 设备不可验证（未设 PIN、策略禁用）时返回 false；调用方决定如何处理。
        public static async Task<bool> VerifyAsync(string message)
        {
            try
            {
                UserConsentVerificationResult result = await UserConsentVerifier.RequestVerificationAsync(message ?? string.Empty);
                return result == UserConsentVerificationResult.Verified;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Start(SettingsRepository settings)
        {
            if (_started || settings == null)
            {
                return;
            }
            _started = true;
            _settings = settings;
            // EnteredBackground/LeavingBackground 自 14393 起可用；最低版本 15063，无需守卫。
            Application.Current.EnteredBackground += OnEnteredBackground;
            Application.Current.LeavingBackground += OnLeavingBackground;
        }

        public void OnColdStart()
        {
            if (_coldStartHandled || _settings == null)
            {
                return;
            }
            _coldStartHandled = true;
            if (_policy.OnColdStart(_settings.AppLockEnabled))
            {
                ShowLock();
            }
        }

        public bool HandleBack()
        {
            return _policy.IsLocked;
        }

        private void OnEnteredBackground(object sender, Windows.ApplicationModel.EnteredBackgroundEventArgs e)
        {
            _policy.OnEnteredBackground(Clock.ElapsedMilliseconds);
        }

        private void OnLeavingBackground(object sender, Windows.ApplicationModel.LeavingBackgroundEventArgs e)
        {
            bool enabled = _settings != null && _settings.AppLockEnabled;
            if (_policy.OnReturnedToForeground(enabled, Clock.ElapsedMilliseconds))
            {
                DispatcherHelper.Post(ShowLock);
            }
        }

        private void ShowLock()
        {
            try
            {
                EnsureLayer();
                // ui/fix-pass：层是缓存的，主题可能已切换——每次显示按有效主题重套背景与 RequestedTheme。
                _layer.RequestedTheme = ThemeService.EffectiveElementTheme();
                _layer.Background = ThemeService.ResolveBrush("AppBgBrush");
                Rect bounds = Window.Current.Bounds;
                _layer.Width = bounds.Width;
                _layer.Height = bounds.Height;
                _popup.IsOpen = true;
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.RegisterBackHandler(this);
                }
                UnlockAsync().Forget("AppLock.Unlock", AppLog.Logger);
            }
            catch (Exception ex)
            {
                AppLog.Error("AppLock", "ShowLock failed", ex);
            }
        }

        private void EnsureLayer()
        {
            if (_popup != null)
            {
                return;
            }
            Application app = Application.Current;
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(new FontIcon
            {
                Glyph = (string)app.Resources["IconLock"],
                FontFamily = (Windows.UI.Xaml.Media.FontFamily)app.Resources["AppIconFontFamily"],
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = Localized.Get("AppLock_Title", "Lumia SSH 已锁定"),
                Style = (Style)app.Resources["SectionTitleTextStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = (Thickness)app.Resources["GapMdTop"]
            });
            var button = new Button
            {
                Content = Localized.Get("AppLock_Unlock", "解锁"),
                Style = (Style)app.Resources["PrimaryButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = (Thickness)app.Resources["GapMdTop"]
            };
            button.Click += OnUnlockClick;
            panel.Children.Add(button);
            _layer = new Grid { Background = ThemeService.ResolveBrush("AppBgBrush") };
            _layer.Children.Add(panel);
            _popup = new Popup { Child = _layer, IsLightDismissEnabled = false };
            Window.Current.SizeChanged += OnWindowSizeChanged;
        }

        private void OnWindowSizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            if (_layer != null)
            {
                _layer.Width = e.Size.Width;
                _layer.Height = e.Size.Height;
            }
        }

        private void OnUnlockClick(object sender, RoutedEventArgs e)
        {
            UnlockAsync().Forget("AppLock.Unlock", AppLog.Logger);
        }

        private async Task UnlockAsync()
        {
            if (_verifying)
            {
                return;
            }
            _verifying = true;
            try
            {
                bool ok = await VerifyAsync(Localized.Get("AppLock_Prompt", "验证身份以解锁 Lumia SSH"));
                if (!ok && !await IsAvailableAsync())
                {
                    // 验证手段已不存在（PIN 被移除、策略禁用）：保留锁定只会把用户永远关在外面。
                    // 此时锁本身已无意义，放行并记日志。
                    Logger(LogLevel.Warning, "verifier unavailable, unlocked");
                    ok = true;
                }
                if (ok)
                {
                    _policy.OnUnlocked();
                    if (_popup != null)
                    {
                        _popup.IsOpen = false;
                    }
                    NavigationService nav;
                    if (ServiceRegistry.TryGet(out nav))
                    {
                        nav.UnregisterBackHandler(this);
                    }
                }
            }
            finally
            {
                _verifying = false;
            }
        }

        private static void Logger(LogLevel level, string message)
        {
            ILogger log = AppLog.Logger;
            if (log != null)
            {
                log.Log(level, "AppLock", message);
            }
        }
    }
}
