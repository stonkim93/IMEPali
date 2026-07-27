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
using System.Linq;

namespace IMEPali
{
    #region [ 1. 앱 환경 설정 (AppConfig) ]
    /// <summary>
    /// 애플리케이션 전반에서 사용되는 사용자 및 환경 설정 클래스입니다.
    /// </summary>
    internal static class AppConfig
    {
        public static bool ShowKeyboardLayout = true;
        public static bool ShowTextOverlay = true;
        
        // Pali어 전환용 트리거 키 (0x19: 한자키, 0xA3: 우측 Ctrl키)
        public static readonly int[] ToggleKeyCodes = { 0x19, 0xA3 };
        
        // 트레이 아이콘 한/영 상태 갱신 폴링 주기 (ms)
        public static readonly int TrayUpdateIntervalMs = 100;

        // 트레이 메뉴의 GitHub 링크
        public static readonly string GithubRepositoryUrl = "https://github.com/stonkim93/IMEPali";
    }
    #endregion

    #region [ 2. 진입점 (Main) ]
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // IMEPointer 및 IMEPali 중복 실행 방지
            // MS Store 패키징(AppContainer) 환경을 고려하여 Global\ 접두사 추가
            using Mutex mutexPointer = new Mutex(true, @"Global\IMEPointer_SingleInstance", out bool isPointerFirst);
            using Mutex mutexPali = new Mutex(true, @"Global\IMEPali_SingleInstance", out bool isPaliFirst);

