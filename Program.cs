// Program.cs - IMEPali
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace IMEPali
{
    internal static class AppConfig
    {
        public static bool ShowKeyboardLayout = true;
        public static bool ShowTextOverlay = true;
        // IsKey2Mode는 Caps Lock 상태 동기화로 대체되므로 제거됨
    }

    #region [ 진입점 (Main) ]
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // IMEPali와 IMEPointer 둘 다 중복 실행 방지
            using Mutex mutexPali = new Mutex(true, "IMEPali_SingleInstance", out bool firstPali);
            using Mutex mutexPointer = new Mutex(true, "IMEPointer_SingleInstance", out bool firstPointer);
            
            if (!firstPali || !firstPointer)
            {
                MessageBox.Show("이미 실행 중입니다.", "IMEPali", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
    #endregion

    #region [ 핵심 로직: PaliMap 및 글자 변환 ]
    internal static class PaliMap
    {
        private static string _lastOutputChar = "";

        // Pali어 자판 매핑 (지정된 영어 위치에만 할당, 나머지는 빈값(null) 처리되어 기본 영어 입력 유지)
        public static readonly Dictionary<int, (string Lower, string Upper)> Map = new()
        {
            { 0x57, ("ś", "Ś") }, { 0x45, ("ṝ", "Ṝ") }, { 0x52, ("ṛ", "Ṛ") }, { 0x54, ("ṭ", "Ṭ") },
            { 0x55, ("ū", "Ū") }, { 0x49, ("ī", "Ī") }, { 0x4F, ("ḹ", "Ḹ") }, { 0x41, ("ā", "Ā") },
            { 0x53, ("ṣ", "Ṣ") }, { 0x44, ("ḍ", "Ḍ") }, { 0x48, ("ḥ", "Ḥ") }, { 0x4A, ("ñ", "Ñ") },
            { 0x4C, ("ḷ", "Ḷ") }, { 0x42, ("ṅ", "Ṅ") }, { 0x4E, ("ṇ", "Ṇ") }, { 0x4D, ("ṃ", "Ṃ") }
        };

        // 전환 카테고리 (0:None, 1:Dot below, 2:Macron, 3:Dot below+Macron, 4:Dot above, 5:Accent, 6:Tilde)
        private static readonly Dictionary<string, string?[]> _paliChains = new()
        {
            {"a", new string?[]{"a", null, "ā", null, null, null, null}},
            {"d", new string?[]{"d", "ḍ", null, null, null, null, null}},
            {"h", new string?[]{"h", "ḥ", null, null, null, null, null}},
            {"i", new string?[]{"i", null, "ī", null, null, null, null}},
            {"l", new string?[]{"l", "ḷ", null, "ḹ", null, null, null}},
            {"m", new string?[]{"m", "ṃ", null, null, null, null, null}},
            {"n", new string?[]{"n", "ṇ", null, null, "ṅ", null, "ñ"}},
            {"t", new string?[]{"t", "ṭ", null, null, null, null, null}},
            {"u", new string?[]{"u", null, "ū", null, null, null, null}},
            {"r", new string?[]{"r", "ṛ", null, "ṝ", null, null, null}},
            {"s", new string?[]{"s", "ṣ", null, null, "ś", null, null}},
        };

        private static readonly Dictionary<string, int> _paliCategoryMap = new();
        private static readonly Dictionary<string, string?[]> _paliReverseChainMap = new();

        static PaliMap()
        {
            _paliChains["s"] = new string?[] { "s", "ṣ", null, null, null, "ś", null };
            
            var upperChains = new Dictionary<string, string?[]>();
            foreach (var kv in _paliChains)
            {
                var upperArr = new string?[7];
                for (int i = 0; i < 7; i++)
                    upperArr[i] = kv.Value[i]?.ToUpper();
                upperChains[kv.Key.ToUpper()] = upperArr;
            }
            foreach (var kv in upperChains) _paliChains[kv.Key] = kv.Value;

            foreach (var kv in _paliChains)
            {
                string?[] chain = kv.Value;
                for (int i = 0; i < 7; i++)
                {
                    if (chain[i] != null)
                    {
                        _paliCategoryMap[chain[i]!] = i;
                        _paliReverseChainMap[chain[i]!] = chain;
                    }
                }
            }
        }

        public static string GetLastOutputChar() => _lastOutputChar;
        public static void SetLastOutputChar(string ch) => _lastOutputChar = ch;

        public static string? GetPaliChar(int vkCode, bool isUpper)
        {
            if (Map.TryGetValue(vkCode, out var val))
            {
                _lastOutputChar = isUpper ? val.Upper : val.Lower;
                MainForm.Instance?.ShowOverlay(_lastOutputChar);
                return _lastOutputChar;
            }
            return null;
        }

        public static void HandlePaliTransformation()
        {
            TextSelectionUtils.TransformAndReplaceText(_lastOutputChar, ApplyPaliTransformation, SetLastOutputChar);
        }

        private static string ApplyPaliTransformation(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            int step = 0;
            // 첫 번째 유효한 문자를 기준으로 변환 단계를 결정합니다.
            foreach (char c in text)
            {
                string s = c.ToString();
                if (_paliCategoryMap.TryGetValue(s, out int cat) && _paliReverseChainMap.TryGetValue(s, out var chain))
                {
                    for (int i = 1; i <= 7; i++)
                    {
                        int next = (cat + i) % 7;
                        if (chain[next] != null) { step = i; break; }
                    }
                    break;
                }
            }
            
            if (step == 0) return text;

            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                string s = c.ToString();
                if (_paliCategoryMap.TryGetValue(s, out int cat) && _paliReverseChainMap.TryGetValue(s, out var chain))
                {
                    int targetCat = -1;
                    for (int i = 1; i <= 7; i++)
                    {
                        int candidate = (cat + i) % 7;
                        if (chain[candidate] != null)
                        {
                            targetCat = candidate;
                            if (i >= step) break; // 동일한 전환 스텝으로 이동 시도
                        }
                    }
                    if (targetCat != -1) sb.Append(chain[targetCat]);
                    else sb.Append(s);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
    #endregion

    #region [ 글로벌 입력 훅 (GlobalInputHook) ]
    internal static class GlobalInputHook
    {
        public static volatile bool IsSending = false;
        private static IntPtr _hookID = IntPtr.Zero;
        private static NativeMethods.LowLevelKeyboardProc _proc = HookCallback;
        
        private static bool _isHanjaDown = false;
        private static bool _hanjaUsedForTyping = false;

        public static void Start()
        {
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _hookID = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(curModule!.ModuleName), 0);
            }
        }

        public static void Stop()
        {
            NativeMethods.UnhookWindowsHookEx(_hookID);
            _hookID = IntPtr.Zero;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !IsSending)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool isKeyDown = (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104);
                bool isKeyUp = (wParam == (IntPtr)0x0101 || wParam == (IntPtr)0x0105);

                if (vkCode == 0x19 || vkCode == 0xA3)
                {
                    if (isKeyDown)
                    {
                        if (!_isHanjaDown) { _isHanjaDown = true; _hanjaUsedForTyping = false; }
                        return (IntPtr)1; 
                    }
                    else if (isKeyUp)
                    {
                        if (_isHanjaDown)
                        {
                            _isHanjaDown = false;
                            if (!_hanjaUsedForTyping)
                            {
                                PaliMap.HandlePaliTransformation();
                            }
                        }
                        return (IntPtr)1; 
                    }
                }

                if (isKeyDown)
                {
                    if (vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1 || vkCode == 0x14) 
                    {
                        // 수식어 키는 통과
                    }
                    else if (_isHanjaDown)
                    {
                        _hanjaUsedForTyping = true;
                        bool isShift = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
                        bool capsOn = (NativeMethods.GetKeyState(0x14) & 0x0001) != 0;
                        bool isUpper = isShift ^ capsOn;

                        string? result = PaliMap.GetPaliChar(vkCode, isUpper);
                        if (result != null)
                        {
                            IsSending = true;
                            NativeMethods.SendUnicodeString(result);
                            IsSending = false;
                            return (IntPtr)1; 
                        }
                    }
                    else
                    {
                        // 그 외 다른 키보드 입력이 들어오면 단일 문자 PE 전환 기록 초기화
                        PaliMap.SetLastOutputChar("");
                    }
                }

                // Caps Lock(0x14) 입력 시에만 배열창 UI 업데이트를 호출하도록 변경
                if (AppConfig.ShowKeyboardLayout && vkCode == 0x14)
                {
                    MainForm.Instance?.UpdateKeyboardLayoutState();
                }
            }
            return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public static void SendReplacement(int backspaces, string text)
        {
            IsSending = true;
            var inputs = new List<NativeMethods.INPUT>();
            bool shiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            
            if (shiftHeld) inputs.Add(MakeKeyUp(0x10));
            
            for (int i = 0; i < backspaces; i++)
            {
                inputs.Add(MakeKeyDown(0x08));
                inputs.Add(MakeKeyUp(0x08));
            }
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
            
            if (text.Length > 0) NativeMethods.SendUnicodeString(text);
            
            // 대문자 입력 시 Shift 키가 1회만 눌린 것으로 인식되도록 Shift 재활성화 코드 제거
            IsSending = false;
        }

        public static NativeMethods.INPUT MakeKeyDown(ushort vk) => new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk } } };
        public static NativeMethods.INPUT MakeKeyUp(ushort vk) => new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = 0x0002 } } };
    }
    #endregion

    #region [ 텍스트 선택/복사 유틸리티 ]
    internal static class TextSelectionUtils
    {
        public static volatile bool IsConverting = false;

        public static void TransformAndReplaceText(string lastOutputChar, Func<string, string> transformFunc, Action<string> setLastOutputChar)
        {
            if (IsConverting) return;
            IsConverting = true;
            
            Thread thread = new Thread(() =>
            {
                try
                {
                    string? selected = ReadSelectedText(out bool isUia);
                    
                    if (!string.IsNullOrEmpty(selected))
                    {
                        string toggled = transformFunc(selected);
                        if (toggled != selected)
                        {
                            MainForm.Instance?.ShowOverlay($"{selected[0]}→{toggled[0]}");
                            setLastOutputChar(toggled.Length == 1 ? toggled : "");
                            GlobalInputHook.SendReplacement(0, toggled);
                        }
                        else if (!isUia) CancelSelection();
                    }
                    else if (!string.IsNullOrEmpty(lastOutputChar))
                    {
                        string toggled = transformFunc(lastOutputChar);
                        if (toggled != lastOutputChar)
                        {
                            MainForm.Instance?.ShowOverlay($"{lastOutputChar[0]}→{toggled[0]}");
                            setLastOutputChar(toggled);
                            GlobalInputHook.SendReplacement(1, toggled);
                        }
                    }
                }
                catch { }
                finally { IsConverting = false; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private static string? ReadSelectedText(out bool isUia)
        {
            isUia = false;
            try
            {
                var focusedElement = AutomationElement.FocusedElement;
                if (focusedElement != null && focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj))
                {
                    var selections = ((TextPattern)patternObj).GetSelection();
                    if (selections != null && selections.Length > 0)
                    {
                        string text = selections[0].GetText(-1).Trim('\r', '\n', '\t', ' ', '\0');
                        if (text.Length > 0)
                        {
                            isUia = true;
                            return text;
                        }
                    }
                }
            }
            catch { }

            // Fallback: Ctrl+C Win32 Clipboard
            bool shiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            string? saved = GetTextWin32();
            try
            {
                ClearWin32();
                SendCtrlC(shiftHeld);
                string? copied = null;
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(20);
                    copied = GetTextWin32();
                    if (!string.IsNullOrEmpty(copied)) break;
                }
                RestoreClipboardAsync(saved);
                return string.IsNullOrEmpty(copied) ? null : copied.Trim('\r', '\n', '\t', ' ', '\0');
            }
            catch { return null; }
        }

        private static void SendCtrlC(bool shiftHeld)
        {
            GlobalInputHook.IsSending = true;
            var inputs = new List<NativeMethods.INPUT>();
            if (shiftHeld) inputs.Add(new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x10, dwFlags = 0x0002 } } });
            inputs.Add(new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x11 } } });
            inputs.Add(new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x43 } } });
            inputs.Add(new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x43, dwFlags = 0x0002 } } });
            inputs.Add(new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x11, dwFlags = 0x0002 } } });
            if (shiftHeld) inputs.Add(new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x10 } } });
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
            GlobalInputHook.IsSending = false;
        }
        
        private static void CancelSelection()
        {
            GlobalInputHook.IsSending = true;
            var inputs = new List<NativeMethods.INPUT>();
            inputs.Add(new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x27 } } });
            inputs.Add(new NativeMethods.INPUT { type = 1, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = 0x27, dwFlags = 0x0002 } } });
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
            GlobalInputHook.IsSending = false;
        }

        private static string? GetTextWin32()
        {
            try
            {
                if (!NativeMethods.IsClipboardFormatAvailable(13)) return null;
                if (!NativeMethods.OpenClipboard(IntPtr.Zero)) return null;
                string? result = null;
                IntPtr hGlobal = NativeMethods.GetClipboardData(13);
                if (hGlobal != IntPtr.Zero)
                {
                    IntPtr ptr = NativeMethods.GlobalLock(hGlobal);
                    if (ptr != IntPtr.Zero) { result = Marshal.PtrToStringUni(ptr); NativeMethods.GlobalUnlock(hGlobal); }
                }
                NativeMethods.CloseClipboard();
                return result;
            }
            catch { return null; }
        }

        private static void ClearWin32()
        {
            try { if (NativeMethods.OpenClipboard(IntPtr.Zero)) { NativeMethods.EmptyClipboard(); NativeMethods.CloseClipboard(); } } catch { }
        }

        private static void RestoreClipboardAsync(string? savedText)
        {
            Task.Run(() => {
                Thread.Sleep(400);
                Thread thread = new Thread(() => {
                    try { if (!string.IsNullOrEmpty(savedText)) Clipboard.SetText(savedText); else Clipboard.Clear(); } catch { }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            });
        }
    }
    #endregion

    #region [ 메인 폼 (MainForm) 및 트레이 제어 ]
    internal class MainForm : Form
    {
        public static MainForm? Instance { get; private set; }
        
        private NotifyIcon _trayIcon = null!;
        private ContextMenuStrip _trayMenu = null!;
        
        private TextOverlayForm? _textOverlay;
        private KeyboardLayoutForm? _kbdLayoutForm;

        // [이번 수정 부분 시작: 폴링 타이머 및 상태 변수 추가]
        private System.Windows.Forms.Timer _stateTimer = null!;
        private bool _lastHangulState = false;
        // [이번 수정 부분 끝]

        public MainForm()
        {
            Instance = this;
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.Hide();
            _ = this.Handle; // 강제 핸들 생성

            InitializeTray();
            GlobalInputHook.Start();

            // [이번 수정 부분 시작: 폴링 타이머 시작]
            _stateTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _stateTimer.Tick += StateTimer_Tick;
            _stateTimer.Start();
            // [이번 수정 부분 끝]
        }

        // [이번 수정 부분 시작: IME 상태 감지 및 타이머 콜백]
        private void StateTimer_Tick(object? sender, EventArgs e)
        {
            bool currentHangul = CheckHangulMode();
            if (currentHangul != _lastHangulState)
            {
                _lastHangulState = currentHangul;
                UpdateTrayIcon(_lastHangulState);
            }
        }

        private bool CheckHangulMode()
        {
            IntPtr hFore = NativeMethods.GetForegroundWindow();
            if (hFore == IntPtr.Zero) return false;
            
            uint tid = NativeMethods.GetWindowThreadProcessId(hFore, out _);
            NativeMethods.GUITHREADINFO gti = new() { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            IntPtr focusWnd = hFore;
            
            if (NativeMethods.GetGUIThreadInfo(tid, ref gti))
            {
                if (gti.hwndFocus != IntPtr.Zero) focusWnd = gti.hwndFocus;
                else if (gti.hwndActive != IntPtr.Zero) focusWnd = gti.hwndActive;
            }

            IntPtr hImeWnd = NativeMethods.ImmGetDefaultIMEWnd(focusWnd);
            if (hImeWnd == IntPtr.Zero) hImeWnd = NativeMethods.ImmGetDefaultIMEWnd(hFore);

            if (hImeWnd != IntPtr.Zero)
            {
                NativeMethods.SendMessageTimeout(
                    hImeWnd, 
                    NativeMethods.WM_IME_CONTROL, 
                    (IntPtr)NativeMethods.IMC_GETCONVERSIONMODE, 
                    IntPtr.Zero, 
                    NativeMethods.SMTO_ABORTIFHUNG, 
                    20, 
                    out IntPtr result);
                
                uint mode = (uint)result.ToInt64();
                return (mode & NativeMethods.IME_CMODE_NATIVE) != 0;
            }
            return false;
        }
        // [이번 수정 부분 끝]

        private void InitializeTray()
        {
            _trayMenu = new ContextMenuStrip();
            
            var titleItem = new ToolStripMenuItem("IMEPali (Pali/Sanskrit)") { Enabled = false };
            titleItem.Font = new Font(titleItem.Font, FontStyle.Bold);
            _trayMenu.Items.Add(titleItem);
            _trayMenu.Items.Add(new ToolStripMenuItem("한자키+영어 입력/전환 기능") { Enabled = false });
            _trayMenu.Items.Add(new ToolStripSeparator());

            var kbdMenu = new ToolStripMenuItem("Pali어 키보드 배열창", null, (s, e) => {
                AppConfig.ShowKeyboardLayout = !((ToolStripMenuItem)s!).Checked;
                ((ToolStripMenuItem)s).Checked = AppConfig.ShowKeyboardLayout;
                if (!AppConfig.ShowKeyboardLayout) _kbdLayoutForm?.Hide();
                else UpdateKeyboardLayoutState();
            }) { Checked = AppConfig.ShowKeyboardLayout };
            _trayMenu.Items.Add(kbdMenu);

            var txtMenu = new ToolStripMenuItem("Pali어 입력문자 표시창", null, (s, e) => {
                AppConfig.ShowTextOverlay = !((ToolStripMenuItem)s!).Checked;
                ((ToolStripMenuItem)s).Checked = AppConfig.ShowTextOverlay;
                if (!AppConfig.ShowTextOverlay) _textOverlay?.Clear();
            }) { Checked = AppConfig.ShowTextOverlay };
            _trayMenu.Items.Add(txtMenu);

            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(new ToolStripMenuItem("종료(Exit)", null, (s, e) => Application.Exit()));

            _trayIcon = new NotifyIcon
            {
                ContextMenuStrip = _trayMenu,
                Visible = true,
                Text = "IMEPali"
            };

            // 좌/우 클릭 모두 메뉴 활성화 처리
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                {
                    NativeMethods.SetForegroundWindow(this.Handle);
                    _trayMenu.Show(Cursor.Position);
                }
            };
            
            // [이번 수정 부분 시작: 초기 상태를 가져와 아이콘 그림]
            _lastHangulState = CheckHangulMode();
            UpdateTrayIcon(_lastHangulState);
            // [이번 수정 부분 끝]
        }

        // [이번 수정 부분 시작: 한/영 상태에 따른 색상 동적 변경 및 Y축 중앙 정렬 보정]
        private void UpdateTrayIcon(bool isHangul)
        {
            int size = 16;
            using Bitmap bmp = new Bitmap(size, size);
            using Graphics g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            
            g.Clear(Color.Black);
            
            Color textColor = isHangul ? Color.White : Color.Orange;
            using Brush tBrush = new SolidBrush(textColor);
            
            StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            
            // Y축을 1.5f 픽셀 아래로 내려 시각적으로 완벽한 상하 중앙 정렬을 맞춤
            g.DrawString("P", new Font("Arial", 10, FontStyle.Bold), tBrush, new RectangleF(0, 1.5f, size, size), sf);

            IntPtr hIcon = bmp.GetHicon();
            Icon? oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = Icon.FromHandle(hIcon);
            if (oldIcon != null) NativeMethods.DestroyIcon(oldIcon.Handle);
        }
        // [이번 수정 부분 끝]

        public void ShowOverlay(string text)
        {
            if (!AppConfig.ShowTextOverlay) return;
            this.Invoke((MethodInvoker)delegate
            {
                if (_textOverlay == null || _textOverlay.IsDisposed) _textOverlay = new TextOverlayForm();
                
                Point caretPos = GetInputCaretPosition();
                int width = Math.Max(40, text.Length * 15 + 24);
                int height = 52;
                
                _textOverlay.ShowOverlay(text, true, 22f, width, height, caretPos.X, caretPos.Y + 40);
            });
        }

        private Point GetInputCaretPosition()
        {
            IntPtr hFore = NativeMethods.GetForegroundWindow();
            uint tid = NativeMethods.GetWindowThreadProcessId(hFore, out _);
            NativeMethods.GUITHREADINFO gti = new() { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            if (NativeMethods.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero)
            {
                NativeMethods.POINT pt = new() { X = gti.rectLeft, Y = gti.rectBottom };
                NativeMethods.ClientToScreen(gti.hwndCaret, ref pt);
                return new Point(pt.X, pt.Y);
            }
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT mPt)) return new Point(mPt.X, mPt.Y);
            return Point.Empty;
        }

        public void UpdateKeyboardLayoutState()
        {
            this.Invoke((MethodInvoker)delegate
            {
                if (!AppConfig.ShowKeyboardLayout)
                {
                    _kbdLayoutForm?.Hide();
                    return;
                }
                
                // Caps Lock 키에만 반응하여 Key1, Key2를 판단하도록 변경
                bool capsOn = (NativeMethods.GetKeyState(0x14) & 1) != 0;
                bool showKey2 = capsOn;

                if (_kbdLayoutForm == null || _kbdLayoutForm.IsDisposed)
                {
                    _kbdLayoutForm = new KeyboardLayoutForm();
                    _kbdLayoutForm.OnLayoutDoubleClicked += (s, e) =>
                    {
                        // 배열창 더블클릭 시 시스템 Caps Lock 자체를 토글 (가상 키 전송)
                        GlobalInputHook.IsSending = true;
                        var inputs = new NativeMethods.INPUT[]
                        {
                            GlobalInputHook.MakeKeyDown(0x14),
                            GlobalInputHook.MakeKeyUp(0x14)
                        };
                        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
                        GlobalInputHook.IsSending = false;

                        // UI 업데이트를 위해 딜레이를 두고 다시 호출
                        Task.Delay(50).ContinueWith(_ => UpdateKeyboardLayoutState());
                    };
                    _kbdLayoutForm.OnClosedByUser += (s, e) =>
                    {
                        AppConfig.ShowKeyboardLayout = false;
                        foreach (ToolStripItem item in _trayMenu.Items)
                            if (item.Text == "Pali어 키보드 배열창") ((ToolStripMenuItem)item).Checked = false;
                    };
                }
                
                _kbdLayoutForm.UpdateImage(showKey2 ? "IMEPali.images.PaliKey2.png" : "IMEPali.images.PaliKey1.png");
                
                if (!_kbdLayoutForm.Visible)
                {
                    _kbdLayoutForm.Show();
                    if (_kbdLayoutForm.WindowState == FormWindowState.Minimized)
                        _kbdLayoutForm.WindowState = FormWindowState.Normal;
                }
            });
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // [이번 수정 부분 시작: 타이머 정리 추가]
            _stateTimer?.Stop();
            _stateTimer?.Dispose();
            // [이번 수정 부분 끝]
            
            GlobalInputHook.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            base.OnFormClosing(e);
        }
    }
    #endregion

    #region [ 자판 배열창 및 텍스트 오버레이 폼 ]
    public class KeyboardLayoutForm : Form
    {
        private readonly PictureBox _pictureBox;
        public event EventHandler? OnLayoutDoubleClicked;
        public event EventHandler? OnClosedByUser;
        private string _currentImageName = "";

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00020000;   // WS_MINIMIZEBOX
                cp.Style |= 0x00080000;   // WS_SYSMENU
                cp.ExStyle |= 0x00040000; // WS_EX_APPWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }
        protected override bool ShowWithoutActivation => true;

        public KeyboardLayoutForm()
        {
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = true;
            this.ShowInTaskbar = true;
            this.TopMost = true; 
            this.Text = "Pali어 키보드 배열창";
            
            int screenWidth = Screen.PrimaryScreen?.WorkingArea.Width ?? 800;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(Math.Max(0, (screenWidth - this.Width) / 2), 50);

            try 
            { 
                var assembly = typeof(Program).Assembly;
                using Stream? stream = assembly.GetManifestResourceStream("IMEPali.images.IMEPali.ico");
                if (stream != null) this.Icon = new Icon(stream);
            } catch { }

            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White
            };
            _pictureBox.DoubleClick += (s, e) => OnLayoutDoubleClicked?.Invoke(this, EventArgs.Empty);
            this.Controls.Add(_pictureBox);
        }

        public void UpdateImage(string resourceName)
        {
            if (_currentImageName == resourceName) return;
            _currentImageName = resourceName;

            try
            {
                using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    Image? oldImg = _pictureBox.Image;
                    Image newImg = Image.FromStream(stream);
                    _pictureBox.Image = newImg;
                    
                    if (this.WindowState == FormWindowState.Normal)
                        this.ClientSize = newImg.Size;
                    
                    oldImg?.Dispose();
                }
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                OnClosedByUser?.Invoke(this, EventArgs.Empty);
            }
            base.OnFormClosing(e);
        }
    }

    public class TextOverlayForm : Form
    {
        private readonly System.Windows.Forms.Timer _hideTimer;
        private string _text = "";
        private float _fontSize = 22f;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }
        protected override bool ShowWithoutActivation => true;

        public TextOverlayForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.Black;
            this.ForeColor = Color.Orange;
            this.TopMost = true;
            this.ShowInTaskbar = false;

            _hideTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _hideTimer.Tick += (s, e) => this.Hide();
            
            this.Paint += TextOverlayForm_Paint;
        }

        public void ShowOverlay(string text, bool useTimer, float fontSize, int width, int height, int x, int y)
        {
            _text = text;
            _fontSize = fontSize;
            this.Size = new Size(width, height);
            this.Location = new Point(x, y);
            
            if (useTimer) { _hideTimer.Stop(); _hideTimer.Start(); }
            else _hideTimer.Stop();
            
            if (!this.Visible) this.Show(); 
            this.Invalidate();
        }

        private void TextOverlayForm_Paint(object? sender, PaintEventArgs e)
        {
            using Font f = new Font("Malgun Gothic", _fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(e.Graphics, _text, f, this.ClientRectangle, Color.Orange, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        
        public void Clear()
        {
            _hideTimer.Stop();
            this.Hide();
        }
    }
    #endregion

    #region [ NativeMethods ]
    internal static class NativeMethods
    {
        public const int WH_KEYBOARD_LL = 13;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        public static void SendUnicodeString(string text)
        {
            var inputs = new List<INPUT>();
            bool shiftHeld = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            
            if (shiftHeld) inputs.Add(GlobalInputHook.MakeKeyUp(0x10));
            
            foreach (char c in text)
            {
                inputs.Add(new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 0x0004 } } }); // KEYEVENTF_UNICODE
                inputs.Add(new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 0x0004 | 0x0002 } } }); // KEYUP
            }
            
            // 대문자 입력 시 Shift 키가 1회만 눌린 것으로 인식되도록 Shift 재활성화 코드 제거
            
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); 
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize; public int flags; public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture; public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret; public int rectLeft; public int rectTop; public int rectRight; public int rectBottom; }

        [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] public static extern bool CloseClipboard();
        [DllImport("user32.dll")] public static extern bool EmptyClipboard();
        [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GlobalUnlock(IntPtr hMem);

        // [이번 수정 부분 시작: IME 감지 API 정의 추가]
        [DllImport("imm32.dll")] 
        public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
        
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] 
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        public const int WM_IME_CONTROL = 0x0283;
        public const int IMC_GETCONVERSIONMODE = 0x0001;
        public const uint IME_CMODE_NATIVE = 0x0001;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        // [이번 수정 부분 끝]
    }
    #endregion
}