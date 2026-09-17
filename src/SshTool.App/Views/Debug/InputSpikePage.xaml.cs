using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Debug
{
    /// <summary>
    /// SP05：软键盘 / 中文 IME / 物理键盘的事件序列采集（01-DESIGN.md §7.5、R4）。
    /// 只记录不发送——目的是搞清 Word Flow 拼音到底触发哪些事件、顺序如何，
    /// 据此决定 §7.5 的组合态判定与提交策略（以及是否启用「300 ms 静默」兜底）。
    /// </summary>
    public sealed partial class InputSpikePage : Page
    {
        // §7.5：哨兵为两个零宽空格，光标停在末尾
        private const string SentinelText = "\u200B\u200B";

        private static readonly string[] Steps =
        {
            "1/7 英文输入：在软键盘上敲 ls -la",
            "2/7 连按退格 3 次",
            "3/7 按回车",
            "4/7 拼音输入「你好」并从候选里选词",
            "5/7 点一个联想词",
            "6/7 输入一个 emoji",
            "7/7 切换输入法（中/英），再随便敲一个字符",
            "做完了：点「导出报告」"
        };

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly StringBuilder _log = new StringBuilder();
        private bool _resetting;
        private bool _hasBeforeTextChanging;
        private bool _composing;
        private int _step = -1;
        private CoreWindow _coreWindow;
        private CoreDispatcher _dispatcher;
        private InputPane _inputPane;

        public InputSpikePage()
        {
            this.InitializeComponent();
            _resetting = true;
            Sentinel.Text = SentinelText;
            Sentinel.SelectionStart = Sentinel.Text.Length;
            _resetting = false;
            Append("page", "InputSpikePage 就绪；哨兵=" + Escape(SentinelText));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _coreWindow = Window.Current.CoreWindow;
            _dispatcher = Window.Current.Dispatcher;
            _inputPane = InputPane.GetForCurrentView();

            _coreWindow.KeyDown += OnCoreKeyDown;
            _coreWindow.KeyUp += OnCoreKeyUp;
            _coreWindow.CharacterReceived += OnCharacterReceived;
            _dispatcher.AcceleratorKeyActivated += OnAcceleratorKey;
            _inputPane.Showing += OnInputPaneShowing;
            _inputPane.Hiding += OnInputPaneHiding;

            // BeforeTextChanging 是 UniversalApiContract 5.0（1709）的成员，15063 上不存在，
            // 必须 ApiInformation 守卫（CLAUDE.md 硬性约束）。它在不在，本身就是 SP05 的结论之一。
            _hasBeforeTextChanging = ApiInformation.IsEventPresent(
                "Windows.UI.Xaml.Controls.TextBox", "BeforeTextChanging");
            if (_hasBeforeTextChanging) { Sentinel.BeforeTextChanging += OnBeforeTextChanging; }
            Append("api", "TextBox.BeforeTextChanging 可用=" + _hasBeforeTextChanging
                + "（contract 5.0 / 1709；15063 预期为 False，差分只能靠 TextChanged）");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_coreWindow != null)
            {
                _coreWindow.KeyDown -= OnCoreKeyDown;
                _coreWindow.KeyUp -= OnCoreKeyUp;
                _coreWindow.CharacterReceived -= OnCharacterReceived;
            }
            if (_dispatcher != null) { _dispatcher.AcceleratorKeyActivated -= OnAcceleratorKey; }
            if (_hasBeforeTextChanging) { Sentinel.BeforeTextChanging -= OnBeforeTextChanging; }
            if (_inputPane != null)
            {
                _inputPane.Showing -= OnInputPaneShowing;
                _inputPane.Hiding -= OnInputPaneHiding;
            }
            base.OnNavigatedFrom(e);
        }

        // ---------- 日志 ----------

        private void Append(string source, string detail)
        {
            _log.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0,8:F0}ms {1,-22} {2}\n",
                _clock.Elapsed.TotalMilliseconds, source, detail);
            LogBox.Text = _log.ToString();
            LogBox.SelectionStart = LogBox.Text.Length;   // 滚到底
        }

        // 把不可见字符显示出来，否则零宽空格与组合中的文本根本看不出区别
        private static string Escape(string text)
        {
            if (text == null) { return "<null>"; }
            var sb = new StringBuilder("\"");
            foreach (char c in text)
            {
                if (c == '\u200B') { sb.Append("<ZWSP>"); }
                else if (c == '\r') { sb.Append("\\r"); }
                else if (c == '\n') { sb.Append("\\n"); }
                else if (c == '\t') { sb.Append("\\t"); }
                else if (c < 0x20 || c == 0x7F) { sb.AppendFormat(CultureInfo.InvariantCulture, "\\x{0:X2}", (int)c); }
                else if (char.IsSurrogate(c)) { sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c); }
                else { sb.Append(c); }
            }
            return sb.Append('"').ToString();
        }

        private void UpdateStatus()
        {
            StatusText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "哨兵={0} 光标={1} 组合中={2} 焦点={3}",
                Escape(Sentinel.Text), Sentinel.SelectionStart,
                _composing ? "是" : "否",
                Sentinel.FocusState == FocusState.Unfocused ? "无" : Sentinel.FocusState.ToString());
        }

        // ---------- 哨兵 TextBox 事件 ----------

        private void OnSentinelKeyDown(object sender, KeyRoutedEventArgs e)
        {
            Append("TextBox.KeyDown", "key=" + e.Key + " repeat=" + e.KeyStatus.RepeatCount);
        }

        private void OnBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
        {
            Append("BeforeTextChanging", "new=" + Escape(args.NewText));
        }

        private void OnTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            Append("TextChanging", "text=" + Escape(sender.Text));
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_resetting) { return; }

            string current = Sentinel.Text;
            Append("TextChanged", "text=" + Escape(current) + (_composing ? " （组合中，按 §7.5 不发送）" : ""));

            if (_composing) { UpdateStatus(); return; }

            Append("  →判定", DiffAgainstSentinel(current));
            ResetSentinel();
            UpdateStatus();
        }

        // §7.5 第 2/3 条：与哨兵做完整差分——多出的字符发送；少掉 n 个发 n×DEL(0x7F)。
        // 组合结束时也走这里，而不是只取「提交文本」：组合中按退格会吃掉哨兵字符
        // （SP05 实测 CompositionEnded text="<ZWSP>"），只看提交文本会把这次退格整个丢掉。
        private static string DiffAgainstSentinel(string current)
        {
            string added = current.Replace("\u200B", string.Empty);
            int sentinelLeft = current.Length - added.Length;
            int missing = SentinelText.Length - sentinelLeft;

            var sb = new StringBuilder();
            if (added.Length > 0) { sb.Append("SEND ").Append(Escape(added)); }
            if (missing > 0)
            {
                if (sb.Length > 0) { sb.Append(" + "); }
                sb.Append("SEND ").Append(missing).Append("×DEL(0x7F)");
            }
            return sb.Length > 0 ? sb.ToString() : "（与哨兵一致，不发送）";
        }

        private void ResetSentinel()
        {
            _resetting = true;
            Sentinel.Text = SentinelText;
            Sentinel.SelectionStart = Sentinel.Text.Length;
            _resetting = false;
        }

        private void OnSelectionChanged(object sender, RoutedEventArgs e)
        {
            Append("SelectionChanged", "start=" + Sentinel.SelectionStart + " len=" + Sentinel.SelectionLength);
        }

        private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
        {
            _composing = true;
            Append("CompositionStarted", "text=" + Escape(sender.Text));
            UpdateStatus();
        }

        private void OnCompositionChanged(TextBox sender, TextCompositionChangedEventArgs args)
        {
            Append("CompositionChanged", "text=" + Escape(sender.Text)
                + " start=" + args.StartIndex + " len=" + args.Length);
        }

        private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
        {
            _composing = false;
            string current = sender.Text;
            Append("CompositionEnded", "text=" + Escape(current));
            Append("  →判定", DiffAgainstSentinel(current));
            ResetSentinel();
            UpdateStatus();
        }

        private void OnSentinelGotFocus(object sender, RoutedEventArgs e)
        {
            Append("TextBox.GotFocus", Sentinel.FocusState.ToString());
            UpdateStatus();
        }

        private void OnSentinelLostFocus(object sender, RoutedEventArgs e)
        {
            Append("TextBox.LostFocus", string.Empty);
            UpdateStatus();
        }

        // ---------- CoreWindow / 物理键盘 ----------

        private void OnCoreKeyDown(CoreWindow sender, KeyEventArgs args)
        {
            Append("CoreWindow.KeyDown", "key=" + args.VirtualKey
                + " menu=" + args.KeyStatus.IsMenuKeyDown + " ext=" + args.KeyStatus.IsExtendedKey);
        }

        private void OnCoreKeyUp(CoreWindow sender, KeyEventArgs args)
        {
            Append("CoreWindow.KeyUp", "key=" + args.VirtualKey);
        }

        private void OnCharacterReceived(CoreWindow sender, CharacterReceivedEventArgs args)
        {
            Append("CharacterReceived", "code=" + args.KeyCode
                + " char=" + Escape(char.ConvertFromUtf32((int)args.KeyCode)));
        }

        private void OnAcceleratorKey(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            Append("AcceleratorKey", "type=" + args.EventType + " key=" + args.VirtualKey
                + " menu=" + args.KeyStatus.IsMenuKeyDown);
        }

        // ---------- 软键盘遮挡 ----------

        private void OnInputPaneShowing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            var r = args.OccludedRect;
            Append("InputPane.Showing", string.Format(
                CultureInfo.InvariantCulture, "occluded={0}×{1} @ {2},{3}", r.Width, r.Height, r.X, r.Y));
        }

        private void OnInputPaneHiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            var r = args.OccludedRect;
            Append("InputPane.Hiding", string.Format(
                CultureInfo.InvariantCulture, "occluded={0}×{1}", r.Width, r.Height));
        }

        // ---------- 按钮 ----------

        private void OnNextStepClick(object sender, RoutedEventArgs e)
        {
            _step++;
            if (_step >= Steps.Length) { _step = Steps.Length - 1; }
            StepText.Text = Steps[_step];
            StepButton.Content = _step >= Steps.Length - 1 ? "完成" : "下一步";
            Append("---- STEP", Steps[_step] + " ----");
            // AllowFocusOnInteraction=False 已经保住了焦点；这里再兜一道，
            // 避免某些输入法切换后焦点被系统收走导致后续步骤敲不进字。
            if (Sentinel.FocusState == FocusState.Unfocused) { FocusSentinel(); }
        }

        private void OnFocusClick(object sender, RoutedEventArgs e)
        {
            FocusSentinel();
        }

        private void FocusSentinel()
        {
            bool ok = Sentinel.Focus(FocusState.Programmatic);
            Append("Focus(Programmatic)", "returned=" + ok);
            UpdateStatus();
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            string report = DebugReport.EnvironmentHeader()
                + "\nSP05 输入事件采集（01-DESIGN §7.5）\n哨兵=" + Escape(SentinelText) + "\n\n"
                + _log.ToString();
            StatusText.Text = await DebugReport.PublishAsync("sp05-input", "SP05", report);
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            _log.Length = 0;
            LogBox.Text = string.Empty;
            _step = -1;
            StepButton.Content = "开始";
            StepText.Text = "按「开始/下一步」逐项做，做完点「导出报告」";
            ResetSentinel();
            UpdateStatus();
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) { Frame.GoBack(); }
        }
    }
}