            if (!isPointerFirst || !isPaliFirst)
            {
                MessageBox.Show("IMEPali 앱이 이미 실행 중입니다.", "IMEPali", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayMainForm());
        }
    }
    #endregion

    #region [ 3. 핵심 로직: Pali어 변환기 (PaliMap) ]
    /// <summary>
    /// 영문 키보드 입력을 Pali어 및 특수 기호로 매핑하고 변환하는 핵심 로직 클래스입니다.
    /// </summary>
    internal static class PaliMap
    {
        private static string _lastOutputChar = "";

        /// <summary>
        /// 특정 영문자 키보드 가상 키코드에 대응하는 Pali어 (소문자, 대문자) 매핑
        /// </summary>
        public static readonly Dictionary<int, (string Lower, string Upper)> KeyMap = new()
        {
            { 0x57, ("ś", "Ś") }, { 0x45, ("ṝ", "Ṝ") }, { 0x52, ("ṛ", "Ṛ") }, { 0x54, ("ṭ", "Ṭ") },
            { 0x55, ("ū", "Ū") }, { 0x49, ("ī", "Ī") }, { 0x4F, ("ḹ", "Ḹ") }, { 0x41, ("ā", "Ā") },
            { 0x53, ("ṣ", "Ṣ") }, { 0x44, ("ḍ", "Ḍ") }, { 0x48, ("ḥ", "Ḥ") }, { 0x4A, ("ñ", "Ñ") },
            { 0x4C, ("ḷ", "Ḷ") }, { 0x42, ("ṅ", "Ṅ") }, { 0x4E, ("ṇ", "Ṇ") }, { 0x4D, ("ṃ", "Ṃ") }
        };

        // 7단계 변환 카테고리
        private static readonly Dictionary<string, string?[]> _transformationChains = new()
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

        private static readonly Dictionary<string, int> _categoryMap = new();
        private static readonly Dictionary<string, string?[]> _reverseChainMap = new();

        static PaliMap()
        {
            _transformationChains["s"] = new string?[] { "s", "ṣ", null, null, null, "ś", null };
            
            // 대문자 체인 생성
            var upperChains = new Dictionary<string, string?[]>();
            foreach (var kv in _transformationChains)
            {
                var upperArr = new string?[7];
                for (int i = 0; i < 7; i++)
                    upperArr[i] = kv.Value[i]?.ToUpper();
                upperChains[kv.Key.ToUpper()] = upperArr;
            }
            foreach (var kv in upperChains) _transformationChains[kv.Key] = kv.Value;

            // 카테고리 및 역참조 매핑 최적화
            foreach (var kv in _transformationChains)
            {
                string?[] chain = kv.Value;
                for (int i = 0; i < 7; i++)
                {
                    if (chain[i] != null)
                    {
                        _categoryMap[chain[i]!] = i;
                        _reverseChainMap[chain[i]!] = chain;
                    }
                }
            }
        }

        public static string GetLastOutputChar() => _lastOutputChar;
        public static void SetLastOutputChar(string ch) => _lastOutputChar = ch;

        /// <summary>
        /// 키코드를 기반으로 Pali어 문자를 반환합니다.
        /// </summary>
        public static string? GetPaliCharacter(int virtualKeyCode, bool isUpperCase)
        {
            if (KeyMap.TryGetValue(virtualKeyCode, out var val))
            {
                _lastOutputChar = isUpperCase ? val.Upper : val.Lower;
                TrayMainForm.Instance?.ShowOverlay(_lastOutputChar);
                return _lastOutputChar;
            }
            return null;
        }

        public static void ProcessTransformation()
        {
            ClipboardUtility.TransformAndReplaceSelectedText(_lastOutputChar, ApplyTransformationRules, SetLastOutputChar);
        }

        private static string ApplyTransformationRules(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            int targetCategoryIndex = -1;
            
            foreach (char c in text)
            {
                string s = c.ToString();
                if (_categoryMap.TryGetValue(s, out int cat) && _reverseChainMap.TryGetValue(s, out var chain))
                {
                    for (int i = 1; i <= 7; i++)
                    {
                        int next = (cat + i) % 7;
                        if (chain[next] != null) 
                        { 
                            targetCategoryIndex = next; 
                            break; 
                        }
                    }
                    break;
                }
            }
            
            if (targetCategoryIndex == -1) return text;

            StringBuilder resultBuilder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                string s = c.ToString();
                if (_categoryMap.TryGetValue(s, out _) && _reverseChainMap.TryGetValue(s, out var chain))
                {
                    resultBuilder.Append(chain[targetCategoryIndex] ?? s);
                }
                else
                {
                    resultBuilder.Append(c);
                }
            }
            return resultBuilder.ToString();
        }
    }
    #endregion

    #region [ 4. 글로벌 키보드 훅 (KeyboardHookManager) ]
    /// <summary>
    /// OS 수준에서 키보드 입력을 가로채어 Pali어 입력 상태를 관리합니다.
    /// </summary>
    internal static class KeyboardHookManager
    {
        public static volatile bool IsSendingInput = false;
        private static IntPtr _hookID = IntPtr.Zero;
        private static NativeMethods.LowLevelKeyboardProc _hookProcedure = HookCallback;
        
        private static bool _isToggleKeyDown = false;
        private static bool _toggleKeyUsedForTyping = false;

        public static void InitializeHook()
        {
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            // PublishSingleFile=true 환경에서 GetModuleHandle 호환성을 위해 null 전달 (현재 프로세스 기준)
            _hookID = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, 
                _hookProcedure, 
                NativeMethods.GetModuleHandle(null), 
                0);
        }

        public static void ReleaseHook()
        {
            if (_hookID != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && !IsSendingInput)
                {
                    int virtualKeyCode = Marshal.ReadInt32(lParam);
                    bool isKeyDown = (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104);
                    bool isKeyUp = (wParam == (IntPtr)0x0101 || wParam == (IntPtr)0x0105);

                    // 지정된 Toggle 키 (한자키, RCtrl) 인지 확인
                    if (AppConfig.ToggleKeyCodes.Contains(virtualKeyCode))
                    {
                        if (isKeyDown)
                        {
                            if (!_isToggleKeyDown) { _isToggleKeyDown = true; _toggleKeyUsedForTyping = false; }
                            return (IntPtr)1; 
                        }
                        else if (isKeyUp)
                        {
                            if (_isToggleKeyDown)
                            {
                                _isToggleKeyDown = false;
                                if (!_toggleKeyUsedForTyping)
                                {
                                    PaliMap.ProcessTransformation();
                                }
                            }
                            return (IntPtr)1; 
                        }
                    }

                    if (isKeyDown)
                    {
                        if (virtualKeyCode == 0x10 || virtualKeyCode == 0xA0 || virtualKeyCode == 0xA1 || virtualKeyCode == 0x14) 
                        {
                        }
                        else if (_isToggleKeyDown)
                        {
                            _toggleKeyUsedForTyping = true;
                            bool isShiftDown = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
                            bool isCapsOn = (NativeMethods.GetKeyState(0x14) & 0x0001) != 0;
                            bool isUpperCase = isShiftDown ^ isCapsOn;

                            string? mappedChar = PaliMap.GetPaliCharacter(virtualKeyCode, isUpperCase);
                            if (mappedChar != null)
                            {
                                SendString(mappedChar);
                                return (IntPtr)1; 
                            }
                        }
                        else
                        {
                            if (virtualKeyCode >= 0x41 && virtualKeyCode <= 0x5A) // A~Z
                            {
                                bool isShiftDown = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
                                bool isCapsOn = (NativeMethods.GetKeyState(0x14) & 0x0001) != 0;
                                bool isUpperCase = isShiftDown ^ isCapsOn;
                                char c = isUpperCase ? (char)virtualKeyCode : (char)(virtualKeyCode + 32);
                                PaliMap.SetLastOutputChar(c.ToString());
                            }
                            else
                            {
                                PaliMap.SetLastOutputChar(""); 
                            }
                        }
                    }

                    if (AppConfig.ShowKeyboardLayout && virtualKeyCode == 0x14)
                    {
                        TrayMainForm.Instance?.UpdateKeyboardLayoutVisibility();
                    }
                }
            }
            catch
            {
                // 후킹 루프 내 예외 방어
            }
            return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public static void SendReplacementText(int backspaces, string text)
        {
            IsSendingInput = true;
            var inputList = new List<NativeMethods.INPUT>();
            bool isShiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            
            if (isShiftHeld) inputList.Add(CreateKeyInput(0x10, true));
            
            for (int i = 0; i < backspaces; i++)
            {
                inputList.Add(CreateKeyInput(0x08, false)); 
                inputList.Add(CreateKeyInput(0x08, true));  
            }
            
            NativeMethods.SendInput((uint)inputList.Count, inputList.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
            
            if (text.Length > 0) NativeMethods.SendUnicodeString(text);
            
            IsSendingInput = false;
        }

        private static void SendString(string text)
        {
            IsSendingInput = true;
            NativeMethods.SendUnicodeString(text);
            IsSendingInput = false;
        }

        public static NativeMethods.INPUT CreateKeyInput(ushort virtualKey, bool isKeyUp)
        {
            return new NativeMethods.INPUT
            {
                type = 1,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = virtualKey,
                        dwFlags = isKeyUp ? 0x0002u : 0x0000u
                    }
                }
            };
        }
    }
    #endregion

    #region [ 5. 텍스트/클립보드 제어 유틸리티 (ClipboardUtility) ]
    /// <summary>
    /// UI Automation 및 Clipboard API를 활용하여 선택된 텍스트를 읽고 변경합니다.
    /// </summary>
    internal static class ClipboardUtility
    {
        public static volatile bool IsProcessing = false;

        public static void TransformAndReplaceSelectedText(string lastOutputChar, Func<string, string> transformationFunc, Action<string> updateLastCharAction)
        {
            if (IsProcessing) return;
            IsProcessing = true;
            
            // UI Automation 및 Clipboard API는 STA 스레드에서 실행되어야 함
            Thread workerThread = new Thread(() =>
            {
                try
                {
                    string? selectedText = ReadSelectedText(out bool isUiaRetrieved);
                    
                    if (!string.IsNullOrEmpty(selectedText))
                    {
                        string transformed = transformationFunc(selectedText);
                        if (transformed != selectedText)
                        {
                            TrayMainForm.Instance?.Invoke((MethodInvoker)delegate {
                                TrayMainForm.Instance.ShowOverlay($"{selectedText[0]}→{transformed[0]}");
                            });
                            updateLastCharAction(transformed.Length == 1 ? transformed : "");
                            KeyboardHookManager.SendReplacementText(0, transformed);
                        }
                        else if (!isUiaRetrieved)
                        {
                            CancelTextSelection();
                        }
                    }
                    else if (!string.IsNullOrEmpty(lastOutputChar))
                    {
                        string transformed = transformationFunc(lastOutputChar);
                        if (transformed != lastOutputChar)
                        {
                            TrayMainForm.Instance?.Invoke((MethodInvoker)delegate {
                                TrayMainForm.Instance.ShowOverlay($"{lastOutputChar[0]}→{transformed[0]}");
                            });
                            updateLastCharAction(transformed);
                            KeyboardHookManager.SendReplacementText(1, transformed);
                        }
                    }
                }
                catch (Exception) 
                { 
                    // 로깅 로직 추가 권장 (예: Debug.WriteLine)
                }
                finally { IsProcessing = false; }
            });

            workerThread.SetApartmentState(ApartmentState.STA); // 필수 설정
            workerThread.Start();
        }
        
        private static string? ReadSelectedText(out bool isUiaRetrieved)
        {
            isUiaRetrieved = false;
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
                            isUiaRetrieved = true;
                            return text;
                        }
                    }
                }
            }
            catch { }

            bool isShiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            string? backupClipboardText = GetClipboardText();
            try
            {
                ClearClipboard();
                SendCtrlC(isShiftHeld);
                string? copiedText = null;
                
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(20);
                    copiedText = GetClipboardText();
                    if (!string.IsNullOrEmpty(copiedText)) break;
                }
                
                RestoreClipboardTextAsync(backupClipboardText);
                return string.IsNullOrEmpty(copiedText) ? null : copiedText.Trim('\r', '\n', '\t', ' ', '\0');
            }
            catch { return null; }
        }

        private static void SendCtrlC(bool isShiftHeld)
        {
            KeyboardHookManager.IsSendingInput = true;
            var inputs = new List<NativeMethods.INPUT>();
            
            if (isShiftHeld) inputs.Add(KeyboardHookManager.CreateKeyInput(0x10, true));
            
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x11, false));
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x43, false));
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x43, true));
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x11, true));
            
            if (isShiftHeld) inputs.Add(KeyboardHookManager.CreateKeyInput(0x10, false));
            
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
            KeyboardHookManager.IsSendingInput = false;
        }
        
        private static void CancelTextSelection()
        {
            KeyboardHookManager.IsSendingInput = true;
            var inputs = new NativeMethods.INPUT[]
            {
                KeyboardHookManager.CreateKeyInput(0x27, false),
                KeyboardHookManager.CreateKeyInput(0x27, true)
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            KeyboardHookManager.IsSendingInput = false;
        }

        private static string? GetClipboardText()
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
                    if (ptr != IntPtr.Zero) 
                    { 
                        result = Marshal.PtrToStringUni(ptr); 
                        NativeMethods.GlobalUnlock(hGlobal); 
                    }
                }
                NativeMethods.CloseClipboard();
                return result;
            }
            catch { return null; }
        }

        private static void ClearClipboard()
        {
            try 
            { 
                if (NativeMethods.OpenClipboard(IntPtr.Zero)) 
                { 
                    NativeMethods.EmptyClipboard(); 
                    NativeMethods.CloseClipboard(); 
                } 
            } 
            catch { }
        }

        private static async void RestoreClipboardTextAsync(string? savedText)
        {
            await Task.Delay(400);
            
            Thread staThread = new Thread(() => {
                try { 
                    if (!string.IsNullOrEmpty(savedText)) Clipboard.SetText(savedText); 
                    else Clipboard.Clear(); 
                } catch { }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
        }
    }
    #endregion

    #region [ 6. 메인 트레이 앱 폼 (TrayMainForm) ]
    /// <summary>
    /// 애플리케이션의 라이프사이클과 시스템 트레이, UI 업데이트를 담당하는 메인 백그라운드 폼입니다.
    /// </summary>
    internal class TrayMainForm : Form
    {
        public static TrayMainForm? Instance { get; private set; }
        
        private NotifyIcon _trayIcon = null!;
        private ContextMenuStrip _trayMenu = null!;
        
        private TextOverlayForm? _overlayForm;
        private KeyboardLayoutForm? _keyboardForm;
        
        private System.Windows.Forms.Timer _imeStatePollingTimer = null!;
        private bool _isHangulModeActive = false;

        public TrayMainForm()
        {
            Instance = this;
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.Hide();
            _ = this.Handle;

            InitializeTrayUI();
            KeyboardHookManager.InitializeHook();

            _imeStatePollingTimer = new System.Windows.Forms.Timer { Interval = AppConfig.TrayUpdateIntervalMs };
            _imeStatePollingTimer.Tick += OnImeStateTimerTick;
            _imeStatePollingTimer.Start();

            UpdateKeyboardLayoutVisibility();
        }

        private void OnImeStateTimerTick(object? sender, EventArgs e)
        {
            bool isCurrentHangul = DetermineHangulMode();
            if (isCurrentHangul != _isHangulModeActive)
            {
                _isHangulModeActive = isCurrentHangul;
                RefreshTrayIconGraphics(_isHangulModeActive);
            }
        }

        private bool DetermineHangulMode()
        {
            IntPtr fgWindow = NativeMethods.GetForegroundWindow();
            if (fgWindow == IntPtr.Zero) return false;
            
            uint threadId = NativeMethods.GetWindowThreadProcessId(fgWindow, out _);
            NativeMethods.GUITHREADINFO threadInfo = new() { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            IntPtr focusWindow = fgWindow;
            
            if (NativeMethods.GetGUIThreadInfo(threadId, ref threadInfo))
            {
                if (threadInfo.hwndFocus != IntPtr.Zero) focusWindow = threadInfo.hwndFocus;
                else if (threadInfo.hwndActive != IntPtr.Zero) focusWindow = threadInfo.hwndActive;
            }

            IntPtr imeWindow = NativeMethods.ImmGetDefaultIMEWnd(focusWindow);
            if (imeWindow == IntPtr.Zero) imeWindow = NativeMethods.ImmGetDefaultIMEWnd(fgWindow);

            if (imeWindow != IntPtr.Zero)
            {
                NativeMethods.SendMessageTimeout(
                    imeWindow, 
                    NativeMethods.WM_IME_CONTROL, 
                    (IntPtr)NativeMethods.IMC_GETCONVERSIONMODE, 
                    IntPtr.Zero, 
                    NativeMethods.SMTO_ABORTIFHUNG, 
                    20, 
                    out IntPtr result);
                
                return ((uint)result.ToInt64() & NativeMethods.IME_CMODE_NATIVE) != 0;
            }
            return false;
        }

        private void InitializeTrayUI()
        {
            _trayMenu = new ContextMenuStrip();
            
            var titleMenuItem = new ToolStripMenuItem("IMEPali (Pali/Sanskrit)", null, (s, e) => 
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = AppConfig.GithubRepositoryUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"웹페이지를 열 수 없습니다.\n{ex.Message}", "IMEPali 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }) { Font = new Font(this.Font, FontStyle.Bold) };
            
            _trayMenu.Items.Add(titleMenuItem);
            _trayMenu.Items.Add(new ToolStripMenuItem("한자키로 Pali어 입력/전환") { Enabled = false });
            _trayMenu.Items.Add(new ToolStripSeparator());

            var keyboardLayoutMenu = new ToolStripMenuItem("Pali어 키보드 배열창", null, (s, e) => {
                var menuItem = (ToolStripMenuItem)s!;
                AppConfig.ShowKeyboardLayout = !menuItem.Checked;
                menuItem.Checked = AppConfig.ShowKeyboardLayout;
                
                if (!AppConfig.ShowKeyboardLayout) _keyboardForm?.Hide();
                else UpdateKeyboardLayoutVisibility();
            }) { Checked = AppConfig.ShowKeyboardLayout };
            _trayMenu.Items.Add(keyboardLayoutMenu);

            var textOverlayMenu = new ToolStripMenuItem("Pali어 입력문자 표시창", null, (s, e) => {
                var menuItem = (ToolStripMenuItem)s!;
                AppConfig.ShowTextOverlay = !menuItem.Checked;
                menuItem.Checked = AppConfig.ShowTextOverlay;
                
                if (!AppConfig.ShowTextOverlay) _overlayForm?.ClearOverlay();
            }) { Checked = AppConfig.ShowTextOverlay };
            _trayMenu.Items.Add(textOverlayMenu);

            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(new ToolStripMenuItem("종료 (Exit)", null, (s, e) => Application.Exit()));

            _trayIcon = new NotifyIcon
            {
                ContextMenuStrip = _trayMenu,
                Visible = true,
                Text = "IMEPali - Pali Input System"
            };

            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                {
                    NativeMethods.SetForegroundWindow(this.Handle);
                    _trayMenu.Show(Cursor.Position);
                }
            };
            
            _isHangulModeActive = DetermineHangulMode();
            RefreshTrayIconGraphics(_isHangulModeActive);
        }

        private void RefreshTrayIconGraphics(bool isHangul)
        {
            int iconSize = 16;
            using Bitmap bmp = new Bitmap(iconSize, iconSize);
            using Graphics graphics = Graphics.FromImage(bmp);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            
            graphics.Clear(Color.Black);
            Color fontColor = isHangul ? Color.White : Color.Orange;
            
            using Brush textBrush = new SolidBrush(fontColor);
            StringFormat stringFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            
            graphics.DrawString("P", new Font("Segoe UI Black", 10, FontStyle.Bold), textBrush, new RectangleF(0, 1.5f, iconSize, iconSize), stringFormat);

            IntPtr hIcon = bmp.GetHicon();
            Icon? previousIcon = _trayIcon.Icon;
            _trayIcon.Icon = Icon.FromHandle(hIcon);
            if (previousIcon != null) NativeMethods.DestroyIcon(previousIcon.Handle);
        }

        public void ShowOverlay(string text)
        {
            if (!AppConfig.ShowTextOverlay) return;
            
            this.Invoke((MethodInvoker)delegate
            {
                if (_overlayForm == null || _overlayForm.IsDisposed) _overlayForm = new TextOverlayForm();
                
                Point caretLocation = GetCaretPositionOnScreen();
                int dynamicWidth = Math.Max(40, text.Length * 15 + 24);
                
                _overlayForm.Display(text, true, 22f, dynamicWidth, 52, caretLocation.X, caretLocation.Y + 40);
            });
        }

        private Point GetCaretPositionOnScreen()
        {
            IntPtr fgWindow = NativeMethods.GetForegroundWindow();
            uint threadId = NativeMethods.GetWindowThreadProcessId(fgWindow, out _);
            NativeMethods.GUITHREADINFO threadInfo = new() { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            
            if (NativeMethods.GetGUIThreadInfo(threadId, ref threadInfo) && threadInfo.hwndCaret != IntPtr.Zero)
            {
                NativeMethods.POINT point = new() { X = threadInfo.rectLeft, Y = threadInfo.rectBottom };
                NativeMethods.ClientToScreen(threadInfo.hwndCaret, ref point);
                return new Point(point.X, point.Y);
            }
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT mousePoint)) return new Point(mousePoint.X, mousePoint.Y);
            
            return Point.Empty;
        }

        public void UpdateKeyboardLayoutVisibility()
        {
            this.Invoke((MethodInvoker)delegate
            {
                if (!AppConfig.ShowKeyboardLayout)
                {
                    _keyboardForm?.Hide();
                    return;
                }
                
                bool isCapsOn = (NativeMethods.GetKeyState(0x14) & 1) != 0;
                string targetImage = isCapsOn ? "IMEPali.images.PaliKey2.png" : "IMEPali.images.PaliKey1.png";

                if (_keyboardForm == null || _keyboardForm.IsDisposed)
                {
                    _keyboardForm = new KeyboardLayoutForm();
                    _keyboardForm.OnLayoutDoubleClicked += (s, e) => ToggleSystemCapsLock();
                    _keyboardForm.OnClosedByUser += (s, e) =>
                    {
                        AppConfig.ShowKeyboardLayout = false;
                        foreach (ToolStripItem item in _trayMenu.Items)
                        {
                            if (item.Text == "Pali어 키보드 배열창") ((ToolStripMenuItem)item).Checked = false;
                        }
                    };
                }
                
                _keyboardForm.RenderImage(targetImage);
                
                if (!_keyboardForm.Visible)
                {
                    _keyboardForm.Show();
                    if (_keyboardForm.WindowState == FormWindowState.Minimized) _keyboardForm.WindowState = FormWindowState.Normal;
                }
            });
        }
        
        private void ToggleSystemCapsLock()
        {
            KeyboardHookManager.IsSendingInput = true;
            var inputs = new NativeMethods.INPUT[]
            {
                KeyboardHookManager.CreateKeyInput(0x14, false),
                KeyboardHookManager.CreateKeyInput(0x14, true)
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            KeyboardHookManager.IsSendingInput = false;

            Task.Delay(50).ContinueWith(_ => UpdateKeyboardLayoutVisibility());
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _imeStatePollingTimer?.Stop();
            _imeStatePollingTimer?.Dispose();
            
            KeyboardHookManager.ReleaseHook();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            base.OnFormClosing(e);
        }
    }
    #endregion

    #region [ 7. UI 구성 요소: 배열창 및 오버레이 폼 ]
    public class KeyboardLayoutForm : Form
    {
        private readonly PictureBox _pictureBox;
        public event EventHandler? OnLayoutDoubleClicked;
        public event EventHandler? OnClosedByUser;
        private string _activeResourceName = "";

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
                using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream("IMEPali.images.IMEPali.ico");
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

        public void RenderImage(string resourceName)
        {
            if (_activeResourceName == resourceName) return;
            _activeResourceName = resourceName;

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
        private readonly System.Windows.Forms.Timer _visibilityTimer;
        private string _displayText = "";
        private float _fontSizePt = 22f;

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

            _visibilityTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _visibilityTimer.Tick += (s, e) => this.Hide();
            
            this.Paint += OnFormPaint;
        }

        public void Display(string text, bool autoHide, float fontSize, int width, int height, int locX, int locY)
        {
            _displayText = text;
            _fontSizePt = fontSize;
            this.Size = new Size(width, height);
            this.Location = new Point(locX, locY);
            
            if (autoHide) 
            { 
                _visibilityTimer.Stop(); 
                _visibilityTimer.Start(); 
            }
            else _visibilityTimer.Stop();
            
            if (!this.Visible) this.Show(); 
            this.Invalidate();
        }

        private void OnFormPaint(object? sender, PaintEventArgs e)
        {
            using Font f = new Font("Malgun Gothic", _fontSizePt, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(e.Graphics, _displayText, f, this.ClientRectangle, Color.Orange, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        
        public void ClearOverlay()
        {
            _visibilityTimer.Stop();
            this.Hide();
        }
    }
    #endregion

    #region [ 8. NativeMethods (Win32 API P/Invoke) ]
    internal static class NativeMethods
    {
        public const int WH_KEYBOARD_LL = 13;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

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
            bool isShiftHeld = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            
            if (isShiftHeld) inputs.Add(KeyboardHookManager.CreateKeyInput(0x10, true));
            
            foreach (char c in text)
            {
                inputs.Add(new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 0x0004 } } });
                inputs.Add(new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 0x0004 | 0x0002 } } });
            }
            
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); 
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        
        [StructLayout(LayoutKind.Sequential)] 
        public struct GUITHREADINFO { public int cbSize; public int flags; public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture; public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret; public int rectLeft; public int rectTop; public int rectRight; public int rectBottom; }

        [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] public static extern bool CloseClipboard();
        [DllImport("user32.dll")] public static extern bool EmptyClipboard();
        [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        public const int WM_IME_CONTROL = 0x0283;
        public const int IMC_GETCONVERSIONMODE = 0x0001;
        public const uint IME_CMODE_NATIVE = 0x0001;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
    }
    #endregion
}