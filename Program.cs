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
using Microsoft.Win32;
using System.Security.Principal;

namespace IMEPali
{
    #region [ 1. 앱 환경 설정 (AppConfig) ]
    /// <summary>
    /// 애플리케이션 전반에서 사용되는 사용자 및 환경 설정 클래스입니다.
    /// </summary>
    internal static class AppConfig
    {
        // UX 1: 설정 영속성 — 레지스트리 저장/로드 경로 (HKCU)
        private const string RegPath = @"Software\IMEPali";

        // UX 1: 재시작 시 레지스트리에서 로드 (기본값: true)
        public static bool ShowKeyboardLayout = LoadBool("ShowKeyboardLayout", true);
        public static bool ShowTextOverlay    = LoadBool("ShowTextOverlay",    true);

        // UX 3: 오버레이 표시 시간(ms) 설정화 (기본값: 1500ms)
        public static int OverlayDisplayMs = LoadInt("OverlayDisplayMs", 1500);
        
        // Pali어 전환용 트리거 키 (0x19: 한자키, 0xA3: 우측 Ctrl키)
        public static readonly int[] ToggleKeyCodes = { 0x19, 0xA3 };
        
        // 트레이 아이콘 한/영 상태 갱신 폴링 주기 (ms)
        // 성능 1 개선: 100ms → 500ms (5× CPU 절약, IME 상태 변경 감지는 500ms 응답으로도 충분)
        public static readonly int TrayUpdateIntervalMs = 500;

        // 트레이 메뉴의 GitHub 링크
        public static readonly string GithubRepositoryUrl = "https://github.com/stonkim93/IMEPali";

        // UX 1: 현재 설정값을 레지스트리에 저장 (트레이 메뉴 토글 시 호출)
        public static void Save()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                if (key == null) return;
                key.SetValue("ShowKeyboardLayout", ShowKeyboardLayout ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("ShowTextOverlay",    ShowTextOverlay    ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("OverlayDisplayMs",   OverlayDisplayMs,           RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IMEPali] AppConfig.Save 오류: {ex.Message}");
            }
        }

