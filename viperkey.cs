using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;

static class ViperKey
{
    const string REV = "rev-20260909-16";   // printed at startup; bump on every code change

    // Where viperkey.json, viperkey.log and the .ico files are looked up.
    // Defaults to the exe folder; VIPERKEY_DIR overrides it (handy when the
    // app is loaded straight from PowerShell, whose AppDomain base is not the
    // working directory).
    static string RuntimeDir()
    {
        string env = Environment.GetEnvironmentVariable("VIPERKEY_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    // ---------- user32 ----------
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    const int VK_CONTROL = 0x11;
    const int VK_SHIFT = 0x10;
    const int VK_MENU = 0x12;
    const int VK_LWIN = 0x5B;
    const int VK_F12 = 0x7B;

    // ---------- low-level keyboard hook (reliable abort detection) ----------
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public int message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public int ptX;
        public int ptY;
    }

    delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    static LowLevelHookProc hookProcDelegate;   // keep the delegate alive
    static IntPtr hookHandle;
    static volatile bool abortRequested;
    static int abortSettledTicks;
    static volatile bool macroActive;
    static Thread abortWatchdogThread;
    static Thread hookThread;

    // abort combo requirements, snapshotted from config on load/reload
    static volatile int hkNeedCtrl, hkNeedShift, hkNeedAlt, hkNeedWin;
    static volatile int hkAbortKeyVk = -1;

    // physically held state, updated ONLY on the hook thread
    static bool hCtrl, hShift, hAlt, hWin, hAbortKey;

    static bool IsCtrlVk(int vk) { return vk == 0x11 || vk == 0xA2 || vk == 0xA3; }
    static bool IsShiftVk(int vk) { return vk == 0x10 || vk == 0xA0 || vk == 0xA1; }
    static bool IsAltVk(int vk) { return vk == 0x12 || vk == 0xA4 || vk == 0xA5; }
    static bool IsWinVk(int vk) { return vk == 0x5B || vk == 0x5C; }

    static void UpdateAbortRequested()
    {
        bool ok = true;
        if (hkNeedCtrl == 1 && !hCtrl) ok = false;
        if (hkNeedShift == 1 && !hShift) ok = false;
        if (hkNeedAlt == 1 && !hAlt) ok = false;
        if (hkNeedWin == 1 && !hWin) ok = false;
        bool now = ok && (hkAbortKeyVk < 0 || hAbortKey);
        bool prev = lastAbortLogState;
        abortRequested = now;
        if (now && !prev)
            Console.WriteLine("[{0:HH:mm:ss}] [abort] hook detected combo", DateTime.Now);
        lastAbortLogState = now;
    }

    static void RefreshAbortHookState()
    {
        hkNeedCtrl = 0; hkNeedShift = 0; hkNeedAlt = 0; hkNeedWin = 0;
        foreach (byte vk in cfg.AbortModVks)
        {
            if (vk == VK_CONTROL) hkNeedCtrl = 1;
            else if (vk == VK_SHIFT) hkNeedShift = 1;
            else if (vk == VK_MENU) hkNeedAlt = 1;
            else if (vk == VK_LWIN) hkNeedWin = 1;
        }
        hkAbortKeyVk = cfg.AbortKeyVk;
    }

    static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP)
            {
                int vk = Marshal.ReadInt32(lParam);
                bool down = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                if (IsCtrlVk(vk)) hCtrl = down;
                else if (IsShiftVk(vk)) hShift = down;
                else if (IsAltVk(vk)) hAlt = down;
                else if (IsWinVk(vk)) hWin = down;
                else if (vk == hkAbortKeyVk) hAbortKey = down;
                Console.WriteLine("[{0:HH:mm:ss}] [hook] vk=0x{1:X2} {2}", DateTime.Now, vk, down ? "down" : "up");
                if (IsCtrlVk(vk) || IsShiftVk(vk) || IsAltVk(vk) || IsWinVk(vk) || vk == hkAbortKeyVk)
                    Console.WriteLine("[{0:HH:mm:ss}] [abort-debug] needC={1} needAlt={2} abortVk=0x{3:X2} hCtrl={4} hAlt={5} hAbortKey={6}", DateTime.Now, hkNeedCtrl, hkNeedAlt, hkAbortKeyVk, hCtrl ? 1 : 0, hAlt ? 1 : 0, hAbortKey ? 1 : 0);
                UpdateAbortRequested();
            }
        }
        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    static void KeyboardHookThread()
    {
        hookProcDelegate = HookProc;
        hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, hookProcDelegate, GetModuleHandle(null), 0);
        Console.WriteLine("[{0:HH:mm:ss}] [abort] low-level keyboard hook installed: {1}", DateTime.Now, hookHandle != IntPtr.Zero);
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hookHandle);
            hookHandle = IntPtr.Zero;
        }
    }

    static void StartKeyboardHook()
    {
        hookThread = new Thread(KeyboardHookThread);
        hookThread.IsBackground = true;
        hookThread.Name = "keyboard-hook";
        hookThread.Start();
    }

    // dedicated watchdog: polls the physical abort combo via GetAsyncKeyState,
    // the same primitive that (provably) sees the Ctrl+F trigger in this session
    static void AbortWatchdogThread()
    {
        while (true)
        {
            if (macroActive && !abortRequested)
            {
                bool all = true;
                for (int i = 0; i < cfg.AbortModVks.Count; i++)
                    if (!KeyDown(cfg.AbortModVks[i])) { all = false; break; }
                if (all && KeyDown(cfg.AbortKeyVk))
                {
                    Console.WriteLine("[{0:HH:mm:ss}] [abort] watchdog saw combo", DateTime.Now);
                    abortRequested = true;
                }
            }
            Thread.Sleep(5);
        }
    }

    static void StartAbortWatchdog()
    {
        abortWatchdogThread = new Thread(AbortWatchdogThread);
        abortWatchdogThread.IsBackground = true;
        abortWatchdogThread.Name = "abort-watchdog";
        abortWatchdogThread.Start();
    }

    // ---------- native global hotkey for the abort combo (belt-and-braces) ----------
    const int WM_HOTKEY = 0x0312;
    const int HOTKEY_ID = 0x4B1E;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    static bool hotkeyRegistered;
    static bool lastAbortLogState;

    sealed class AbortForm : Form
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                if (!abortRequested)
                    Console.WriteLine("[{0:HH:mm:ss}] [abort] hotkey fired", DateTime.Now);
                abortRequested = true;
            }
            base.WndProc(ref m);
        }
    }

    static AbortForm abortForm;

    static uint AbortHotkeyMods()
    {
        uint mods = 0;
        foreach (byte vk in cfg.AbortModVks)
        {
            if (vk == VK_CONTROL) mods |= 0x2;   // MOD_CONTROL
            else if (vk == VK_MENU) mods |= 0x1; // MOD_ALT
            else if (vk == VK_SHIFT) mods |= 0x4;// MOD_SHIFT
            else if (vk == VK_LWIN) mods |= 0x8; // MOD_WIN
        }
        return mods;
    }

    static void RegisterAbortHotkey()
    {
        UnregisterAbortHotkey();
        IntPtr h = abortForm != null ? abortForm.Handle : IntPtr.Zero;
        hotkeyRegistered = RegisterHotKey(h, HOTKEY_ID, AbortHotkeyMods(), (uint)cfg.AbortKeyVk);
        Console.WriteLine("[{0:HH:mm:ss}] [abort] hotkey registered: {1}", DateTime.Now, hotkeyRegistered ? "yes" : "no");
    }

    static void UnregisterAbortHotkey()
    {
        if (hotkeyRegistered)
        {
            UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
            hotkeyRegistered = false;
        }
    }

    // sleep in small slices so an abort request is noticed promptly
    static bool SleepAbortable(int ms)
    {
        for (int waited = 0; waited < ms; waited += 10)
        {
            if (abortRequested) return true;
            Thread.Sleep(Math.Min(10, ms - waited));
        }
        return false;
    }

    // ---------- HID modifier flags (same values in VIIPER's keyboard protocol) ----------
    const byte MOD_LCONTROL = 0x01;
    const byte MOD_LSHIFT = 0x02;
    const byte MOD_LALT = 0x04;
    const byte MOD_LWIN = 0x08;

    // ---------- VIIPER TCP API ----------
    sealed class ViiperEngine : IDisposable
    {
        const string Host = "127.0.0.1";
        const int ApiPort = 3242;

        Process serverProc;
        int busId = -1;
        string devId;
        TcpClient stream;
        NetworkStream streamNs;
        bool deviceAlive;
        readonly object stateLock = new object();
        long lastRebuildAttempt;

        static string Request(string path)
        {
            using (TcpClient c = new TcpClient())
            {
                c.Connect(Host, ApiPort);
                c.ReceiveTimeout = 5000;
                using (NetworkStream ns = c.GetStream())
                {
                    byte[] req = Encoding.UTF8.GetBytes(path + "\0");
                    ns.Write(req, 0, req.Length);
                    ns.Flush();
                    byte[] buf = new byte[4096];
                    using (MemoryStream ms = new MemoryStream())
                    {
                        int n;
                        while ((n = ns.Read(buf, 0, buf.Length)) > 0)
                            ms.Write(buf, 0, n);
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
        }

        static Dictionary<string, object> Deser(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        bool Ping()
        {
            try { return Request("ping").Contains("VIIPER"); }
            catch { return false; }
        }

        static string FindServerPath()
        {
            string[] cands =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "viiper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIIPER", "viiper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VIIPER", "viiper.exe"),
            };
            foreach (string p in cands)
                if (File.Exists(p)) return p;
            return null;
        }

        static string FindUsbipDir()
        {
            string[] cands =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBip"),
            };
            foreach (string p in cands)
                if (File.Exists(Path.Combine(p, "usbip.exe"))) return p;
            return null;
        }

        public bool EnsureServer()
        {
            if (Ping()) return true;
            string path = FindServerPath();
            if (path == null) return false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(path);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                string usbipDir = FindUsbipDir();
                if (usbipDir != null)
                {
                    string cur = psi.EnvironmentVariables["PATH"];
                    psi.EnvironmentVariables["PATH"] = (string.IsNullOrEmpty(cur) ? "" : cur + ";") + usbipDir;
                }
                serverProc = Process.Start(psi);
            }
            catch { return false; }
            for (int i = 0; i < 40; i++)
            {
                if (Ping()) return true;
                Thread.Sleep(250);
            }
            return false;
        }

        void CloseStreamLocked()
        {
            if (stream != null)
            {
                try { stream.Close(); } catch { }
                stream = null;
                streamNs = null;
            }
        }

        bool RebuildDeviceLocked()
        {
            try
            {
                if (busId < 0)
                {
                    string r = Request("bus/create");
                    Dictionary<string, object> d = Deser(r);
                    object o;
                    if (d == null || !d.TryGetValue("busId", out o))
                        throw new Exception("bus/create failed: " + (r ?? "no response"));
                    busId = Convert.ToInt32(o);
                }

                string dev = Request("bus/" + busId + "/add {\"type\":\"keyboard\"}");
                Dictionary<string, object> dd = Deser(dev);
                object dv;
                if (dd == null || !dd.TryGetValue("devId", out dv))
                {
                    busId = -1;
                    throw new Exception("bus/" + busId.ToString() + "/add failed: " + (dev ?? "no response"));
                }
                devId = Convert.ToString(dv);

                TcpClient s = new TcpClient();
                s.Connect(Host, ApiPort);
                NetworkStream ns = s.GetStream();
                byte[] hs = Encoding.UTF8.GetBytes("bus/" + busId + "/" + devId + "\0");
                ns.Write(hs, 0, hs.Length);
                ns.Flush();

                CloseStreamLocked();
                stream = s;
                streamNs = ns;
                deviceAlive = true;

                Thread t = new Thread(DrainLoop);
                t.IsBackground = true;
                t.Start();

                Console.WriteLine("[{0:HH:mm:ss}] Virtual keyboard up (bus {1}, device {2}).", DateTime.Now, busId, devId);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[{0:HH:mm:ss}] VIIPER device setup failed: {1}", DateTime.Now, ex.Message);
                return false;
            }
        }

        public bool EnsureDevice()
        {
            lock (stateLock)
            {
                if (deviceAlive) return true;
                if ((Environment.TickCount - lastRebuildAttempt) < 2000) return false;
                lastRebuildAttempt = Environment.TickCount;
                return RebuildDeviceLocked();
            }
        }

        void DrainLoop()
        {
            NetworkStream mine = streamNs;
            byte[] buf = new byte[256];
            try
            {
                while (true)
                {
                    int n = mine.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    if (cfg.Debug) Console.WriteLine("[viper] feedback {0} bytes", n);
                }
            }
            catch { }
            lock (stateLock)
            {
                if (ReferenceEquals(streamNs, mine))
                {
                    deviceAlive = false;
                    Console.WriteLine("[{0:HH:mm:ss}] VIIPER device stream lost - will reconnect.", DateTime.Now);
                }
            }
        }

        public void SendState(byte mod, byte[] keys)
        {
            NetworkStream ns;
            lock (stateLock)
            {
                if (!deviceAlive) return;
                ns = streamNs;
            }
            int n = keys == null ? 0 : keys.Length;
            byte[] pkt = new byte[2 + n];
            pkt[0] = mod;
            pkt[1] = (byte)n;
            for (int i = 0; i < n; i++)
                pkt[2 + i] = keys[i];
            try
            {
                ns.Write(pkt, 0, pkt.Length);
                ns.Flush();
            }
            catch { }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (busId >= 0)
                {
                    try { Request("bus/remove " + busId); } catch { }
                }
                CloseStreamLocked();
            }
        }
    }

    // ---------- config model ----------
    sealed class Config
    {
        public List<byte> TriggerModVks = new List<byte>();
        public int TriggerKeyVk;
        public string TriggerKeyName = "K";
        public List<byte> AbortModVks = new List<byte>();
        public int AbortKeyVk;
        public string AbortKeyName = "F12";
        public int DefaultDelayMs = 3000;
        public bool Debug;
        public List<KeyToken> Sequence = new List<KeyToken>();
    }

    sealed class KeyToken
    {
        public List<KeyStroke> Strokes = new List<KeyStroke>();
        public string Name;
        public int DelayMs = -1;   // -1 means use DefaultDelayMs
    }

    sealed class KeyStroke
    {
        public byte Mods;
        public byte Hid;
    }

    static Config cfg;
    static string cfgPathField;
    static string LastConfigLoadError;
    static long cfgChangeTicks;
    static long cfgReloadedTicks;
    const long ReloadDebounceTicks = 300 * TimeSpan.TicksPerMillisecond;

    static ViiperEngine viiper;
    static volatile bool reloadNow;
    static volatile bool exitRequested;
    static TextWriter logWriter;
