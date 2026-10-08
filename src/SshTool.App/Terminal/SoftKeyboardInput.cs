using System;
using System.Text;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Terminal;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace SshTool.App.Terminal
{
    // T09：软键盘通路（01-DESIGN.md §7.5，SP05 五条提交规则）。
    // 两条互斥子通路（真机修复 2026-09-29）：
    //   直通模式（默认，ImeMode=false）：可打印字符走 CoreWindow.CharacterReceived 逐键直通
    //     （不理 args.Handled，见 OnCharacterReceived），回车/退格走哨兵 TextBox 的 KeyDown，
    //     不跟组合态；哨兵文本只作兜底（Password 输入范围下它收不到文本，形同虚设）。
    //   输入法模式（ImeMode=true）：让开 CharacterReceived，全部走哨兵差分 + 组合态五条规则。
    // 物理键盘（HardwareKeyboardInput）在哨兵持有焦点时让开 CharacterReceived，两者不会重复发送。
    public sealed class SoftKeyboardInput : IDisposable
    {
        // CharacterReceived 只收可打印字符：0x00–0x1F（含回车 0x0D、退格 0x08、Tab 0x09）
        // 与 0x7F 都走 KeyDown 路（§7.5 第 4/5 条），在这里一律跳过。
        private const uint FirstPrintableCode = 32;
        private const uint DeleteCode = 127;

        // 直通模式下 CharacterReceived 已经逐键发出去的字符（复位哨兵时清空）。
        private readonly StringBuilder _direct = new StringBuilder();
        private TextBox _box;
        private InputPane _inputPane;
        private CoreWindow _coreWindow;
        private bool _resetting;
        private bool _composing;
        private bool _enterHeld;
        private bool _disposed;
        private bool _imeMode;
        private bool _directFlushQueued;

        public SoftKeyboardInput()
        {
            Sticky = new StickyModifiers();
            Modes = new TerminalModes();
        }

        public StickyModifiers Sticky { get; set; }

        public TerminalModes Modes { get; set; }

        public bool BackspaceAsBs { get; set; }

        public bool IsComposing
        {
            get { return _composing; }
        }

        // 真机修复（2026-09-28，Lumia 反馈「只有回车有效」）：Word Flow 中文键盘连英文字母也进组合态
        // （§7.5），字母要憋到组合结束才发送，且组合结束时 IME 的上屏文本未必已写进哨兵——结果只剩
        // 走 KeyDown 路的回车能到远端。终端输入绝大多数是 ASCII，所以默认把哨兵设成 Password 输入范围：
        // 键盘固定为拉丁布局、无联想无组合、不学习输入（顺带避免把服务器密码记进词库），逐键直通。
        // 需要打中文时由终端菜单切到 IME 模式（Default 输入范围，走组合态规则）。
        // 定论（2026-09-29 真机日志，55 次按键逐事件比对）：Password 输入范围确实做到了
        // 「无组合态」（零 CompositionStarted），但代价是 TextBox **只消费字符、不写进 Text**
        // （55 次 CharacterReceived 全带 Handled=true，零次 TextChanged）——所以哨兵差分在
        // 直通模式下永远是空的，字母全丢，而回车/退格走 KeyDown 照常。
        // 现在的分工：直通模式的字符**只**来自 CharacterReceived（哨兵文本在这个模式下形同虚设，
        // 差分逻辑只在别的机型/输入范围上真收到文本时才起作用）；需要联想词、emoji 或中文时
        // 切 IME 模式（Default 输入范围，哨兵文本与组合态才恢复作用）。
        public bool ImeMode
        {
            get { return _imeMode; }
            set
            {
                if (_imeMode == value)
                {
                    return;
                }
                _imeMode = value;
                ApplyInputScope();
                // 直通与输入法是两条互斥的通路：切模式时连带清掉另一条留下的状态。
                _direct.Length = 0;
                _composing = false;
            }
        }

        public bool HasFocus
        {
            get { return _box != null && _box.FocusState != FocusState.Unfocused; }
        }

        public Rect OccludedRect { get; private set; }

        public double OccludedHeight
        {
            get { return OccludedRect.Height; }
        }

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler OcclusionChanged;

        public void Attach(TextBox box)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SoftKeyboardInput));
            }
            DetachBox();
            _box = box ?? throw new ArgumentNullException(nameof(box));
            _box.KeyDown += OnKeyDown;
            _box.KeyUp += OnKeyUp;
            _box.TextChanged += OnTextChanged;
            _box.TextCompositionStarted += OnCompositionStarted;
            _box.TextCompositionEnded += OnCompositionEnded;
            _box.GotFocus += OnGotFocus;
            _box.LostFocus += OnLostFocus;
            ApplyInputScope();
            ResetSentinel();
            AttachInputPane();
            AttachCoreWindow();
        }

        public void Detach()
        {
            DetachBox();
            DetachInputPane();
            DetachCoreWindow();
            OccludedRect = new Rect();
            _composing = false;
            _enterHeld = false;
            _direct.Length = 0;
        }

        public bool Focus()
        {
            if (_box == null)
            {
                return false;
            }
            if (!HasFocus)
            {
                // 失焦期间 IME 可能没来得及发 CompositionEnded：残留的组合标志会让之后所有
                // TextChanged 被丢弃（字母全部失效），重新拿焦点时一律清掉。
                _composing = false;
            }
            ResetSentinel();
            bool ok = _box.Focus(FocusState.Programmatic);
            Diag("focus request ok=" + (ok ? 1 : 0));
            return ok;
        }

        // C-03：OccludedRect.Height > 0 只是 SIP 可见性的代理——桌面窗口化模式下 OS
        // 可能改窗口大小而非遮挡（SIP 在但 Height 为 0）；W10M 永远遮挡故真机无影响，
        // 桌面场景由 RestoreInputFocus 的物理键盘早退覆盖（TerminalView）。
        public bool IsInputPaneVisible
        {
            get { return OccludedRect.Height > 0; }
        }

        public void HidePane()
        {
            if (_inputPane != null)
            {
                _inputPane.TryHide();
            }
        }

        public void ShowPane()
        {
            if (_inputPane != null && HasFocus)
            {
                _inputPane.TryShow();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Detach();
            Input = null;
            OcclusionChanged = null;
        }

        private void AttachInputPane()
        {
            DetachInputPane();
            try
            {
                _inputPane = InputPane.GetForCurrentView();
            }
            catch (Exception)
            {
                _inputPane = null;
            }
            if (_inputPane != null)
            {
                _inputPane.Showing += OnInputPaneShowing;
                _inputPane.Hiding += OnInputPaneHiding;
            }
        }

        private void DetachInputPane()
        {
            if (_inputPane == null)
            {
                return;
            }
            _inputPane.Showing -= OnInputPaneShowing;
            _inputPane.Hiding -= OnInputPaneHiding;
            _inputPane = null;
        }

        // 真机修复（2026-09-29，Lumia 反馈「字母仍然无效，回车退格有效」）：
        // 9-28 把哨兵设成 InputScope=Password 想换掉中文键盘、走「逐键落进哨兵文本」那条路，
        // 真机上没生效——字母依旧只进输入法组合态，从不落进哨兵，于是差分（连 CompositionEnded
        // 的补差分）拿不到任何东西，而回车/退格走 KeyDown 路照常到达，正是现场症状。
        // 结论：直通模式不再依赖哨兵文本，改用 §7.5 第 1 条实测过的第三条通路
        // CoreWindow.CharacterReceived —— 每次软键盘按键都会来一发，与输入法是否组合无关，
        // 和物理键盘走的是同一个事件。哨兵仍然要在（它负责把 SIP 拉起来、接回车/退格的 KeyDown），
        // 只是它的文本在直通模式下降级成兜底（见 EmitDiff）。
        // 真机诊断（2026-09-29）：三条通路到底哪条在响，只有真机能回答。只记事件名与计数，
        // 绝不记字符、键码或文本（§12.2 脱敏：终端输入可能是 sudo 密码）。Debug 级，
        // 默认不落盘——设置→关于→日志级别=调试 才开。
        private void Diag(string what)
        {
            AppLog.Diag("SoftInput", what + " ime=" + (_imeMode ? 1 : 0)
                + " composing=" + (_composing ? 1 : 0)
                + " focus=" + (HasFocus ? 1 : 0)
                + " direct=" + _direct.Length);
        }

        private void AttachCoreWindow()
        {
            DetachCoreWindow();
            try
            {
                Window current = Window.Current;
                _coreWindow = current != null ? current.CoreWindow : null;
            }
            catch (Exception)
            {
                _coreWindow = null;
            }
            if (_coreWindow != null)
            {
                _coreWindow.CharacterReceived += OnCharacterReceived;
            }
        }

        private void DetachCoreWindow()
        {
            if (_coreWindow == null)
            {
                return;
            }
            _coreWindow.CharacterReceived -= OnCharacterReceived;
            _coreWindow = null;
        }

        private void OnCharacterReceived(CoreWindow sender, CharacterReceivedEventArgs args)
        {
            // 输入法模式下这条路要让开：组合中的字母也会来 CharacterReceived，
            // 直通发出去就和上屏的中文重复了（§7.5 第 3 条仍由哨兵差分负责）。
            //
            // args.Handled 一律不看（2026-09-29 真机日志定论）：焦点在哨兵时 XAML 的文本输入
            // 处理排在本处理器之前并把 Handled 置真——而 Password 输入范围下它只「消费」字符、
            // 不写进 TextBox.Text（真机 55 次按键：55 次 Handled、0 次 TextChanged）。
            // 照 HardwareKeyboardInput 那样按 Handled 早退，等于把每一个字母都丢掉，正是现场症状。
            // 在那边早退是对的（防止重复发送焦点控件已消费的字符），在这里职责相反：
            // 本处理器就是那个「消费者」的替代品，去重由 _direct 记账负责（哨兵真收到文本时
            // 那一份会在 EmitDiff 里被丢弃），不靠 Handled。
            if (_imeMode || args == null || !HasFocus)
            {
                Diag("cr skip reason=" + (args == null ? "null"
                    : _imeMode ? "imemode" : "nofocus"));
                return;
            }
            uint code = args.KeyCode;
            if (code < FirstPrintableCode || code == DeleteCode)
            {
                // 控制字符：回车/退格/Tab 归 KeyDown 路。记「有没有来」，不记是哪个。
                Diag("cr ctrlchar");
                return;
            }
            string text;
            try
            {
                text = char.ConvertFromUtf32((int)code);
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }
            // 记账在发送之前：同一次按键的 TextChanged 紧随其后到达（实测顺序
            // KeyDown → TextChanging → CharacterReceived → TextChanged），据此丢弃哨兵那一份。
            _direct.Append(text);
            ScheduleDirectFlush();
            Diag("cr pass len=" + text.Length);
            EmitText(text);
        }

        // 直通记账只在「本次按键的 TextChanged」这一小段窗口内有效。若哨兵压根没收到文本
        // （正是本次真机症状），TextChanged 不会来、记账也就无人清理——留着会让之后一次
        // 真正需要补发的哨兵新增（联想词、emoji、输入法插短语）被误判成「已发过」而丢掉。
        // 故每轮按键都排一次 Low 优先级清理：已排队的 TextChanged 是 Normal，必定先跑。
        private void ScheduleDirectFlush()
        {
            if (_directFlushQueued || _box == null)
            {
                return;
            }
            _directFlushQueued = true;
            _box.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                _directFlushQueued = false;
                _direct.Length = 0;
            }).AsTask().Forget("SoftKeyboard.DirectFlush", AppLog.Logger);
        }

        private void DetachBox()
        {
            if (_box == null)
            {
                return;
            }
            _box.KeyDown -= OnKeyDown;
            _box.KeyUp -= OnKeyUp;
            _box.TextChanged -= OnTextChanged;
            _box.TextCompositionStarted -= OnCompositionStarted;
            _box.TextCompositionEnded -= OnCompositionEnded;
            _box.GotFocus -= OnGotFocus;
            _box.LostFocus -= OnLostFocus;
            _box = null;
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            Diag("kd " + (e.Key == Windows.System.VirtualKey.Enter ? "enter"
                : e.Key == Windows.System.VirtualKey.Back ? "back" : "other"));
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (_composing)
                {
                    // 组合中回车交给 IME 上屏，由 CompositionEnded 差分发送。
                    return;
                }
                if (_enterHeld)
                {
                    e.Handled = true;
                    return;
                }
                _enterHeld = true;
                e.Handled = true;
                EmitMapped(TerminalKey.Enter);
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Back)
            {
                if (_composing)
                {
                    return;
                }
                e.Handled = true;
                EmitMapped(TerminalKey.Backspace);
            }
        }

        private void OnKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _enterHeld = false;
            }
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_resetting || _box == null || _composing)
            {
                if (!_resetting)
                {
                    Diag("tc skip reason=" + (_box == null ? "nobox" : "composing"));
                }
                return;
            }
            SentinelDiffResult diff = SentinelDiff.Compute(_box.Text);
            Diag("tc ins=" + diff.Inserted.Length + " del=" + diff.Deleted);
            if (!diff.IsEmpty)
            {
                EmitDiff(diff, false);
                ResetSentinel();
                return;
            }
            if (!SentinelDiff.IsRestored(_box.Text))
            {
                ResetSentinel();
            }
        }

        private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
        {
            // 直通模式压根不跟组合态：字符已由 CharacterReceived 逐键发出，而一次没有配对
            // CompositionEnded 的 CompositionStarted 会把 TextChanged 连回车退格一起憋死
            // （OnKeyDown 里组合中一律让给输入法）——那就是「键盘全废」。
            _composing = _imeMode;
            Diag("comp start");
        }

        private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
        {
            _composing = false;
            Diag("comp end");
            if (_resetting || sender == null || !_imeMode)
            {
                return;
            }
            CommitPending(true);
            // IME 可能在 CompositionEnded 之后才把上屏文本写进哨兵：此时上面这次差分为空，
            // 紧随其后的 TextChanged 会因组合标志已清而正常发送；再在下一轮消息循环补一次差分兜底。
            // 两路谁先看到新增谁发送并复位哨兵，另一路看到的是已复位的哨兵，不会重复发送。
            sender.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => CommitPending(true))
                .AsTask().Forget("SoftKeyboard.CommitPending", AppLog.Logger);
        }

        private void CommitPending(bool sendDeletes)
        {
            if (_resetting || _box == null || _composing)
            {
                return;
            }
            SentinelDiffResult diff = SentinelDiff.Compute(_box.Text);
            if (diff.IsEmpty)
            {
                return;
            }
            EmitDiff(diff, sendDeletes);
            ResetSentinel();
        }

        private void ApplyInputScope()
        {
            if (_box == null)
            {
                return;
            }
            var scope = new InputScope();
            scope.Names.Add(new InputScopeName(_imeMode ? InputScopeNameValue.Default : InputScopeNameValue.Password));
            _box.InputScope = scope;
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            Diag("gotfocus");
            if (_box != null && !SentinelDiff.IsRestored(_box.Text) && !_composing)
            {
                ResetSentinel();
            }
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            Diag("lostfocus");
            _enterHeld = false;
            _composing = false;
        }

        private void OnInputPaneShowing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = true;
            SetOcclusion(args.OccludedRect);
        }

        private void OnInputPaneHiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = true;
            SetOcclusion(new Rect());
        }

        private void SetOcclusion(Rect rect)
        {
            AppLog.Diag("SoftInput", "sip occluded y=" + (int)rect.Y + " h=" + (int)rect.Height);
            OccludedRect = rect;
            EventHandler handler = OcclusionChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void EmitDiff(SentinelDiffResult diff, bool sendDeletes)
        {
            if (diff.Inserted.Length > 0)
            {
                // 直通模式：这一串里该发的字符 CharacterReceived 已经逐键发过了，哨兵这一份
                // 一律丢弃（里面还可能夹着输入法的拼音分隔符，如 l's，发出去就是乱码）；
                // 一个直通字符都没来过时才整串补发——联想词、短语、emoji 以及
                // CharacterReceived 不触发的机型全靠这条兜底。
                if (_direct.Length == 0)
                {
                    Diag("diff send len=" + diff.Inserted.Length);
                    EmitText(diff.Inserted);
                }
                else
                {
                    Diag("diff drop len=" + diff.Inserted.Length);
                }
                return;
            }
            if (sendDeletes && diff.ShouldSendDeletes)
            {
                EmitBackspaces(diff.Deleted);
            }
        }

        private void EmitText(string text)
        {
            StickyModifiers sticky = Sticky;
            if (IsSingleCodePoint(text) && sticky != null
                && (sticky.Ctrl != StickyState.Off || sticky.Alt != StickyState.Off)
                && !char.IsSurrogate(text[0]))
            {
                byte[] mapped = KeyMap.Map(
                    sticky.Wrap(TerminalKey.Char, text[0]),
                    Modes ?? new TerminalModes(),
                    BackspaceAsBs);
                if (mapped != null)
                {
                    Emit(mapped);
                }
                return;
            }
            Emit(Encoding.UTF8.GetBytes(text));
        }

        private void EmitBackspaces(int count)
        {
            if (count <= 0)
            {
                return;
            }
            byte value = BackspaceAsBs ? (byte)0x08 : (byte)0x7F;
            var data = new byte[count];
            for (int i = 0; i < count; i++)
            {
                data[i] = value;
            }
            Emit(data);
        }

        private void EmitMapped(TerminalKey key)
        {
            StickyModifiers sticky = Sticky ?? new StickyModifiers();
            byte[] mapped = KeyMap.Map(sticky.Wrap(key), Modes ?? new TerminalModes(), BackspaceAsBs);
            Emit(mapped);
        }

        private void Emit(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }
            EventHandler<TerminalInputEventArgs> handler = Input;
            Diag("emit bytes=" + data.Length + " sink=" + (handler != null ? 1 : 0));
            if (handler != null)
            {
                handler(this, new TerminalInputEventArgs(data));
            }
        }

        private void ResetSentinel()
        {
            if (_box == null)
            {
                return;
            }
            _resetting = true;
            _box.Text = SentinelDiff.Restore();
            _box.SelectionStart = _box.Text.Length;
            _resetting = false;
            _direct.Length = 0;
        }

        private static bool IsSingleCodePoint(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (text.Length == 1)
            {
                return !char.IsSurrogate(text[0]);
            }
            return text.Length == 2 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1]);
        }
    }
}