        private static bool LoadBool(string name, bool defaultValue)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
                if (key?.GetValue(name) is int v) return v != 0;
            }
            catch { }
            return defaultValue;
        }

        private static int LoadInt(string name, int defaultValue)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
                if (key?.GetValue(name) is int v) return v;
            }
            catch { }
            return defaultValue;
        }
    }
    #endregion

    #region [ 2. 진입점 (Main) ]
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // IMEPointer, IMCPointer, IMEJapanese 및 IMEPali 중복 실행 방지
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
            {"s", new string?[]{"s", "ṣ", null, null, null, "ś", null}},
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
        // 버그 3 수정: volatile bool 대신 Interlocked로 원자적 플래그 관리
        // SendString(비동기)과 SendReplacementText(동기)가 동일 플래그를 공유하므로 원자적 조작 필수
        private static int _isSendingInput = 0;
        public static bool IsSendingInput
        {
            get => _isSendingInput == 1;
            set => Interlocked.Exchange(ref _isSendingInput, value ? 1 : 0);
        }
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
                if (nCode >= 0)
                {
                    int virtualKeyCode = Marshal.ReadInt32(lParam);
                    bool isKeyUp = (wParam == (IntPtr)0x0101 || wParam == (IntPtr)0x0105);

                    // 앱이 스스로 입력을 발생시키는 중이라도, 토글키를 뗐다면 상태를 반드시 초기화하여 갇힘(Stuck) 방지
                    if (IsSendingInput)
                    {
                        if (isKeyUp && AppConfig.ToggleKeyCodes.Contains(virtualKeyCode))
                        {
                            _isToggleKeyDown = false;
                        }
                        return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
                    }

                    // 이후 기존 로직 유지
                    bool isKeyDown = (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104);

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
                            // [수정] Copilot 매크로 (Win+Shift+F23) 방어 로직
                            bool isLWinDown = (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0;
                            bool isShiftDown = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
                            if (isLWinDown) isShiftDown = false; // Copilot 억지 Shift 끄기                            
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
                                // [수정] 동일한 방어 로직 적용
                                bool isLWinDown = (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0;
                                bool isShiftDown = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
                                if (isLWinDown) isShiftDown = false;

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

            // [수정] Copilot 매크로의 찌꺼기 수식키(Win, Shift)를 강제로 해제
            bool isLWinHeld = (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0;
            bool isShiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            
            if (isLWinHeld) inputList.Add(CreateKeyInput(0x5B, true));            
            if (isShiftHeld) inputList.Add(CreateKeyInput(0x10, true));
            
            for (int i = 0; i < backspaces; i++)
            {
                inputList.Add(CreateKeyInput(0x08, false)); 
                inputList.Add(CreateKeyInput(0x08, true));  
            }
            
            if (inputList.Count > 0)
            {
                NativeMethods.SendInput((uint)inputList.Count, inputList.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
            }

            if (text.Length > 0) NativeMethods.SendUnicodeString(text);
            
            IsSendingInput = false;
        }

        private static void SendString(string text)
        {
            // 백그라운드 태스크에서 전송하여 글로벌 키보드 후킹 스레드를 블로킹하지 않음
            Task.Run(async () =>
            {
                IsSendingInput = true;
                
                // Copilot 키(Win, Shift 등 조합)의 논리적 잔여 신호가 해제될 시간을 줌 (약 30ms)
                await Task.Delay(30); 
                
                NativeMethods.SendUnicodeString(text);
                
                // 전송 완료 후 약간의 딜레이를 주어 입력 꼬임 방지
                await Task.Delay(10);
                IsSendingInput = false;
            });
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
        // volatile bool 대신 Interlocked로 원자적 Check-Then-Act 보장 (버그 2 수정)
        private static int _isProcessing = 0;
        public static bool IsProcessing => _isProcessing == 1;

        // 성능 2 개선: WM_CLIPBOARDUPDATE 이벤트 기반 대기용 (Busy-Wait 폴링 제거)
        // TrayMainForm.WndProc → WM_CLIPBOARDUPDATE 수신 시 Set() 호출
        internal static readonly ManualResetEventSlim ClipboardUpdatedEvent = new ManualResetEventSlim(false);

        public static void TransformAndReplaceSelectedText(string lastOutputChar, Func<string, string> transformationFunc, Action<string> updateLastCharAction)
        {
            // Interlocked.CompareExchange: 0 → 1 로 바꾸는 데 성공한 스레드만 진입 (원자적)
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;
            
            // UI Automation 및 Clipboard API는 STA 스레드에서 실행되어야 함
            Thread workerThread = new Thread(() =>
            {
                try
                {
                    // [수정] Copilot 매크로(Win+Shift+F23) 물리적/논리적 해제 지연을 기다림
                    Thread.Sleep(50); 

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
                catch (Exception ex)
                {
                    // 품질 1 개선: 예외 삼킴 제거 → Debug 로깅
                    System.Diagnostics.Debug.WriteLine($"[IMEPali] TransformAndReplace 오류: {ex.Message}");
                }
                finally { Interlocked.Exchange(ref _isProcessing, 0); }
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IMEPali] UIA 선택 텍스트 읽기 실패: {ex.Message}");
            }

            // bool isShiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            string? backupClipboardText = GetClipboardText();
            try
            {
                // 성능 2 개선: 이벤트 기반 클립보드 대기 (Busy-Wait 20ms × 20회 → 이벤트 Wait)
                // TrayMainForm이 WM_CLIPBOARDUPDATE를 받으면 ClipboardUpdatedEvent.Set() 신호
                ClipboardUpdatedEvent.Reset();
                ClearClipboard();
                SendCtrlC();
                
                // 최대 400ms 대기. 신호 도착 즉시 빠져나옴 (평균 수십ms)
                ClipboardUpdatedEvent.Wait(400);
                string? copiedText = GetClipboardText();
                
                _ = RestoreClipboardTextAsync(backupClipboardText); // async Task 호출 (fire-and-forget)
                return string.IsNullOrEmpty(copiedText) ? null : copiedText.Trim('\r', '\n', '\t', ' ', '\0');
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IMEPali] 클립보드 선택 텍스트 읽기 실패: {ex.Message}");
                return null;
            }
        }

        private static void SendCtrlC()
        {
            KeyboardHookManager.IsSendingInput = true;
            var inputs = new List<NativeMethods.INPUT>();
            
            // [수정] 수식키 강제 해제
            bool isLWinHeld = (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0;
            bool isShiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            if (isLWinHeld) inputs.Add(KeyboardHookManager.CreateKeyInput(0x5B, true));
            if (isShiftHeld) inputs.Add(KeyboardHookManager.CreateKeyInput(0x10, true));
            
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x11, false));
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x43, false));
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x43, true));
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x11, true));
            
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
            KeyboardHookManager.IsSendingInput = false;
        }
        
        private static void CancelTextSelection()
        {
            KeyboardHookManager.IsSendingInput = true;
            var inputs = new List<NativeMethods.INPUT>();

            // [수정] 수식키 강제 해제
            bool isLWinHeld = (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0;
            bool isShiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            if (isLWinHeld) inputs.Add(KeyboardHookManager.CreateKeyInput(0x5B, true));
            if (isShiftHeld) inputs.Add(KeyboardHookManager.CreateKeyInput(0x10, true));

            inputs.Add(KeyboardHookManager.CreateKeyInput(0x27, false));
            inputs.Add(KeyboardHookManager.CreateKeyInput(0x27, true));
            
            NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
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

        // 버그 4 수정: async void → async Task (예외 캐치 불가 문제 해결)
        // 호출부에서 _ = RestoreClipboardTextAsync(...) 형태로 호출하여 경고 억제
        private static async Task RestoreClipboardTextAsync(string? savedText)
        {
            await Task.Delay(400);
            
            Thread staThread = new Thread(() => {
                try { 
                    if (!string.IsNullOrEmpty(savedText)) Clipboard.SetText(savedText); 
                    else Clipboard.Clear(); 
                } 
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[IMEPali] RestoreClipboardTextAsync 오류: {ex.Message}");
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
        }
    }
    #endregion

    #region [ 6. 레지스트리 키맵핑 도구 (RegistryHelper) ]
    internal static class RegistryHelper
    {
        private const string RegPath = @"SYSTEM\CurrentControlSet\Control\Keyboard Layout";
        private const string RegValue = "Scancode Map";
        private static readonly byte[] MappingBytes = { 0x71, 0xE0, 0x6E, 0x00 };

        public static bool IsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static bool IsMappingApplied()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegPath, false);
                if (key?.GetValue(RegValue) is byte[] data && data.Length >= 20)
                {
                    int count = BitConverter.ToInt32(data, 8);
                    for (int i = 0; i < count - 1; i++)
                    {
                        int offset = 12 + (i * 4);
                        if (offset + 4 <= data.Length)
                        {
                            if (data[offset] == MappingBytes[0] && data[offset + 1] == MappingBytes[1] &&
                                data[offset + 2] == MappingBytes[2] && data[offset + 3] == MappingBytes[3])
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
            catch { return false; }
        }

        public static bool ToggleMapping(bool apply)
        {
            if (!IsAdmin())
            {
                MessageBox.Show("레지스트리 수정을 위해 앱을 '관리자 권한'으로 실행해주세요.", "권한 필요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegPath, true);
                if (key == null) return false;

                byte[]? currentData = key.GetValue(RegValue) as byte[];

                if (apply)
                {
                    if (IsMappingApplied()) return true;

                    byte[] newData;
                    if (currentData == null || currentData.Length < 20)
                    {
                        newData = new byte[20];
                        Array.Clear(newData, 0, 8);
                        BitConverter.GetBytes(2).CopyTo(newData, 8);
                        MappingBytes.CopyTo(newData, 12);
                        Array.Clear(newData, 16, 4);
                    }
                    else
                    {
                        int oldCount = BitConverter.ToInt32(currentData, 8);
                        newData = new byte[currentData.Length + 4];
                        Array.Copy(currentData, 0, newData, 0, 8);
                        BitConverter.GetBytes(oldCount + 1).CopyTo(newData, 8);
                        Array.Copy(currentData, 12, newData, 12, currentData.Length - 16);
                        MappingBytes.CopyTo(newData, currentData.Length - 4);
                        Array.Clear(newData, newData.Length - 4, 4);
                    }
                    key.SetValue(RegValue, newData, RegistryValueKind.Binary);
                }
                else
                {
                    if (!IsMappingApplied() || currentData == null) return true;

                    int oldCount = BitConverter.ToInt32(currentData, 8);
                    if (oldCount <= 2)
                    {
                        key.DeleteValue(RegValue, false);
                    }
                    else
                    {
                        byte[] newData = new byte[currentData.Length - 4];
                        Array.Copy(currentData, 0, newData, 0, 8);
                        BitConverter.GetBytes(oldCount - 1).CopyTo(newData, 8);

                        int destOffset = 12;
                        for (int i = 0; i < oldCount - 1; i++)
                        {
                            int srcOffset = 12 + (i * 4);
                            bool isTarget = currentData[srcOffset] == MappingBytes[0] &&
                                            currentData[srcOffset + 1] == MappingBytes[1] &&
                                            currentData[srcOffset + 2] == MappingBytes[2] &&
                                            currentData[srcOffset + 3] == MappingBytes[3];

                            if (!isTarget)
                            {
                                Array.Copy(currentData, srcOffset, newData, destOffset, 4);
                                destOffset += 4;
                            }
                        }
                        Array.Clear(newData, newData.Length - 4, 4);
                        key.SetValue(RegValue, newData, RegistryValueKind.Binary);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"레지스트리 수정 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
    }
    #endregion

    #region [ 7. 메인 트레이 앱 폼 (TrayMainForm) ]
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
        private ToolStripMenuItem _remapMenuItem = null!;
        // 품질 3 개선: keyboardLayoutMenu를 필드로 승격 → 문자열 검색 없이 직접 참조
        private ToolStripMenuItem _keyboardLayoutMenuItem = null!;
        
        private System.Windows.Forms.Timer _imeStatePollingTimer = null!;
        private bool _isHangulModeActive = false;

        public TrayMainForm()
        {
            Instance = this;
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.Hide();
            _ = this.Handle;
            // 성능 2 개선: 클립보드 변경 알림 등록 (WM_CLIPBOARDUPDATE 수신)
            NativeMethods.AddClipboardFormatListener(this.Handle);

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

            // 품질 3 개선: 지역 변수 → 필드 참조 (OnClosedByUser에서 문자열 검색 불필요)
            _keyboardLayoutMenuItem = new ToolStripMenuItem("Pali어 키보드 배열창", null, (s, e) => {
                var menuItem = (ToolStripMenuItem)s!;
                AppConfig.ShowKeyboardLayout = !menuItem.Checked;
                menuItem.Checked = AppConfig.ShowKeyboardLayout;
                AppConfig.Save(); // UX 1: 설정 즐시 저장
                if (!AppConfig.ShowKeyboardLayout) _keyboardForm?.Hide();
                else UpdateKeyboardLayoutVisibility();
            }) { Checked = AppConfig.ShowKeyboardLayout };
            _trayMenu.Items.Add(_keyboardLayoutMenuItem);

            var textOverlayMenu = new ToolStripMenuItem("Pali어 입력문자 표시창", null, (s, e) => {
                var menuItem = (ToolStripMenuItem)s!;
                AppConfig.ShowTextOverlay = !menuItem.Checked;
                menuItem.Checked = AppConfig.ShowTextOverlay;
                AppConfig.Save(); // UX 1: 설정 즐시 저장
                if (!AppConfig.ShowTextOverlay) _overlayForm?.ClearOverlay();
            }) { Checked = AppConfig.ShowTextOverlay };
            _trayMenu.Items.Add(textOverlayMenu);

            // 한자키 복원 관련 시작
            _remapMenuItem = new ToolStripMenuItem("한자키 적용/복원 키맵핑", null, (s, e) => {
                bool isCurrentlyApplied = RegistryHelper.IsMappingApplied();
                bool targetApply = !isCurrentlyApplied;
                string actionName = targetApply ? "적용" : "복원";

                var confirmResult = MessageBox.Show(
                    $"갤럭시북5 등의 Copilot 키를 한자키로 {actionName}하시겠습니까?\n(레지스트리 키맵핑이 수정되며, 원활한 진행을 위해 관리자 권한과 재부팅이 필요합니다.)",
                    "키맵핑 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (confirmResult == DialogResult.Yes)
                {
                    if (RegistryHelper.ToggleMapping(targetApply))
                    {
                        _remapMenuItem.Checked = targetApply;
                        MessageBox.Show($"키맵핑 {actionName} 작업이 완료되었습니다.\n정상적인 반영을 위해 시스템을 재부팅(Reboot)해 주시기 바랍니다.", 
                                        "재부팅 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }) { Checked = RegistryHelper.IsMappingApplied() };
            _trayMenu.Items.Add(_remapMenuItem);
            // 한자키 복원 관련 끝

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

            // 버그 5 수정: GDI hIcon 핸들 누수 해결
            // Icon.FromHandle(hIcon)은 GDI 핸들을 복사하지 않으므로,
            // 새 아이콘으로 교체 후 원본 hIcon과 이전 아이콘 핸들 모두 해제해야 함
            IntPtr hIcon = bmp.GetHicon();
            Icon newIcon = Icon.FromHandle(hIcon);
            Icon? previousIcon = _trayIcon.Icon;
            _trayIcon.Icon = newIcon;
            previousIcon?.Dispose();
            NativeMethods.DestroyIcon(hIcon); // GetHicon()으로 생성된 원본 GDI 핸들 해제
        }

        public void ShowOverlay(string text)
        {
            if (!AppConfig.ShowTextOverlay) return;
            
            this.Invoke((MethodInvoker)delegate
            {
                if (_overlayForm == null || _overlayForm.IsDisposed) _overlayForm = new TextOverlayForm();
                
                Point caretLocation = GetCaretPositionOnScreen();
                int dynamicWidth = Math.Max(40, text.Length * 15 + 24);

                // UX 2: 캐랿 위치를 못 찾은 경우(Point.Empty) 폴백 처리
                // 화면 중앙 하단 근처로 표시하여 (0,0) 충돌 방지
                if (caretLocation == Point.Empty)
                {
                    var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
                    caretLocation = new Point(
                        workArea.Left + (workArea.Width - dynamicWidth) / 2,
                        workArea.Bottom - 120);
                }
                
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
                        // 품질 3 개선: 문자열 검색 제거 → 필드 직접 참조
                        _keyboardLayoutMenuItem.Checked = false;
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

        /// <summary>
        /// 성능 2 개선: WM_CLIPBOARDUPDATE 수신 시 ClipboardUtility의 이벤트를 신호 처리
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                ClipboardUtility.ClipboardUpdatedEvent.Set();
            }
            base.WndProc(ref m);
        }
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _imeStatePollingTimer?.Stop();
            _imeStatePollingTimer?.Dispose();
            
            // 성능 2 개선: 앱 종료 시 클립보드 알림 해제
            NativeMethods.RemoveClipboardFormatListener(this.Handle);
            KeyboardHookManager.ReleaseHook();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            base.OnFormClosing(e);
        }
    }
    #endregion

    #region [ 8. UI 구성 요소: 배열창 및 오버레이 폼 ]
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

            // UX 4: 저장된 위치 복원. 저장값이 없으면 화면 중앙 상단 기본 위치
            this.StartPosition = FormStartPosition.Manual;
            if (!RestoreLocationFromRegistry())
            {
                int screenWidth = Screen.PrimaryScreen?.WorkingArea.Width ?? 800;
                this.Location = new Point(Math.Max(0, (screenWidth - this.Width) / 2), 50);
            }

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

            // UX 4: 이동/크기 변경 시 위치 저장
            this.LocationChanged += (s, e) => SaveLocationToRegistry();
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

        // UX 4: 현재 위치를 HKCU\Software\IMEPali에 저장
        private void SaveLocationToRegistry()
        {
            if (this.WindowState != FormWindowState.Normal) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\IMEPali");
                key?.SetValue("KeyboardFormX", this.Left, RegistryValueKind.DWord);
                key?.SetValue("KeyboardFormY", this.Top,  RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IMEPali] KeyboardForm 위치 저장 오류: {ex.Message}");
            }
        }

        // UX 4: 저장된 위치 복원. 화면 밀밖이면 false 반환
        private bool RestoreLocationFromRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\IMEPali");
                if (key?.GetValue("KeyboardFormX") is int x &&
                    key?.GetValue("KeyboardFormY") is int y)
                {
                    var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
                    // 화면 바깥의 위치라면 복원 거부
                    if (x >= workArea.Left && x < workArea.Right - 50 &&
                        y >= workArea.Top  && y < workArea.Bottom - 30)
                    {
                        this.Location = new Point(x, y);
                        return true;
                    }
                }
            }
            catch { }
            return false;
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

            // UX 3: 하드코딩 1500ms → AppConfig.OverlayDisplayMs 설정값 사용
            _visibilityTimer = new System.Windows.Forms.Timer { Interval = AppConfig.OverlayDisplayMs };
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

    #region [ 9. NativeMethods (Win32 API P/Invoke) ]
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

        // 성능 2 개선: 클립보드 변경 알림 API
        public const uint WM_CLIPBOARDUPDATE = 0x031D;
        [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    }
    #endregion

    #region [ 10. 가상 키 코드 상수 (VK) ]
    /// <summary>
    /// 품질 2 개선: 코드 전반의 매직 넘버를 제거하기 위한 가상 키 코드 상수 모음.
    /// 신규 코드 작성 시 하드코딩 대신 이 클래스를 참조할 것.
    /// </summary>
    internal static class VK
    {
        public const int SHIFT    = 0x10; // Shift (공통)
        public const int LSHIFT   = 0xA0; // 왼쪽 Shift
        public const int RSHIFT   = 0xA1; // 오른쪽 Shift
        public const int CAPITAL  = 0x14; // Caps Lock
        public const int HANJA    = 0x19; // 한자키
        public const int LWIN     = 0x5B; // 왼쪽 Windows 키 (Copilot 방어에 사용)
        public const int RCONTROL = 0xA3; // 오른쪽 Ctrl (토글키 겸용)
        public const int BACK     = 0x08; // Backspace
        public const int CTRL     = 0x11; // Ctrl (공통)
        public const int C_KEY    = 0x43; // C (Ctrl+C 복사에 사용)
        public const int RIGHT    = 0x27; // 오른쪽 화살표 (선택 해제에 사용)
        // A~Z: 0x41~0x5A (범위로 사용)
    }
    #endregion
}