static NotifyIcon notifyIcon;
    static Icon normalTrayIcon;
    static Icon alertTrayIcon;
    static System.Threading.Timer configFlashTimer;
    static bool flashOnAlert;
    static volatile bool configErrorActive;
    static ToolStripMenuItem miConfigError;
    static ToolStripMenuItem miTrigger;
    static ToolStripMenuItem miAbort;
    static string pendingConfigError;
    static readonly string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // ---------- key / modifier lookup ----------
    static byte ModBitsForName(string m)
    {
        switch (m.ToUpperInvariant())
        {
            case "CTRL": case "CONTROL": return MOD_LCONTROL;
            case "SHIFT": return MOD_LSHIFT;
            case "ALT": return MOD_LALT;
            case "WIN": case "GUI": return MOD_LWIN;
            default: return 0;
        }
    }

    static List<byte> TriggerVksFromSpec(string modSpec)
    {
        List<byte> vks = new List<byte>();
        if (modSpec.Equals("none", StringComparison.OrdinalIgnoreCase)) return vks;
        foreach (string pm in modSpec.Split('+'))
        {
            byte b = ModBitsForName(pm);
            if (b == 0)
            {
                Console.WriteLine("Unknown modifier: {0} (valid: Ctrl, Alt, Shift, Win)", pm);
                return null;
            }
            int vk = (b & MOD_LSHIFT) != 0 ? VK_SHIFT : (b & MOD_LCONTROL) != 0 ? VK_CONTROL : (b & MOD_LALT) != 0 ? VK_MENU : VK_LWIN;
            vks.Add((byte)vk);
        }
        return vks;
    }

    static bool LookupKey(string name, out byte hid, out byte implicitShift)
    {
        hid = 0;
        implicitShift = 0;
        if (string.IsNullOrEmpty(name)) return false;

        if (name.Length == 1)
        {
            char c = name[0];
            switch (c)
            {
                case '~': hid = 0x35; implicitShift = MOD_LSHIFT; return true;
                case '`': hid = 0x35; return true;
                case '!': hid = 0x1E; implicitShift = MOD_LSHIFT; return true;
                case '@': hid = 0x1F; implicitShift = MOD_LSHIFT; return true;
                case '#': hid = 0x20; implicitShift = MOD_LSHIFT; return true;
                case '$': hid = 0x21; implicitShift = MOD_LSHIFT; return true;
                case '%': hid = 0x22; implicitShift = MOD_LSHIFT; return true;
                case '^': hid = 0x23; implicitShift = MOD_LSHIFT; return true;
                case '&': hid = 0x24; implicitShift = MOD_LSHIFT; return true;
                case '*': hid = 0x25; implicitShift = MOD_LSHIFT; return true;
                case '(': hid = 0x26; implicitShift = MOD_LSHIFT; return true;
                case ')': hid = 0x27; implicitShift = MOD_LSHIFT; return true;
                case '_': hid = 0x2D; implicitShift = MOD_LSHIFT; return true;
                case '+': hid = 0x2E; implicitShift = MOD_LSHIFT; return true;
                case '{': hid = 0x2F; implicitShift = MOD_LSHIFT; return true;
                case '}': hid = 0x30; implicitShift = MOD_LSHIFT; return true;
                case '|': hid = 0x31; implicitShift = MOD_LSHIFT; return true;
                case ':': hid = 0x33; implicitShift = MOD_LSHIFT; return true;
                case '"': hid = 0x34; implicitShift = MOD_LSHIFT; return true;
                case '<': hid = 0x36; implicitShift = MOD_LSHIFT; return true;
                case '>': hid = 0x37; implicitShift = MOD_LSHIFT; return true;
                case '?': hid = 0x38; implicitShift = MOD_LSHIFT; return true;
            }

            if (c >= '1' && c <= '9') { hid = (byte)(0x1E + (c - '1')); return true; }
            if (c == '0') { hid = 0x27; return true; }
            if (c == '-') { hid = 0x2D; return true; }
            if (c == '=') { hid = 0x2E; return true; }
            if (c == '[') { hid = 0x2F; return true; }
            if (c == ']') { hid = 0x30; return true; }
            if (c == '\\') { hid = 0x31; return true; }
            if (c == ';') { hid = 0x33; return true; }
            if (c == '\'') { hid = 0x34; return true; }
            if (c == ',') { hid = 0x36; return true; }
            if (c == '.') { hid = 0x37; return true; }
            if (c == '/') { hid = 0x38; return true; }

            char lc = char.ToLowerInvariant(c);
            if (lc >= 'a' && lc <= 'z') { hid = (byte)(0x04 + (lc - 'a')); return true; }
        }

        switch (name.ToUpperInvariant())
        {
            case "ENTER": hid = 0x28; return true;
            case "ESC": case "ESCAPE": hid = 0x29; return true;
            case "BACKSPACE": hid = 0x2A; return true;
            case "TAB": hid = 0x2B; return true;
            case "SPACE": hid = 0x2C; return true;
            case "MINUS": hid = 0x2D; return true;
            case "EQUAL": hid = 0x2E; return true;
            case "LBRACKET": hid = 0x2F; return true;
            case "RBRACKET": hid = 0x30; return true;
            case "BACKSLASH": hid = 0x31; return true;
            case "SEMICOLON": hid = 0x33; return true;
            case "QUOTE": hid = 0x34; return true;
            case "GRAVE": hid = 0x35; return true;
            case "TILDE": hid = 0x35; implicitShift = MOD_LSHIFT; return true;
            case "COMMA": hid = 0x36; return true;
            case "PERIOD": hid = 0x37; return true;
            case "SLASH": hid = 0x38; return true;
            case "CAPSLOCK": hid = 0x39; return true;
        }

        string un = name.ToUpperInvariant();
        for (int f = 1; f <= 12; f++)
            if (un == "F" + f.ToString()) { hid = (byte)(0x3A + f - 1); return true; }
        return false;
    }

    static int HidToVk(byte hid, string name)
    {
        if (name.Length == 1)
        {
            char upper = char.ToUpperInvariant(name[0]);
            if (upper >= 'A' && upper <= 'Z') return 0x41 + (upper - 'A');
            if (upper >= '0' && upper <= '9') return 0x30 + (upper - '0');
        }
        switch (name.ToUpperInvariant())
        {
            case "~": case "`": case "GRAVE": case "TILDE": return 0xC0;
            case "ENTER": return 0x0D;
            case "TAB": return 0x09;
            case "SPACE": return 0x20;
            case "ESC": return 0x1B;
            case "BACKSPACE": return 0x08;
            case "MINUS": return 0xBD;
            case "EQUAL": return 0xBB;
            case "LBRACKET": return 0xDB;
            case "RBRACKET": return 0xDD;
            case "BACKSLASH": return 0xDC;
            case "SEMICOLON": return 0xBA;
            case "QUOTE": return 0xDE;
            case "COMMA": return 0xBC;
            case "PERIOD": return 0xBE;
            case "SLASH": return 0xBF;
            case "CAPSLOCK": return 0x14;
        }
        string un = name.ToUpperInvariant();
        for (int f = 1; f <= 12; f++)
            if (un == "F" + f.ToString())
                return 0x70 + f - 1;
        return 0;
    }

    // ---------- step parsing ----------
    static bool ParseOneStep(string tok, out KeyToken kt, out string err)
    {
        err = null;
        kt = new KeyToken();
        kt.Name = tok;
        byte pendingMods = 0;
        bool hasKey = false;
        foreach (string rawPart in tok.Split('+'))
        {
            string part = rawPart.Trim();
            if (part.Length == 0) continue;

            byte mb = ModBitsForName(part);
            if (mb != 0) { pendingMods |= mb; continue; }

            byte hid; byte imp;
            if (!LookupKey(part, out hid, out imp))
            {
                err = "Unknown key in step: " + part;
                return false;
            }
            KeyStroke st = new KeyStroke();
            st.Mods = (byte)(pendingMods | imp);
            st.Hid = hid;
            kt.Strokes.Add(st);
            pendingMods = 0;   // modifiers prefix only the key that follows: Shift+`+1 == ~+1
            hasKey = true;
        }
        if (!hasKey)
        {
            err = "Each step needs at least one actual key: " + tok;
            return false;
        }
        return true;
    }

    // ---------- config loading ----------
    static string StripJsonComments(string s)
    {
        StringBuilder sb = new StringBuilder();
        bool inBlock = false;
        foreach (string line in s.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (inBlock)
            {
                if (t.Contains("*/")) inBlock = false;
                continue;
            }
            if (t.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!t.Contains("*/")) inBlock = true;
                continue;
            }
            if (t.StartsWith("//", StringComparison.Ordinal)) continue;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    static Config FailLoad(string msg)
    {
        int line = LineForMessage(msg);
        string final = line > 0 ? "line " + line + ": " + msg : msg;
        Console.WriteLine(final);
        LastConfigLoadError = final;
        return null;
    }

    static string LastJsonText;
    static int curStepIndex;

    static int LineOfFirst(string needle)
    {
        if (LastJsonText == null) return -1;
        int line = 1;
        foreach (string raw in LastJsonText.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Contains(needle)) return line;
            line++;
        }
        return -1;
    }

    static int StepFailLine()
    {
        if (LastJsonText == null || curStepIndex < 1) return -1;
        int seen = 0;
        int line = 1;
        foreach (string raw in LastJsonText.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Contains("\"keys\""))
            {
                seen++;
                if (seen == curStepIndex) return line;
            }
            line++;
        }
        return -1;
    }

    static int LineForMessage(string msg)
    {
        if (msg.StartsWith("Invalid trigger_mods")) return LineOfFirst("\"trigger_mods\"");
        if (msg.StartsWith("Unknown trigger_key")) return LineOfFirst("\"trigger_key\"");
        if (msg.StartsWith("Invalid abort_mods")) return LineOfFirst("\"abort_mods\"");
        if (msg.StartsWith("Unknown abort_key")) return LineOfFirst("\"abort_key\"");
        if (msg.StartsWith("default_delay_ms too small")) return LineOfFirst("\"default_delay_ms\"");
        if (msg.StartsWith("'steps' must be an array") || msg.StartsWith("Config must contain a 'steps' array"))
            return LineOfFirst("\"steps\"");
        if (msg.StartsWith("Unknown key in step") || msg.StartsWith("Each step needs at least one actual key"))
            return StepFailLine();
        if (msg.StartsWith("Each step must be an object")) return StepFailLine();
        if (msg.StartsWith("A step is missing its 'keys' string")) return StepFailLine();
        if (msg.StartsWith("Invalid delay_ms in step") || msg.StartsWith("delay_ms too small")) return StepFailLine();
        if (msg.StartsWith("No steps defined")) return LineOfFirst("\"steps\"");
        return -1;
    }

    static int ExtractCharOffset(string msg)
    {
        int i = msg.LastIndexOf('(');
        if (i < 0) return -1;
        int j = msg.IndexOf(')', i);
        if (j < 0) return -1;
        int v;
        if (int.TryParse(msg.Substring(i + 1, j - i - 1), out v)) return v;
        return -1;
    }

    static int OffsetToOriginalLine(string raw, string stripped, int offset)
    {
        int sLine = 1;
        int n = Math.Min(offset, stripped == null ? 0 : stripped.Length);
        for (int i = 0; i < n; i++)
            if (stripped[i] == '\n') sLine++;

        bool inBlock = false;
        int emitted = 0;
        int line = 1;
        foreach (string rawLine in (raw ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            string t = rawLine.Trim();
            if (!inBlock && t.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!t.Contains("*/")) inBlock = true;
                line++; continue;
            }
            if (inBlock)
            {
                if (t.Contains("*/")) inBlock = false;
                line++; continue;
            }
            if (t.StartsWith("//", StringComparison.Ordinal)) { line++; continue; }
            emitted++;
            if (emitted == sLine) return line;
            line++;
        }
        return -1;
    }

    static Config LoadJsonConfig(string path)
    {
        string text = "";
        string js = "";
        try
        {
            LastConfigLoadError = null;
            text = File.ReadAllText(path);
            LastJsonText = text;
            js = StripJsonComments(text);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            Dictionary<string, object> root = (Dictionary<string, object>)ser.DeserializeObject(js);

            Config nc = new Config();

            object o;
            string mods = "Ctrl";
            if (root.TryGetValue("trigger_mods", out o))
            {
                if (o is string) mods = (string)o;
                else if (o is IList<object>)
                {
                    List<string> mm = new List<string>();
                    foreach (object x in (IList<object>)o) mm.Add((string)x);
                    mods = String.Join("+", mm.ToArray());
                }
            }
            List<byte> vks = TriggerVksFromSpec(mods);
            if (vks == null) return FailLoad("Invalid trigger_mods: " + mods);
            nc.TriggerModVks = vks;

            string tk = "K";
            if (root.TryGetValue("trigger_key", out o) && o is string) tk = (string)o;
            byte tHid; byte tImp;
            if (!LookupKey(tk, out tHid, out tImp))
            {
                return FailLoad("Unknown trigger_key: " + tk);
            }
            nc.TriggerKeyName = tk.ToUpperInvariant();
            nc.TriggerKeyVk = HidToVk(tHid, tk);

            string abortMods = "Ctrl+Alt";
            if (root.TryGetValue("abort_mods", out o))
            {
                if (o is string) abortMods = (string)o;
                else if (o is IList<object>)
                {
                    List<string> am = new List<string>();
                    foreach (object x in (IList<object>)o) am.Add((string)x);
                    abortMods = String.Join("+", am.ToArray());
                }
            }
            List<byte> aVks = TriggerVksFromSpec(abortMods);
            if (aVks == null) return FailLoad("Invalid abort_mods: " + abortMods);
            nc.AbortModVks = aVks;

            string ak = "F12";
            if (root.TryGetValue("abort_key", out o) && o is string) ak = (string)o;
            byte aHid; byte aImp;
            if (!LookupKey(ak, out aHid, out aImp))
            {
                return FailLoad("Unknown abort_key: " + ak);
            }
            nc.AbortKeyName = ak.ToUpperInvariant();
            nc.AbortKeyVk = HidToVk(aHid, ak);

            if (root.TryGetValue("default_delay_ms", out o))
            {
                nc.DefaultDelayMs = Convert.ToInt32(o);
                if (nc.DefaultDelayMs < 50)
                {
                    return FailLoad("default_delay_ms too small (min 50)");
                }
            }

            if (root.TryGetValue("debug", out o))
                nc.Debug = Convert.ToBoolean(o);

            object stepsObj;
            if (!root.TryGetValue("steps", out stepsObj))
            {
                return FailLoad("Config must contain a 'steps' array.");
            }
            IList<object> arr = stepsObj as IList<object>;
            if (arr == null) return FailLoad("'steps' must be an array.");
            curStepIndex = 0;
            foreach (object so in arr)
            {
                curStepIndex++;
                Dictionary<string, object> d = so as Dictionary<string, object>;
                if (d == null) return FailLoad("Each step must be an object.");
                object keysObj;
                if (!d.TryGetValue("keys", out keysObj) || !(keysObj is string))
                {
                    return FailLoad("A step is missing its 'keys' string.");
                }
                KeyToken kt; string err;
                if (!ParseOneStep((string)keysObj, out kt, out err))
                {
                    return FailLoad(err);
                }
                object dObj;
                if (d.TryGetValue("delay_ms", out dObj))
                {
                    try { kt.DelayMs = Convert.ToInt32(dObj); }
                    catch { return FailLoad("Invalid delay_ms in step: " + (string)keysObj); }
                    if (kt.DelayMs < 50)
                    {
                        return FailLoad("delay_ms too small (min 50): " + (string)keysObj);
                    }
                }
                nc.Sequence.Add(kt);
            }

            if (nc.Sequence.Count == 0) return FailLoad("No steps defined.");
            return nc;
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            int off = ExtractCharOffset(msg);
            int line = (off > 0) ? OffsetToOriginalLine(text, js, off) : -1;
            if (line > 0) msg = "line " + line + ": " + msg;
            return FailLoad("Config parse error: " + msg);
        }
    }

    static void WriteDefaultJson(string path)
    {
        string txt =
            "{\n" +
            "    // Trigger: hold Ctrl and press K\n" +
            "    \"trigger_mods\": \"Ctrl\",\n" +
            "    \"trigger_key\": \"K\",\n" +
            "    // Abort combo: interrupts the running sequence at any time\n" +
            "    \"abort_mods\": \"Ctrl+Alt\",\n" +
            "    \"abort_key\": \"F12\",\n" +
            "    // Default pause between steps in milliseconds (1000 = 1s, 3500 = 3.5s)\n" +
            "    \"default_delay_ms\": 3000,\n" +
            "    // debug: true writes a viperkey.log file next to the exe\n" +
            "    \"debug\": false,\n" +
            "    // steps: one object per step. Keys joined with '+' form a chord:\n" +
            "    // pressed in the order written, held briefly, released in reverse.\n" +
            "    // A modifier prefixes ONLY the key that follows it, so ~+1 == Shift+grave+1\n" +
            "    // (a tilde, then 1).\n" +
            "    // Optional per-step delay_ms overrides the default pause AFTER that step.\n" +
            "    \"steps\": [\n" +
            "        // EXAMPLE steps - replace these with your own sequence.\n" +
            "        { \"keys\": \"~+1\", \"delay_ms\": 1000 },\n" +
            "        { \"keys\": \"Alt+B\" }\n" +
            "    ]\n" +
            "}\n";
        File.WriteAllText(path, txt, Encoding.UTF8);
    }

    // ---------- logging ----------
    static bool PeekDebugFlag(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            Dictionary<string, object> root = (Dictionary<string, object>)ser.DeserializeObject(StripJsonComments(text));
            object o;
            if (root != null && root.TryGetValue("debug", out o))
                return Convert.ToBoolean(o);
        }
        catch { }
        return false;
    }

    static void SetLogging(bool debug)
    {
        if (logWriter != null)
        {
            logWriter.Flush();
            logWriter.Dispose();
            logWriter = null;
        }
        if (!debug)
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return;
        }
        try
        {
            string logPath = Path.Combine(RuntimeDir(), "viperkey.log");
            StreamWriter sw = new StreamWriter(logPath, true, Encoding.UTF8);
            sw.AutoFlush = true;
            logWriter = sw;
            Console.SetOut(logWriter);
            Console.SetError(logWriter);
        }
        catch
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
    }

    // ---------- input ----------
    // A step is a chord: each key is PRESSED DOWN while the previous keys stay
    // held, so `+1 = hold grave, then press 1, then release 1, then release grave.
    static bool FireChord(KeyToken kt)
    {
        byte mods = 0;
        foreach (KeyStroke st in kt.Strokes) mods |= st.Mods;
        List<byte> held = new List<byte>();
        for (int k = 0; k < kt.Strokes.Count; k++)
        {
            held.Add(kt.Strokes[k].Hid);
            viiper.SendState(mods, held.ToArray());
            if (SleepAbortable(60))
            {
                viiper.SendState(0, new byte[0]);
                return false;
            }
        }
        if (SleepAbortable(80))   // both keys genuinely down together
        {
            viiper.SendState(0, new byte[0]);
            return false;
        }
        for (int k = kt.Strokes.Count - 1; k >= 1; k--)
        {
            held.RemoveAt(k);
            viiper.SendState(mods, held.ToArray());
            if (SleepAbortable(60))
            {
                viiper.SendState(0, new byte[0]);
                return false;
            }
        }
        viiper.SendState(0, new byte[0]);   // release last key + all modifiers
        Thread.Sleep(60);
        return true;
    }

    static void PressKey(byte mod, byte[] keys)
    {
        viiper.SendState(mod, keys);
        Thread.Sleep(40);
        viiper.SendState(0, new byte[0]);
        Thread.Sleep(40);
    }

    // Abort combo (configurable via abort_mods/abort_key in JSON).
    static string AbortComboName()
    {
        if (cfg.AbortModVks.Count == 0) return cfg.AbortKeyName;
        return AbortModsNamesJoined() + "+" + cfg.AbortKeyName;
    }

    static string AbortModsNamesJoined()
    {
        if (cfg.AbortModVks.Count == 0) return "none";
        List<string> names = new List<string>();
        foreach (byte vk in cfg.AbortModVks)
        {
            switch (vk)
            {
                case VK_CONTROL: names.Add("Ctrl"); break;
                case VK_SHIFT: names.Add("Shift"); break;
                case VK_MENU: names.Add("Alt"); break;
                default: names.Add("Win"); break;
            }
        }
        return String.Join("+", names.ToArray());
    }

    static bool AbortHeld()
    {
        if (abortRequested) return true;
        for (int i = 0; i < cfg.AbortModVks.Count; i++)
            if (!KeyDown(cfg.AbortModVks[i])) return false;
        return KeyDown(cfg.AbortKeyVk);
    }

    static void FireSequence()
    {
        for (int i = 0; i < cfg.Sequence.Count; i++)
        {
            if (AbortHeld())
            {
                Console.WriteLine("[{0:HH:mm:ss}] ABORTED by {1}", DateTime.Now, AbortComboName());
                SendAllKeysUp();
                return;
            }

            KeyToken kt = cfg.Sequence[i];
            Console.WriteLine("[{0:HH:mm:ss}] firing {1}", DateTime.Now, kt.Name);
            if (!FireChord(kt))
            {
                Console.WriteLine("[{0:HH:mm:ss}] ABORTED by {1}", DateTime.Now, AbortComboName());
                SendAllKeysUp();
                return;
            }

            if (i < cfg.Sequence.Count - 1)
            {
                int d = kt.DelayMs >= 0 ? kt.DelayMs : cfg.DefaultDelayMs;
                Console.WriteLine("[{0:HH:mm:ss}] waiting {1}ms", DateTime.Now, d);
                for (int waited = 0; waited < d; waited += 50)
                {
                    if (AbortHeld())
                    {
                        Console.WriteLine("[{0:HH:mm:ss}] ABORTED by {1}", DateTime.Now, AbortComboName());
                        SendAllKeysUp();
                        return;
                    }
                    Thread.Sleep(Math.Min(50, d - waited));
                }
            }
        }
        Console.WriteLine("[{0:HH:mm:ss}] macro complete", DateTime.Now);
    }

    static void SendAllKeysUp()
    {
        viiper.SendState(0, new byte[0]);
    }

    static bool AnyModDown()
    {
        for (int i = 0; i < cfg.TriggerModVks.Count; i++)
            if (KeyDown(cfg.TriggerModVks[i])) return true;
        return false;
    }

    static string TriggerModNamesJoined()
    {
        if (cfg.TriggerModVks.Count == 0) return "none";
        List<string> names = new List<string>();
        foreach (byte vk in cfg.TriggerModVks)
        {
            switch (vk)
            {
                case VK_CONTROL: names.Add("Ctrl"); break;
                case VK_SHIFT: names.Add("Shift"); break;
                case VK_MENU: names.Add("Alt"); break;
                default: names.Add("Win"); break;
            }
        }
        return String.Join("+", names.ToArray());
    }

    // ---------- main ----------
    [STAThread]
    static int Main()
    {
        cfgPathField = Path.Combine(RuntimeDir(), "viperkey.json");
        if (!File.Exists(cfgPathField))
        {
            WriteDefaultJson(cfgPathField);
            MessageBox.Show("viperkey.json was not found.\n\n" +
                "A template has been created at:\n" + cfgPathField + "\n\n" +
                "Fill in your macro steps, then start the tool again.",
                "Macro Tool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        SetLogging(PeekDebugFlag(cfgPathField));

        cfg = LoadJsonConfig(cfgPathField);
        if (cfg == null)
        {
            MessageBox.Show("Config error in " + cfgPathField + ".\n\n" +
                (LastConfigLoadError ?? "invalid JSON") + "\n\nRun again after fixing it.",
                "Macro Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        SetLogging(cfg.Debug);

        Console.WriteLine("Virtual keyboard macro tool [" + REV + "]");
        Console.WriteLine("Config: {0}", cfgPathField);
        Console.WriteLine("Trigger: {0}+{1}", TriggerModNamesJoined(), cfg.TriggerKeyName);
        Console.WriteLine("Default delay: {0}ms", cfg.DefaultDelayMs);
        for (int i = 0; i < cfg.Sequence.Count; i++)
        {
            KeyToken kt = cfg.Sequence[i];
            int d = kt.DelayMs >= 0 ? kt.DelayMs : cfg.DefaultDelayMs;
            Console.WriteLine("  step {0}: {1}    (wait {2}ms)", i + 1, kt.Name, d);
        }
        Console.WriteLine("Contacting VIIPER server (127.0.0.1:3242)...");

        viiper = new ViiperEngine();
        if (!viiper.EnsureServer())
        {
            MessageBox.Show("VIIPER server (viiper.exe) is not reachable on port 3242.\n\n" +
                "Install VIIPER with:\n  irm https://alia5.github.io/VIIPER/stable/install.ps1 | iex\n" +
                "or drop viiper.exe next to viperkey.exe - the tool starts it automatically.",
                "Macro Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        Console.WriteLine("VIIPER server reachable.");

        if (!viiper.EnsureDevice())
        {
            MessageBox.Show("Could not create the virtual keyboard via VIIPER.\n" +
                "Check that the usbip-win2 driver is installed (USBip-*.exe).",
                "Macro Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        SetupTray();

        Console.WriteLine("Armed. Tray icon active. Press {0}+{1} to fire.", TriggerModNamesJoined(), cfg.TriggerKeyName);
        Console.WriteLine("ABORT: {0} interrupts any running sequence.", AbortComboName());
        Console.WriteLine("Config hot-reload active - edit viperkey.json and save.");

        RefreshAbortHookState();
        StartKeyboardHook();
        StartAbortWatchdog();
        abortForm = new AbortForm();
        RegisterAbortHotkey();

        Thread poller = new Thread(PollLoop);
        poller.IsBackground = true;
        poller.Start();

        Application.Run();

        Console.WriteLine("Exiting.");
        UnregisterAbortHotkey();
        viiper.SendState(0, new byte[0]);
        viiper.Dispose();
        return 0;
    }

    static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max - 1) + "...";
    }

// base64-encoded fallbacks (used only if the .ico files are missing)
    const string EMBEDDED_ICON =
                "AAABAAEAGBgAAAEAIACyBgAAFgAAAIlQTkcNChoKAAAADUlIRFIAAAAYAAAAGAgGAAAA4Hc9+AAABmdJREFUeAF8VAlQU1cUPflZ" +
                "SFBWG7YEGoqAAg5FW52RaXVGnVodJ6CEKopjq6hQtW4dKkvdQOq4gjogKFhb6aCo1Na907FWmbpQFygquIPBJASSkEAg/yd977uM" +
                "qOOfd+9/7y7n3Lx7fxi843FZLBG2ih+zjJrkM7qh0dq2gED2KRH90ChtZ5LmjK2iIovGvAMCbyVw2e0RppzcA62h4Xc6MjLyHfcf" +
                "TBCNGhUom/OlUDZnjpDuqc2UnpH/NPSDO+acnAM0521EbxD0XLyY1qJQNXbmrU9xT50F/5s34H/1Mnwr9sKrIB+eBRvgU1EOed0V" +
                "+NXfhHT2bFjy8lK0ypBGmvs6ST8C26GarNb4+FJGFSpUPm6B77bNEA8Oez3n5Vk0eDC8t25BAIllVCqhjuTaDh3OehlANi8JbOcv" +
                "prVqEvIHJCRBcbUWomAlcfdfzvZ2OBoa0HfpMljydtlsfACNDbhyCbKEadBppuX3ECzeQRRPQO9Pq04slo6MR+DRQ8RMlstFFMAZ" +
                "O9CRvRqGGakwpi+CZeMm2MrLYd2xE6bFS2DOygHX+oSP9TtaDenI0TCo1cUUkxoZqnQ5a1f3dXYKg2qeg1OjQIC+h4/QkqABIx8E" +
                "ybAYCIQMXCYTOG0bnBYLhAoFxDHRMOWuRu+FWpoFec1hODrNQiPBpAbGZTBEGEv3pPhmZkIUGEhtvFgv/oO2nLXwmJ4MUXgEOgt3" +
                "ouu3U+i+VIeeazfR/dcFmDZtg7lgIzwyFsJacwycXk8wAuCdmwMLwaTYjKHqSBLXx2HQ4nQeuOPEaTzIWAb95u1gxGLYr99A+9ZC" +
                "9EllkCSqIZs/Dx7f58CrcDt89pVD+NHHaF+6EjL1FFhPneUxPNO+gtPhgLnqUBLTdfzkWOnwWIgVQWgn4J0kSMSx8Fu2CMEVuxGQ" +
                "9S1UZ34HFMGQjh0Ded4aeM+fCw/NVPLrNPCrKMOgkl1wWa1wyWRwOZ38gIjjYmE9fmos01N/K0YaG8szu0dGwC06Gv5rsuE2PA49" +
                "J0/DZe+F02yG000C75QvANJ8trkZfbW1sB89ip7qw3AbFo0Bn38GkVIJx4OHPJY4Lg7d/92KYRxms59YEcgb3cNC4a9JgCQwAC6t" +
                "Ft2NtyEaOgS6n6vg9PGG0F0GCASkJ+GQjB4NaWIixHEfwrJuPVykCCkhos0HeUTKILAmsx/DkYqcAn6YiBkQ+/rw75blq+CzYC7M" +
                "1+vRQgikUVG8/XUlCguDe0oKzKRPooEDyFw7+RAXIwRHJp1hvDz1Dp2ON1JlJ6N5fUQ8pJ+MBjNwICTeXlBkpCHku+XU/VYRka+d" +
                "6+4BWBYc6QUNYp/qwXh66Bm3yPAG6416agNtkK7qCDwmjIMi8xmgTBUCxZyZ4KtzkpL4yP6K9ojt6oKLEDj07byzm4yyJDyygfGa" +
                "OP6cpe5fOAwGCBgGiq/nI+yHdXwQyPU92zzXjOD55tmrt74BbFMz9HkbIRkxHLYbDRCS/jntdliv1MFj0vhzzHspydUcy6GtdB+f" +
                "xVfK74gSPAd8hchOpsRYeRDGkr1gyd+Ibnc5WEsXPGfNgK58PzzI1eqKSshtsfAm2IxUqWySz5pR+WB9PrguK0El6xVAcgKdHMvV" +
                "a2hatBztVdVgyDSxAhdaNxVCFBQERXEhGtTTEbDk2cfakrsGvqkzKik2A/KoCtasdTqcXIMmlZzIopVTEirkSBdHqnSPCEev9ime" +
                "FJehp/keQkt3wH/FYjzashPy5GkYED0UjZMSwTo5TkEwaR5PIJXLm4ZVV6ZrT9fg9oJvqB20aip97UZc+XgMGlPTYDx+GjLyXQyp" +
                "+gmqzQWQKIJAn+AlCxE0bzbuZixF28kaRBz8JZ1iUh9PQDfyqVPKYop2Z98vLcL1yRrQRlF7x59/Q56UgBG1fyCWFBCUPg8iMrrU" +
                "90KEYhHq1cm4T65qCMGgWC98LwmoIWTJgg0jqn+drz9xhjsfEoOWXWV4b+I4qDKXQfp+MA3pJyz56368ZQfOyfxhOHaCG05ylQTj" +
                "1aB+BNQRkKQuG2O4FyVXT668vSIXlKhuYhKaV63D46LdvNzNWodrE6fhwvsxuJu5Gn7TkyrjDQ+j5CSXYrwqbxBQJ72/mD1FMz9t" +
                "rI0MWZqR3dvRefZxSXnbnZW5HJVHxeVtfR2ms8pli7Pjm65GRu8vmUlzaO7r8j8AAAD//5DcaBgAAAAGSURBVAMA68Oz6EXMEn8A" +
                "AAAASUVORK5CYII=" +
        "";
    const string EMBEDDED_ALERT =
                "AAABAAEAGBgAAAEAIACICQAAFgAAACgAAAAYAAAAMAAAAAEAIAAAAAAAYAkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAA+Pj/JsfC7np1bNW6NSfD5hgIu/wYCLv8NSfD5nVs17rHwu56+Pj/JgAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP///yahm+OlFwe6/x4Pvf9TR8z/gXja/5iR4f+YkeH/gXnb/1JHzv8bDr//EQO9" +
                "/56X5aX///8mAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA4d71ZREDuvwjFb//mpTi//Lx+///////////////" +
                "///////////////////v7/v/kozh/xQGv/8OA7784d73ZQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADPy/B6AAC1/1RKz//3" +
                "9/3//////6+p5/+OiN//4uD3//////////////////////////////////39//9VTtL/AAC5/8/L8noAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAOHe92UAALb/bmbX/////////////////+zr+f8+NMr/JhrE/09G0P+bluX/8O/7/////////////////1RQ5v9UUOb/VFDm" +
                "/wAAvP/h3vdlAAAAAAAAAAAAAAAA////JhAFv/xQR9D////////////+/v///v7/////////////wb3u/5aR4/9zbdz/TUbT/3hz" +
                "3//l5Pn/VFDm/1RQ5v9UUOb/VFDm/1RQ5v8PB8b8////JgAAAAAAAAAAn5vmpQ8HwP/z8vz///////7+////////////////////" +
                "////zsvx/3Fr2f+Lh+P/sq/u/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v8PBsn/n5vopQAAAAD4+P8mEgjD/4qE4f//////" +
                "///////////////////////////+/v/////////////f3vf/VFDm/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v+Pieb/EgfL" +
                "//j4/ybJx/J6FQzG/+rq+v//////9PP7/////////////v7///////////////////////9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ" +
                "5v9UUOb/VFDm/8XC8v/39/3/FgvP/8nH9Xp7dd+6Rz/U///////9/f7/jojg/3133P//////////////////////VFDm/1RQ5v9U" +
                "UOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/9/f+/62p7///////SUDb/3t15bo5MNPmdG7g////////////4N/3/zoyz/+NiOT/" +
                "/////+Hf9v9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm//////94dOz/+/v//8G/9P//////d3Hl/zkw2OYaD838i4Xl" +
                "//////////////////////9TS9z/VFDm/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v9UUOb//////9zb+/+Bfu///////83L" +
                "9///////jYnp/xkQ1fwbEND8iYTn////////////7u36//////9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v//" +
                "////3Nz7/4KB8f/w8P7/9PP7/8/O9///////iofq/xkR1vw7MdfmcGni///////e3PX/npjj/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/" +
                "VFDm/1RQ5v9UUOb/2tr7/8XF+f+urvf/rKv2////////////ycbz/93c+f//////cGvm/zkz3uaAeuW6QTra//////9UUOb/VFDm" +
                "/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v///////////////////////////9TS+P+4tvT/rarv//39/v//////Pzrh/356" +
                "6rrPzfV6EgjS/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/JR7Z/2Bb5P9/e+r/nJnv/62q8v+qp/L/ZGDp/z865f/E" +
                "wvX/pKHw///////j4vv/EAnc/83N+Xr///8mVFDm/1RQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm/1RQ5v+3tPP/HBbf/xEK3v8bFeH/" +
                "IBvi/x0Z4/8MB+H/Lyzm/8vK+f93c+z/6en8//////+Bfe3/FQ7e/////yYAAAAAU1DmpVRQ5v9UUOb/VFDm/1RQ5v9UUOb/VFDm" +
                "/0I83/+Rj+//iIXw/1lX6/9OTOv/Tkzr/1lY7f99fPH/pqX1/2Ng7P/Fw/j//////+fm+/8FAN3/qKTzpQAAAAAAAAAAV1HkJlRQ" +
                "5vxUUOb/VFDm/1RQ5v/+/v///////+3s/P9+eu3/XFjq/1dU6v9eXOz/Z2bu/2dm7/9iYe7/e3nw/+Tj/P///////////zo25v8X" +
                "FOH8////JgAAAAAAAAAAAAAAAFNR5mVUUOb/VFDm/////////////////////////////////+vr/f/T0/r/ycj5/9PT+//09P7/" +
                "////////////////U1Dp/wAA3v/o5vxlAAAAAAAAAAAAAAAAAAAAAAAAAADZ1/t6AADb/zkz5P/j4vv/////////////////////" +
                "/////////////////////////////////+Pi+/82NOf/AADg/9fX+3oAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA6Oj8ZRsW" +
                "4fwAAN//c3Dt/9zb+v/////////////////////////////////b2/v/cnHv/wAA4/8ZF+T86Oj8ZQAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAP///yasqvalFxPj/wsJ4/81M+j/YmDt/3p48P95ePH/YWDu/zQy6v8JCOX/FBPn/6qq9qX///8m" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA////JtHR+3qEgvO6QD7q5hwb5vwcG+f8Pz7r" +
                "5oKC87rR0ft6////JgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" +
        "";
    static Icon LoadIcon(string path, string embeddedB64)
    {
        try { if (File.Exists(path)) return new Icon(path); } catch { }
        try { if (!string.IsNullOrEmpty(embeddedB64)) { using (var ms = new MemoryStream(Convert.FromBase64String(embeddedB64))) return new Icon(ms); } } catch { }
        return null;
    }

    static void SetupTray()
    {
        notifyIcon = new NotifyIcon();
        try
        {
            string iconPath = Path.Combine(RuntimeDir(), "icon.ico");
            notifyIcon.Icon = LoadIcon(iconPath, EMBEDDED_ICON);
            if (notifyIcon.Icon == null) notifyIcon.Icon = SystemIcons.Application;
        }
        catch
        {
            notifyIcon.Icon = SystemIcons.Application;
        }
notifyIcon.Text = "Macro Tool";
        normalTrayIcon = notifyIcon.Icon;
        try
        {
            string alertPath = Path.Combine(RuntimeDir(), "alert.ico");
            alertTrayIcon = LoadIcon(alertPath, EMBEDDED_ALERT);
        }
        catch { }
        if (alertTrayIcon == null) alertTrayIcon = normalTrayIcon;

        configFlashTimer = new System.Threading.Timer(ConfigFlashTick, null, Timeout.Infinite, Timeout.Infinite);

        ContextMenuStrip menu = new ContextMenuStrip();

        miConfigError = new ToolStripMenuItem("Invalid config");
        miConfigError.Visible = false;
        miConfigError.Image = SystemIcons.Warning.ToBitmap();
        miConfigError.Click += (s, e) => ClearConfigError();
        menu.Items.Insert(0, miConfigError);

        miTrigger = new ToolStripMenuItem("Trigger");
        miTrigger.Click += (s, e) => OpenConfigJson();
        menu.Items.Add(miTrigger);

        miAbort = new ToolStripMenuItem("Abort");
        miAbort.Click += (s, e) => OpenConfigJson();
        menu.Items.Add(miAbort);

        menu.Items.Add(new ToolStripSeparator());
        RefreshComboMenu();

        ToolStripMenuItem miReload = new ToolStripMenuItem("Reload Config");
        miReload.Click += (s, e) => { reloadNow = true; };
        menu.Items.Add(miReload);

        ToolStripMenuItem miStartup = new ToolStripMenuItem("Run at Startup");
        miStartup.CheckOnClick = true;
        miStartup.Checked = GetRunAtStartup();
        miStartup.CheckedChanged += (s, e) => SetRunAtStartup(miStartup.Checked);
        menu.Items.Add(miStartup);

        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
        miExit.Click += (s, e) => { exitRequested = true; Application.Exit(); };
        menu.Items.Add(miExit);

        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left && pendingConfigError != null)
                ShowTrayBalloon("Macro Tool - invalid config", pendingConfigError, ToolTipIcon.Error);
        };
        notifyIcon.Visible = true;
    }

    static void ConfigFlashTick(object state)
    {
        try
        {
            if (!configErrorActive) return;
            flashOnAlert = !flashOnAlert;
            notifyIcon.Icon = flashOnAlert ? alertTrayIcon : normalTrayIcon;
        }
        catch { }
    }

    static void ShowConfigError(string reason)
    {
        pendingConfigError = Truncate(reason, 100);
        configErrorActive = true;
        flashOnAlert = false;
        notifyIcon.Icon = normalTrayIcon;
        notifyIcon.Text = Truncate("Macro Tool - INVALID config: " + reason, 60);
        miConfigError.Text = "Invalid config - old config kept";
        miConfigError.Visible = true;
        configFlashTimer.Change(0, 500);
        ShowTrayBalloon("Macro Tool",
            "Invalid config - keeping previous config. Reason: " + Truncate(reason, 90),
            ToolTipIcon.Error);
    }

    static void ClearConfigError()
    {
        pendingConfigError = null;
        configErrorActive = false;
        configFlashTimer.Change(Timeout.Infinite, Timeout.Infinite);
        notifyIcon.Icon = normalTrayIcon;
        notifyIcon.Text = "Macro Tool";
        miConfigError.Visible = false;
    }

    static bool GetRunAtStartup()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY))
                return k != null && k.GetValue("ViperKey") != null;
        }
        catch
        {
            return false;
        }
    }

    static void SetRunAtStartup(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RUN_KEY))
            {
                if (on)
                    k.SetValue("ViperKey", "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                else
                    k.DeleteValue("ViperKey", false);
            }
        }
        catch
        {
            Console.WriteLine("[{0:HH:mm:ss}] Could not update run-at-startup registry.", DateTime.Now);
        }
    }

    static bool AbortComboPhysicallyDown()
    {
        for (int i = 0; i < cfg.AbortModVks.Count; i++)
            if (!KeyDown(cfg.AbortModVks[i])) return false;
        return KeyDown(cfg.AbortKeyVk);
    }

    static void PollLoop()
    {
        using (FileSystemWatcher watcher = new FileSystemWatcher(Path.GetDirectoryName(cfgPathField)))
        {
            watcher.Filter = Path.GetFileName(cfgPathField);
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Changed += (s, e) => { Interlocked.Exchange(ref cfgChangeTicks, DateTime.UtcNow.Ticks); };
            watcher.EnableRaisingEvents = true;

            bool comboHeld = false;
            while (!exitRequested)
            {
                if (reloadNow)
                {
                    reloadNow = false;
                    TryReloadConfig();
                }

                long change = Volatile.Read(ref cfgChangeTicks);
                if (change > cfgReloadedTicks && (DateTime.UtcNow.Ticks - change) >= ReloadDebounceTicks)
                {
                    cfgReloadedTicks = change;
                    TryReloadConfig();
                }

                viiper.EnsureDevice();

                bool mods = true;
                for (int i = 0; i < cfg.TriggerModVks.Count; i++)
                    if (!KeyDown(cfg.TriggerModVks[i])) { mods = false; break; }
                bool key = KeyDown(cfg.TriggerKeyVk);

                if (mods && key && !comboHeld)
                {
                    comboHeld = true;
                    Console.WriteLine("[{0:HH:mm:ss}] {1}+{2} pressed...", DateTime.Now,
                        TriggerModNamesJoined(), cfg.TriggerKeyName);

                    while (KeyDown(cfg.TriggerKeyVk) || AnyModDown())
                        Thread.Sleep(20);
                    Console.WriteLine("[{0:HH:mm:ss}] released -> firing", DateTime.Now);
                    macroActive = true;
                    try { FireSequence(); }
                    finally { macroActive = false; }
                    Console.WriteLine();
                    comboHeld = false;
                }
                else if (!mods && !key)
                {
                    comboHeld = false;
                }

                // re-arm after the abort combo is released
                if (abortRequested && !AbortComboPhysicallyDown())
                {
                    if (abortSettledTicks == 0) abortSettledTicks = Environment.TickCount;
                    else if (Environment.TickCount - abortSettledTicks >= 250)
                    {
                        abortRequested = false;
                        abortSettledTicks = 0;
                    }
                }
                else abortSettledTicks = 0;

                Thread.Sleep(15);
            }
        }
    }

    static void TryReloadConfig()
    {
        Config fresh = LoadJsonConfig(cfgPathField);
        if (fresh != null)
        {
            cfg = fresh;
            SetLogging(fresh.Debug);
            RefreshAbortHookState();
            RegisterAbortHotkey();
            ClearConfigError();
            RefreshComboMenu();
            Console.WriteLine("[{0:HH:mm:ss}] Config reloaded. New trigger: {1}+{2}, {3} steps.",
                DateTime.Now, TriggerModNamesJoined(), cfg.TriggerKeyName, cfg.Sequence.Count);
        }
        else
        {
            Console.WriteLine("[{0:HH:mm:ss}] Config reload FAILED - keeping previous config.", DateTime.Now);
            ShowConfigError(LastConfigLoadError ?? "invalid JSON");
        }
    }

    static void RefreshComboMenu()
    {
        try
        {
            if (miTrigger != null)
                miTrigger.Text = "Trigger: " + TriggerModNamesJoined() + "+" + cfg.TriggerKeyName;
            if (miAbort != null)
                miAbort.Text = "Abort: " + AbortComboName();
        }
        catch { }
    }

    static void OpenConfigJson()
    {
        try { System.Diagnostics.Process.Start(cfgPathField); }
        catch { }
    }

    static void ShowTrayBalloon(string title, string body, ToolTipIcon icon)
    {
        try
        {
            notifyIcon.BalloonTipTitle = title;
            notifyIcon.BalloonTipText = body;
            notifyIcon.BalloonTipIcon = icon;
            notifyIcon.ShowBalloonTip(5000);
        }
        catch { }
    }
}