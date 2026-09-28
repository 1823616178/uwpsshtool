using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using SshTool.App.Infrastructure;
using SshTool.Core.Lifecycle;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace SshTool.App.Platform
{
    // W04（01-DESIGN §16.4）：应用在后台时会话掉线，发一条本地通知。文案只含会话标题
    // （主机显示名），不含地址与用户名；点通知回到该会话（launch = "session:<id>"）。
    public sealed class DisconnectNotifier
    {
        public const string LaunchPrefix = "session:";

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly SessionManager _sessions;
        private readonly SettingsRepository _settings;
        private readonly Func<bool> _inBackground;
        private readonly DisconnectNotifyPolicy _policy = new DisconnectNotifyPolicy();
        private readonly Dictionary<SessionInfo, SessionUiState> _states = new Dictionary<SessionInfo, SessionUiState>();
        private readonly object _gate = new object();

        public DisconnectNotifier(SessionManager sessions, SettingsRepository settings, Func<bool> inBackground)
        {
            _sessions = sessions;
            _settings = settings;
            _inBackground = inBackground;
        }

        // 与应用同寿命（AppServices 单例持有），不需要退订。
        public void Start()
        {
            if (_sessions == null)
            {
                return;
            }
            _sessions.SessionsChanged += OnSessionsChanged;
            OnSessionsChanged(this, EventArgs.Empty);
        }

        private void OnSessionsChanged(object sender, EventArgs e)
        {
            lock (_gate)
            {
                var current = new HashSet<SessionInfo>(_sessions.Sessions);
                foreach (SessionInfo info in current)
                {
                    if (!_states.ContainsKey(info))
                    {
                        _states[info] = info.State;
                        info.PropertyChanged += OnSessionPropertyChanged;
                    }
                }
                foreach (SessionInfo gone in new List<SessionInfo>(_states.Keys))
                {
                    if (!current.Contains(gone))
                    {
                        gone.PropertyChanged -= OnSessionPropertyChanged;
                        _states.Remove(gone);
                        _policy.Forget(gone.SessionId);
                    }
                }
            }
        }

        private void OnSessionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var info = sender as SessionInfo;
            if (info == null || (e != null && !string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != "State"))
            {
                return;
            }
            bool notify;
            lock (_gate)
            {
                SessionUiState previous;
                if (!_states.TryGetValue(info, out previous))
                {
                    return;
                }
                SessionUiState next = info.State;
                _states[info] = next;
                notify = _policy.ShouldNotify(
                    info.SessionId, previous, next,
                    _settings != null && _settings.NotifyOnDisconnect,
                    _inBackground != null && _inBackground(),
                    info.UserClosed, info.PolicySuspended,
                    Clock.ElapsedMilliseconds);
            }
            if (notify)
            {
                Show(info);
            }
        }

        private static void Show(SessionInfo info)
        {
            try
            {
                XmlDocument xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                XmlNodeList texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(Localized.Get("Notify_DisconnectTitle", "连接已断开")));
                string body = Localized.Format("Notify_DisconnectBody", "「{0}」的会话已断开，点按返回", info.Title ?? string.Empty);
                texts[1].AppendChild(xml.CreateTextNode(body));
                ((XmlElement)xml.GetElementsByTagName("toast")[0]).SetAttribute("launch", LaunchPrefix + info.SessionId);
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            }
            catch (Exception ex)
            {
                AppLog.Error("DisconnectNotifier", "Show failed", ex);
            }
        }
    }
}
