// WattWidget - desktop-pinned power draw widget with history graphs.
// On battery: true system draw from battery telemetry (also used to self-calibrate
// the AC estimate's base offset). On AC: CPU+GPU package power via LibreHardwareMonitor
// plus the learned base (display/RAM/SSD) offset.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using LibreHardwareMonitor.Hardware;

[assembly: AssemblyTitle("WattAWidget")]
[assembly: AssemblyProduct("WattAWidget")]
[assembly: AssemblyDescription("Desktop power draw widget with history graphs")]
[assembly: AssemblyCompany("Vinyas")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Vinyas")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WattAWidget
{
    static class Program
    {
        // LibreHardwareMonitorLib + HidSharp are embedded as resources so the exe
        // ships as a single file; this loads them on demand.
        static Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (name != "LibreHardwareMonitorLib" && name != "HidSharp") return null;
            using (var st = typeof(Program).Assembly.GetManifestResourceStream(name + ".dll"))
            {
                if (st == null) return null;
                var buf = new byte[st.Length];
                int off = 0;
                while (off < buf.Length)
                {
                    int n = st.Read(buf, off, buf.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                return Assembly.Load(buf);
            }
        }

        // separate non-inlined method so no LHM-referencing type loads before the
        // AssemblyResolve hook is registered
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RunApp()
        {
            Application.Run(new WidgetForm());
        }

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            bool takeover = args.Length > 0 && args[0] == "--takeover";
            var mutex = new Mutex(false, "WattAWidget_SingleInstance");
            bool got = false;
            int tries = takeover ? 100 : 1;
            for (int i = 0; i < tries && !got; i++)
            {
                try { got = mutex.WaitOne(takeover ? 100 : 0); }
                catch (AbandonedMutexException) { got = true; }
            }
            if (!got) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RunApp();
            GC.KeepAlive(mutex);
        }
    }

    // Documented shell COM interface: lets a process ask which virtual desktop is
    // current and move its own windows there.
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    class VirtualDesktopManagerCls { }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        public const uint GW_HWNDPREV = 3;
        public const int DWMWA_CLOAKED = 14;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int L, T, R, B; }
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
        public const int SW_SHOWNOACTIVATE = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }
    }

    class PowerSample
    {
        public DateTime Time;
        public double Watts;
        public PowerSample(DateTime t, double w) { Time = t; Watts = w; }
    }

    class BatteryState
    {
        public bool Present, PowerOnline, Charging;
        public double ChargeW, DischargeW;
        public int Percent = -1;
        public double HoursLeft = -1;
    }

    static class BatteryReader
    {
        public static BatteryState Read()
        {
            var st = new BatteryState();
            try
            {
                double remaining = 0;
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM BatteryStatus"))
                using (var col = s.Get())
                {
                    foreach (ManagementObject o in col)
                        using (o)
                        {
                            st.Present = true;
                            st.PowerOnline = st.PowerOnline || Convert.ToBoolean(o["PowerOnline"]);
                            st.Charging = st.Charging || Convert.ToBoolean(o["Charging"]);
                            st.ChargeW += Convert.ToDouble(o["ChargeRate"]) / 1000.0;
                            st.DischargeW += Convert.ToDouble(o["DischargeRate"]) / 1000.0;
                            remaining += Convert.ToDouble(o["RemainingCapacity"]);
                        }
                }
                if (st.Present)
                {
                    double full = SumWmi("BatteryFullChargedCapacity", "FullChargedCapacity");
                    if (full > 0) st.Percent = (int)Math.Round(100.0 * remaining / full);
                    if (st.DischargeW > 0.1) st.HoursLeft = (remaining / 1000.0) / st.DischargeW;
                }
            }
            catch { }
            return st;
        }

        public static double SumWmi(string cls, string prop)
        {
            double sum = 0;
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM " + cls))
            using (var col = s.Get())
            {
                foreach (ManagementObject o in col)
                    using (o) sum += Convert.ToDouble(o[prop]);
            }
            return sum;
        }
    }

    class PowerReading
    {
        public double Cpu, Gpu;
        public bool HasCpu, HasGpu;
        public bool Any { get { return HasCpu || HasGpu; } }
        public double Total { get { return (HasCpu ? Cpu : 0) + (HasGpu ? Gpu : 0); } }
    }

    // Reads CPU package + discrete GPU power via LibreHardwareMonitor.
    class LhmReader
    {
        Computer computer;

        public void Init()
        {
            try
            {
                var c = new Computer();
                c.IsCpuEnabled = true;
                c.IsGpuEnabled = true;
                c.Open();
                computer = c;
            }
            catch { computer = null; }
        }

        public PowerReading Read()
        {
            if (computer == null) return null;
            try
            {
                var r = new PowerReading();
                foreach (IHardware hw in computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        hw.Update();
                        ISensor best = null;
                        foreach (ISensor sn in hw.Sensors)
                        {
                            if (sn.SensorType != SensorType.Power || !sn.Value.HasValue) continue;
                            if (sn.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0) { best = sn; break; }
                            if (best == null || sn.Value.Value > best.Value.Value) best = sn;
                        }
                        if (best != null && best.Value.Value > 0) { r.Cpu += best.Value.Value; r.HasCpu = true; }
                    }
                    else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd)
                    {
                        // Integrated Radeon ("AMD Radeon(TM) Graphics") power is already part of
                        // the CPU package number - adding it again would double-count.
                        if (hw.HardwareType == HardwareType.GpuAmd &&
                            hw.Name.IndexOf("(TM) Graphics", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        hw.Update();
                        ISensor best = null;
                        foreach (ISensor sn in hw.Sensors)
                        {
                            if (sn.SensorType != SensorType.Power || !sn.Value.HasValue) continue;
                            if (best == null || sn.Value.Value > best.Value.Value) best = sn;
                        }
                        if (best != null && best.Value.Value > 0) { r.Gpu += best.Value.Value; r.HasGpu = true; }
                    }
                }
                return r.Any ? r : null;
            }
            catch { return null; }
        }

        public void Close()
        {
            try { if (computer != null) computer.Close(); } catch { }
            computer = null;
        }
    }

    class WidgetForm : Form
    {
        const int SparkMinutes = 30;
        const int CalMinSamples = 60; // ~2 min on battery before the base offset is trusted

        float sc = 1f;
        int W, HCompact, HExpanded;
        int viewMode;   // 0 compact, 1 last-24h, 2 last-7d
        int themeMode;  // 0 auto, 1 light, 2 dark
        bool systemLight;
        volatile bool running = true;
        Thread pollThread;
        readonly LhmReader lhm = new LhmReader();
        readonly object sync = new object();

        // display state (poll thread writes under sync, paint reads under sync)
        string mainText = "--";
        Color mainColor = Color.White;
        string subText = "starting...";
        string badgeText = "";
        Color badgeColor = Color.Gray;

        readonly List<PowerSample> recent = new List<PowerSample>();
        // hour -> {sum of minute avgs, count, ac minutes, battery minutes}
        readonly Dictionary<DateTime, double[]> hourly = new Dictionary<DateTime, double[]>();
        // date -> {sum of minute avgs, count}; Wh for a day = sum / 60
        readonly Dictionary<DateTime, double[]> daily = new Dictionary<DateTime, double[]>();
        double sessSum; int sessCount; double sessPeak;
        DateTime curMinute = DateTime.MinValue; double minSum; int minCount; int minAc;

        // learned base offset: battery total minus CPU+GPU estimate (display/RAM/SSD/fans)
        double baseOffset; int baseN;

        double costRate; // per kWh; 0 = hide cost

        string dataDir, histDir, settingsFile;
        bool isAdmin;

        bool dragging; Point dragStart;

        Font fMain, fSub, fTiny, fCap;

        // theme palette (set by ApplyTheme)
        bool lightMode;
        Color tBack, tBorder, tDivider, tDim, tSub, tFaint, tNeutral;
        Color tGreen, tAmber, tRed, tBlue, tGridA, tGridB;

        readonly AutoResetEvent pollWake = new AutoResetEvent(false);
        IVirtualDesktopManager vdm;

        public WidgetForm()
        {
            try { vdm = (IVirtualDesktopManager)new VirtualDesktopManagerCls(); } catch { vdm = null; }
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattAWidget");
            // one-time migration from the app's old name
            string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattWidget");
            if (!Directory.Exists(dataDir) && Directory.Exists(oldDir))
                try { Directory.Move(oldDir, dataDir); } catch { }
            histDir = Path.Combine(dataDir, "history");
            try { Directory.CreateDirectory(histDir); } catch { }
            settingsFile = Path.Combine(dataDir, "settings.ini");
            isAdmin = CheckAdmin();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;

            using (var g = Graphics.FromHwnd(IntPtr.Zero)) sc = g.DpiX / 96f;
            W = S(250); HCompact = S(124); HExpanded = S(262);

            fMain = new Font("Segoe UI", 22f, FontStyle.Bold);
            fSub = new Font("Segoe UI", 8.25f);
            fTiny = new Font("Segoe UI", 6.8f);
            fCap = new Font("Segoe UI", 7f, FontStyle.Bold);

            LoadSettings();
            UpdateSystemTheme();
            ApplyTheme();
            mainColor = tNeutral;
            ApplySize();
            LoadHistory();
            BuildMenu();

            Shown += OnShownPin;
            Activated += delegate { SinkToBottom(); };
            MouseDown += OnDragDown;
            MouseMove += OnDragMove;
            MouseUp += OnDragUp;
            DoubleClick += delegate { CycleView(); };
            FormClosing += OnClosingCleanup;

            pollThread = new Thread(PollLoop);
            pollThread.IsBackground = true;
            pollThread.Start();
        }

        int S(double v) { return (int)Math.Round(v * sc); }

        static bool CheckAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ---------- theme ----------

        void UpdateSystemTheme()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 0);
                systemLight = (v is int) && (int)v != 0;
            }
            catch { systemLight = false; }
        }

        bool EffectiveLight()
        {
            return themeMode == 1 || (themeMode == 0 && systemLight);
        }

        void ApplyTheme()
        {
            lightMode = EffectiveLight();
            if (lightMode)
            {
                tBack = Color.FromArgb(246, 246, 249);
                tBorder = Color.FromArgb(203, 203, 211);
                tDivider = Color.FromArgb(224, 224, 230);
                tDim = Color.FromArgb(118, 118, 130);
                tSub = Color.FromArgb(92, 92, 105);
                tFaint = Color.FromArgb(148, 148, 158);
                tNeutral = Color.FromArgb(128, 128, 138);
                tGreen = Color.FromArgb(32, 148, 78);
                tAmber = Color.FromArgb(188, 136, 8);
                tRed = Color.FromArgb(203, 68, 48);
                tBlue = Color.FromArgb(24, 102, 205);
                tGridA = Color.FromArgb(50, 0, 0, 0);
                tGridB = Color.FromArgb(38, 0, 0, 0);
            }
            else
            {
                tBack = Color.FromArgb(22, 22, 28);
                tBorder = Color.FromArgb(58, 58, 70);
                tDivider = Color.FromArgb(48, 48, 58);
                tDim = Color.FromArgb(150, 150, 162);
                tSub = Color.FromArgb(168, 168, 180);
                tFaint = Color.FromArgb(110, 110, 122);
                tNeutral = Color.FromArgb(140, 140, 150);
                tGreen = Color.FromArgb(90, 200, 120);
                tAmber = Color.FromArgb(240, 200, 80);
                tRed = Color.FromArgb(240, 110, 90);
                tBlue = Color.FromArgb(110, 170, 245);
                tGridA = Color.FromArgb(45, 255, 255, 255);
                tGridB = Color.FromArgb(40, 255, 255, 255);
            }
            BackColor = tBack;
        }

        void SetThemeMode(int mode)
        {
            themeMode = mode;
            UpdateSystemTheme();
            ApplyTheme();
            SaveSettings();
            Invalidate();
            pollWake.Set(); // resample so accent colors re-derive from the new palette
        }

        // ---------- desktop pinning ----------

        // Win11 24H2 renders the wallpaper via DWM, so the classic SetParent(Progman) trick
        // paints behind the wallpaper. Instead: stay a normal top-level window that is
        // non-activating, absent from Alt-Tab, and glued to the bottom of the z-order.
        // One wrinkle: when Windows Spotlight is the wallpaper, its full-screen
        // CoreWindow sits near the bottom of the z-band, so plain HWND_BOTTOM would put
        // us underneath the wallpaper. zFloor tracks the window directly above that
        // layer; we pin ourselves there instead.
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const int WM_SETTINGCHANGE = 0x001A;
        const uint SWP_NOZORDER = 0x0004;

        IntPtr zFloor; // Handle == "already in the right slot"; Zero == plain HWND_BOTTOM

        void UpdateZFloor()
        {
            IntPtr spot = IntPtr.Zero, w = IntPtr.Zero;
            Rectangle vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            int vw = vs.Width * 9 / 10, vh = vs.Height * 9 / 10;
            while ((w = Native.FindWindowEx(IntPtr.Zero, w, "Windows.UI.Core.CoreWindow", null)) != IntPtr.Zero)
            {
                if (!Native.IsWindowVisible(w)) continue;
                int cloak;
                if (Native.DwmGetWindowAttribute(w, Native.DWMWA_CLOAKED, out cloak, 4) == 0 && cloak != 0) continue;
                Native.RECT r;
                if (!Native.GetWindowRect(w, out r)) continue;
                if (r.R - r.L >= vw && r.B - r.T >= vh) { spot = w; break; }
            }
            zFloor = spot == IntPtr.Zero ? IntPtr.Zero : Native.GetWindow(spot, Native.GW_HWNDPREV);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: no Alt-Tab / Task View entry
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE: never steals focus
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WINDOWPOSCHANGING && IsHandleCreated)
            {
                // Every reposition gets forced to the desktop floor (above the wallpaper
                // layer, below everything else).
                var wp = (Native.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(Native.WINDOWPOS));
                if (zFloor == Handle)
                {
                    wp.flags |= SWP_NOZORDER; // already in the right slot; block raises
                }
                else
                {
                    wp.hwndInsertAfter = zFloor != IntPtr.Zero ? zFloor : Native.HWND_BOTTOM;
                    wp.flags &= ~SWP_NOZORDER;
                }
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
            else if (m.Msg == WM_SETTINGCHANGE && themeMode == 0)
            {
                bool prev = systemLight;
                UpdateSystemTheme();
                if (systemLight != prev)
                {
                    ApplyTheme();
                    Invalidate();
                    pollWake.Set();
                }
            }
            base.WndProc(ref m);
        }

        void OnShownPin(object s, EventArgs e)
        {
            SinkToBottom();
        }

        void SinkToBottom()
        {
            UpdateZFloor();
            if (zFloor == Handle) return; // already directly above the wallpaper layer
            Native.SetWindowPos(Handle, zFloor != IntPtr.Zero ? zFloor : Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ---------- drag ----------

        void OnDragDown(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { dragging = true; dragStart = new Point(e.X, e.Y); }
        }

        void OnDragMove(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            Point p = PointToScreen(new Point(e.X, e.Y));
            Location = new Point(p.X - dragStart.X, p.Y - dragStart.Y);
        }

        void OnDragUp(object s, MouseEventArgs e)
        {
            if (dragging) { dragging = false; SaveSettings(); }
        }

        // ---------- menu ----------

        ToolStripMenuItem miViewCompact, miViewDay, miViewWeek;
        ToolStripMenuItem miThemeAuto, miThemeLight, miThemeDark;
        ToolStripMenuItem miAuto;

        void BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var miView = new ToolStripMenuItem("View");
            miViewCompact = new ToolStripMenuItem("Compact");
            miViewCompact.Click += delegate { SetView(0); };
            miViewDay = new ToolStripMenuItem("24-hour history");
            miViewDay.Click += delegate { SetView(1); };
            miViewWeek = new ToolStripMenuItem("7-day history");
            miViewWeek.Click += delegate { SetView(2); };
            miView.DropDownItems.Add(miViewCompact);
            miView.DropDownItems.Add(miViewDay);
            miView.DropDownItems.Add(miViewWeek);
            menu.Items.Add(miView);

            var miTheme = new ToolStripMenuItem("Theme");
            miThemeAuto = new ToolStripMenuItem("Follow Windows");
            miThemeAuto.Click += delegate { SetThemeMode(0); };
            miThemeLight = new ToolStripMenuItem("Light");
            miThemeLight.Click += delegate { SetThemeMode(1); };
            miThemeDark = new ToolStripMenuItem("Dark");
            miThemeDark.Click += delegate { SetThemeMode(2); };
            miTheme.DropDownItems.Add(miThemeAuto);
            miTheme.DropDownItems.Add(miThemeLight);
            miTheme.DropDownItems.Add(miThemeDark);
            menu.Items.Add(miTheme);

            var miRate = new ToolStripMenuItem("Set electricity rate...");
            miRate.Click += delegate { PromptRate(); };
            menu.Items.Add(miRate);

            var miHealth = new ToolStripMenuItem("Battery health...");
            miHealth.Click += delegate { ShowBatteryHealth(); };
            menu.Items.Add(miHealth);

            var miReset = new ToolStripMenuItem("Reset session avg/peak");
            miReset.Click += delegate { lock (sync) { sessSum = 0; sessCount = 0; sessPeak = 0; } };
            menu.Items.Add(miReset);

            var miCal = new ToolStripMenuItem("Reset AC calibration");
            miCal.Click += delegate { lock (sync) { baseOffset = 0; baseN = 0; } SaveSettings(); };
            menu.Items.Add(miCal);

            miAuto = new ToolStripMenuItem("Start with Windows");
            miAuto.Click += delegate { ToggleAutostart(); };
            menu.Items.Add(miAuto);

            if (!isAdmin)
            {
                var miAdmin = new ToolStripMenuItem("Restart as administrator (enables AC watts)");
                miAdmin.Click += delegate { RestartAsAdmin(); };
                menu.Items.Add(miAdmin);
            }

            menu.Items.Add(new ToolStripSeparator());
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            var miVer = new ToolStripMenuItem(string.Format("WattAWidget {0}.{1}.{2}", v.Major, v.Minor, v.Build));
            miVer.Enabled = false;
            menu.Items.Add(miVer);
            var miExit = new ToolStripMenuItem("Exit");
            miExit.Click += delegate { Close(); };
            menu.Items.Add(miExit);

            menu.Opening += delegate
            {
                miViewCompact.Checked = viewMode == 0;
                miViewDay.Checked = viewMode == 1;
                miViewWeek.Checked = viewMode == 2;
                miThemeAuto.Checked = themeMode == 0;
                miThemeLight.Checked = themeMode == 1;
                miThemeDark.Checked = themeMode == 2;
                miAuto.Checked = AutostartEnabled();
            };
            ContextMenuStrip = menu;
        }

        void SetView(int v)
        {
            viewMode = v;
            ApplySize();
            Invalidate();
            SaveSettings();
        }

        void CycleView()
        {
            SetView((viewMode + 1) % 3);
        }

        void ApplySize()
        {
            int h = viewMode > 0 ? HExpanded : HCompact;
            Size = new Size(W, h);
            var path = RoundRect(new Rectangle(0, 0, W, h), S(10));
            Region = new Region(path);
        }

        void PromptRate()
        {
            string cur = costRate > 0 ? costRate.ToString("0.##", CultureInfo.CurrentCulture) : "";
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Electricity cost per kWh (e.g. 8.50).\r\nLeave empty to hide the cost estimate.",
                "Electricity rate", cur, -1, -1);
            if (input == null) return;
            input = input.Trim();
            double v;
            if (input.Length == 0) { costRate = 0; }
            else if (double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ||
                     double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                if (v >= 0) costRate = v;
            }
            SaveSettings();
            Invalidate();
        }

        void ShowBatteryHealth()
        {
            string msg;
            try
            {
                double design = BatteryReader.SumWmi("BatteryStaticData", "DesignedCapacity");
                double full = BatteryReader.SumWmi("BatteryFullChargedCapacity", "FullChargedCapacity");
                double cycles = -1;
                try { cycles = BatteryReader.SumWmi("BatteryCycleCount", "CycleCount"); } catch { }

                if (design > 0 && full > 0)
                {
                    msg = string.Format(
                        "Design capacity:  {0:N0} mWh\r\nFull-charge now:  {1:N0} mWh\r\n\r\nBattery health:  {2:0}%",
                        design, full, 100.0 * full / design);
                    if (cycles > 0) msg += string.Format("\r\nCycle count:  {0:0}", cycles);
                    lock (sync)
                    {
                        if (baseN >= CalMinSamples)
                            msg += string.Format("\r\n\r\nAC base offset (learned):  +{0:0.0} W", baseOffset);
                    }
                }
                else msg = "Battery capacity telemetry unavailable.";
            }
            catch { msg = "Battery telemetry unavailable."; }
            MessageBox.Show(this, msg, "Battery health", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------- autostart ----------

        string StartupShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WattAWidget.lnk");
        }

        bool AutostartEnabled()
        {
            if (File.Exists(StartupShortcutPath())) return true;
            return RunSchtasks("/Query /TN WattAWidget") == 0;
        }

        void ToggleAutostart()
        {
            if (AutostartEnabled())
            {
                RunSchtasks("/Delete /TN WattAWidget /F");
                try { if (File.Exists(StartupShortcutPath())) File.Delete(StartupShortcutPath()); } catch { }
            }
            else
            {
                bool ok = false;
                if (isAdmin)
                {
                    // Elevated logon task: starts with admin rights, no UAC prompt.
                    string args = string.Format("/Create /F /TN WattAWidget /SC ONLOGON /RL HIGHEST /TR \"\\\"{0}\\\"\"", Application.ExecutablePath);
                    ok = RunSchtasks(args) == 0;
                }
                if (!ok) CreateStartupShortcut();
            }
        }

        void CreateStartupShortcut()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                dynamic lnk = shell.CreateShortcut(StartupShortcutPath());
                lnk.TargetPath = Application.ExecutablePath;
                lnk.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                lnk.Description = "WattAWidget";
                lnk.Save();
            }
            catch { }
        }

        static int RunSchtasks(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(5000)) return 1;
                    return p.ExitCode;
                }
            }
            catch { return 1; }
        }

        void RestartAsAdmin()
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, "--takeover");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try { Process.Start(psi); } catch { return; } // UAC declined
            Close();
        }

        // ---------- polling ----------

        void PollLoop()
        {
            lhm.Init(); // may take a moment; runs off the UI thread
            while (running)
            {
                try { SampleOnce(); } catch { }
                pollWake.WaitOne(2000);
            }
        }

        void SampleOnce()
        {
            DateTime now = DateTime.Now;
            BatteryState bat = BatteryReader.Read();
            PowerReading r = lhm.Read();

            double? consumption = null;
            bool isAc = !bat.Present || bat.PowerOnline;
            string mt, sb, bd;
            Color mc, bc;

            string pctTxt = bat.Percent >= 0 ? bat.Percent + "%" : "";
            bool cal;
            lock (sync) { cal = baseN >= CalMinSamples; }

            if (bat.Present && !bat.PowerOnline)
            {
                double w = bat.DischargeW;
                consumption = w;
                mt = string.Format("{0:0.0} W", w);
                mc = WattColor(w);
                bd = "BATTERY"; bc = tGreen;
                string eta = bat.HoursLeft > 0 ? string.Format(" | {0:0.0}h left", bat.HoursLeft) : "";
                double avg, peak;
                lock (sync)
                {
                    avg = sessCount > 0 ? sessSum / sessCount : w;
                    peak = Math.Max(sessPeak, w);
                    // calibrate the AC base offset: true total minus CPU+GPU estimate
                    if (r != null && w > 1)
                    {
                        double delta = w - r.Total;
                        if (delta > -5 && delta < 60)
                        {
                            double d = Math.Max(0, delta);
                            if (baseN == 0) baseOffset = d;
                            else baseOffset += 0.02 * (d - baseOffset);
                            baseN++;
                        }
                    }
                }
                sb = string.Format("avg {0:0.0} / peak {1:0.0} W | {2}{3}", avg, peak, pctTxt, eta);
            }
            else if (bat.Present) // plugged in
            {
                bd = "AC"; bc = tBlue;
                if (r != null)
                {
                    double sys = r.Total + (cal ? baseOffset : 0);
                    consumption = sys;
                    mt = string.Format("{0:0.0} W", sys);
                    mc = WattColor(sys);
                    if (bat.Charging)
                        sb = string.Format("wall ~{0:0} W | chg +{1:0.0} | {2}", sys + bat.ChargeW, bat.ChargeW, pctTxt);
                    else if (r.HasGpu && cal)
                        sb = string.Format("CPU {0:0} + GPU {1:0} + base {2:0} | {3}", r.Cpu, r.Gpu, baseOffset, pctTxt);
                    else if (r.HasGpu)
                        sb = string.Format("CPU {0:0} + GPU {1:0} W (est) | {2}", r.Cpu, r.Gpu, pctTxt);
                    else
                        sb = string.Format("{0} | {1}", cal ? "est. system draw" : "est. CPU draw", pctTxt);
                }
                else if (bat.Charging)
                {
                    mt = string.Format("+{0:0.0} W", bat.ChargeW);
                    mc = tGreen;
                    sb = string.Format("charging | {0} | {1}", pctTxt,
                        isAdmin ? "no power sensors" : "right-click for AC watts");
                }
                else
                {
                    mt = "AC";
                    mc = tNeutral;
                    sb = isAdmin
                        ? string.Format("no power sensors found | {0}", pctTxt)
                        : string.Format("right-click > restart as admin | {0}", pctTxt);
                }
            }
            else // no battery (desktop)
            {
                if (r != null)
                {
                    double sys = r.Total;
                    consumption = sys;
                    mt = string.Format("{0:0.0} W", sys);
                    mc = WattColor(sys);
                    bd = "AC"; bc = tBlue;
                    sb = r.HasGpu
                        ? string.Format("CPU {0:0} + GPU {1:0} W (est)", r.Cpu, r.Gpu)
                        : "est. CPU draw";
                }
                else
                {
                    mt = "--"; mc = tNeutral;
                    bd = ""; bc = tNeutral;
                    sb = isAdmin ? "no power telemetry" : "right-click > restart as admin";
                }
            }

            lock (sync)
            {
                mainText = mt; mainColor = mc; subText = sb; badgeText = bd; badgeColor = bc;

                if (consumption.HasValue)
                {
                    double w = consumption.Value;
                    sessSum += w; sessCount++;
                    if (w > sessPeak) sessPeak = w;

                    recent.Add(new PowerSample(now, w));
                    DateTime cutoff = now.AddMinutes(-(SparkMinutes + 5));
                    while (recent.Count > 0 && recent[0].Time < cutoff) recent.RemoveAt(0);

                    DateTime minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
                    if (minute != curMinute)
                    {
                        if (minCount > 0) CommitMinute(curMinute, minSum / minCount, minAc * 2 >= minCount);
                        curMinute = minute; minSum = 0; minCount = 0; minAc = 0;
                    }
                    minSum += w; minCount++;
                    if (isAc) minAc++;
                }
            }

            if (IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke((Action)UiTick); } catch { }
            }
        }

        int uiTicks;
        int lastSavedBaseN;

        void UiTick()
        {
            // Persist the continuously-learned calibration every ~5 min so a force-kill
            // or power loss doesn't discard it (SaveSettings otherwise only runs on UI
            // events and clean exit).
            if (++uiTicks % 150 == 0)
            {
                int bn;
                lock (sync) { bn = baseN; }
                if (bn != lastSavedBaseN)
                {
                    SaveSettings();
                    lastSavedBaseN = bn;
                }
            }

            // Win+D minimizes us along with everything else; quietly come back.
            if (WindowState == FormWindowState.Minimized)
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);

            // Keep our z-slot pinned above the (possibly moving) wallpaper layer.
            IntPtr prevFloor = zFloor;
            UpdateZFloor();
            if (zFloor != prevFloor && zFloor != Handle) SinkToBottom();

            // Follow the user across virtual desktops: if we're not on the current one,
            // move to whichever desktop hosts the foreground window.
            try
            {
                if (vdm != null)
                {
                    bool onCur;
                    if (vdm.IsWindowOnCurrentVirtualDesktop(Handle, out onCur) == 0 && !onCur)
                    {
                        IntPtr fg = Native.GetForegroundWindow();
                        Guid gid;
                        if (fg != IntPtr.Zero && vdm.GetWindowDesktopId(fg, out gid) == 0 && gid != Guid.Empty)
                        {
                            vdm.MoveWindowToDesktop(Handle, ref gid);
                            SinkToBottom();
                        }
                    }
                }
            }
            catch { }

            Invalidate();
        }

        // caller holds sync
        void CommitMinute(DateTime minute, double avg, bool srcAc)
        {
            DateTime hour = new DateTime(minute.Year, minute.Month, minute.Day, minute.Hour, 0, 0);
            double[] b;
            if (!hourly.TryGetValue(hour, out b)) { b = new double[4]; hourly[hour] = b; }
            b[0] += avg; b[1] += 1;
            if (srcAc) b[2] += 1; else b[3] += 1;

            double[] d;
            if (!daily.TryGetValue(minute.Date, out d)) { d = new double[2]; daily[minute.Date] = d; }
            d[0] += avg; d[1] += 1;

            // prune old buckets
            DateTime oldH = DateTime.Now.AddHours(-26);
            var dead = new List<DateTime>();
            foreach (DateTime k in hourly.Keys) if (k < oldH) dead.Add(k);
            foreach (DateTime k in dead) hourly.Remove(k);
            dead.Clear();
            DateTime oldD = DateTime.Today.AddDays(-8);
            foreach (DateTime k in daily.Keys) if (k < oldD) dead.Add(k);
            foreach (DateTime k in dead) daily.Remove(k);

            try
            {
                string file = Path.Combine(histDir, "watt-" + minute.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
                File.AppendAllText(file, minute.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    + "," + avg.ToString("0.0", CultureInfo.InvariantCulture)
                    + "," + (srcAc ? "A" : "B") + "\r\n");
            }
            catch { }
        }

        void LoadHistory()
        {
            // history files are tiny, but don't let them accumulate forever
            try
            {
                foreach (string f in Directory.GetFiles(histDir, "watt-*.csv"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)) File.Delete(f);
            }
            catch { }

            DateTime hourCutoff = DateTime.Now.AddHours(-25);
            for (int d = 7; d >= 0; d--)
            {
                string file = Path.Combine(histDir, "watt-" + DateTime.Now.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
                if (!File.Exists(file)) continue;
                try
                {
                    foreach (string line in File.ReadAllLines(file))
                    {
                        string[] parts = line.Split(',');
                        if (parts.Length < 2) continue;
                        DateTime t;
                        double v;
                        if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;

                        double[] db;
                        if (!daily.TryGetValue(t.Date, out db)) { db = new double[2]; daily[t.Date] = db; }
                        db[0] += v; db[1] += 1;

                        if (t < hourCutoff) continue;
                        DateTime hour = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0);
                        double[] b;
                        if (!hourly.TryGetValue(hour, out b)) { b = new double[4]; hourly[hour] = b; }
                        b[0] += v; b[1] += 1;
                        if (parts.Length >= 3)
                        {
                            if (parts[2] == "A") b[2] += 1;
                            else if (parts[2] == "B") b[3] += 1;
                        }
                    }
                }
                catch { }
            }
        }

        // ---------- settings ----------

        void LoadSettings()
        {
            int x = int.MinValue, y = 0;
            try
            {
                if (File.Exists(settingsFile))
                {
                    foreach (string line in File.ReadAllLines(settingsFile))
                    {
                        string[] kv = line.Split(new[] { '=' }, 2);
                        if (kv.Length != 2) continue;
                        if (kv[0] == "x") int.TryParse(kv[1], out x);
                        else if (kv[0] == "y") int.TryParse(kv[1], out y);
                        else if (kv[0] == "expanded") { if (kv[1] == "1") viewMode = 1; } // legacy
                        else if (kv[0] == "view") int.TryParse(kv[1], out viewMode);
                        else if (kv[0] == "theme")
                        {
                            if (kv[1] == "light") themeMode = 1;
                            else if (kv[1] == "dark") themeMode = 2;
                            else themeMode = 0;
                        }
                        else if (kv[0] == "offset") double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out baseOffset);
                        else if (kv[0] == "offsetn") int.TryParse(kv[1], out baseN);
                        else if (kv[0] == "rate") double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out costRate);
                    }
                }
            }
            catch { }
            if (viewMode < 0 || viewMode > 2) viewMode = 0;
            if (baseOffset < 0 || baseOffset > 60 || baseN < 0) { baseOffset = 0; baseN = 0; }

            Rectangle vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (x != int.MinValue && x > vs.Left - 100 && x < vs.Right - 40 && y > vs.Top - 40 && y < vs.Bottom - 40)
            {
                Location = new Point(x, y);
            }
            else
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - W - S(20), wa.Top + S(20));
            }
        }

        void SaveSettings()
        {
            try
            {
                string theme = themeMode == 1 ? "light" : themeMode == 2 ? "dark" : "auto";
                double off; int offn;
                lock (sync) { off = baseOffset; offn = baseN; }
                string content = string.Format(CultureInfo.InvariantCulture,
                    "x={0}\r\ny={1}\r\nview={2}\r\ntheme={3}\r\noffset={4:0.00}\r\noffsetn={5}\r\nrate={6:0.####}\r\n",
                    Location.X, Location.Y, viewMode, theme, off, offn, costRate);
                // write-then-replace so a crash mid-write can't corrupt the file
                string tmp = settingsFile + ".tmp";
                File.WriteAllText(tmp, content);
                if (File.Exists(settingsFile)) File.Replace(tmp, settingsFile, null);
                else File.Move(tmp, settingsFile);
            }
            catch { }
        }

        void OnClosingCleanup(object s, FormClosingEventArgs e)
        {
            running = false;
            pollWake.Set();
            try { if (pollThread != null && pollThread.IsAlive) pollThread.Join(1500); } catch { }
            lock (sync) { if (minCount > 0) { CommitMinute(curMinute, minSum / minCount, minAc * 2 >= minCount); minCount = 0; } }
            SaveSettings();
            lhm.Close();
        }

        // ---------- painting ----------

        Color WattColor(double w)
        {
            if (w < 15) return tGreen;
            if (w < 35) return tAmber;
            return tRed;
        }

        static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        string CostSuffix(double kwh)
        {
            if (costRate <= 0) return "";
            string sym = CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol;
            return string.Format(" · {0}{1:0.0}", sym, kwh * costRate);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            string mt, sb, bd;
            Color mc, bc;
            PowerSample[] pts;
            double[] avgs = null; bool[] acFlags = null;
            double[] weekWh = null;
            DateTime endHour = DateTime.MinValue, endDay = DateTime.MinValue;
            lock (sync)
            {
                mt = mainText; sb = subText; bd = badgeText;
                mc = mainColor; bc = badgeColor;
                pts = recent.ToArray();
                if (viewMode == 1) avgs = HourlyAvgsLocked(out endHour, out acFlags);
                else if (viewMode == 2) weekWh = WeeklyWhLocked(out endDay);
            }

            // border
            using (var pen = new Pen(tBorder))
            using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), S(10)))
                g.DrawPath(pen, path);

            var sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // caption + badge
            using (var br = new SolidBrush(tDim))
                g.DrawString("POWER", fCap, br, S(12), S(8));
            if (bd.Length > 0)
            {
                SizeF bsz = g.MeasureString(bd, fCap);
                float bx = W - S(12) - bsz.Width;
                using (var br = new SolidBrush(bc))
                {
                    g.FillEllipse(br, bx - S(9), S(11), S(5), S(5));
                    g.DrawString(bd, fCap, br, bx, S(8));
                }
            }

            // main number
            using (var br = new SolidBrush(mc))
                g.DrawString(mt, fMain, br, new Rectangle(0, S(16), W, S(44)), sfC);

            // sub line
            using (var br = new SolidBrush(tSub))
                g.DrawString(sb, fSub, br, new Rectangle(S(4), S(60), W - S(8), S(16)), sfC);

            // sparkline
            var sparkRect = new Rectangle(S(14), S(82), W - S(28), S(28));
            DrawSpark(g, sparkRect, pts, mc);

            if (viewMode > 0)
            {
                int top = HCompact;
                using (var pen = new Pen(tDivider))
                    g.DrawLine(pen, S(12), top, W - S(12), top);

                string title; string kwhTxt = "";
                if (viewMode == 1)
                {
                    title = "LAST 24 HOURS";
                    double kwh = 0; int nb = 0;
                    for (int i = 0; i < avgs.Length; i++)
                        if (!double.IsNaN(avgs[i])) { kwh += avgs[i]; nb++; }
                    kwh /= 1000.0;
                    if (nb > 0) kwhTxt = string.Format("~ {0:0.000} kWh{1}", kwh, CostSuffix(kwh));
                }
                else
                {
                    title = "LAST 7 DAYS";
                    double kwh = 0; int nb = 0;
                    for (int i = 0; i < weekWh.Length; i++)
                        if (!double.IsNaN(weekWh[i])) { kwh += weekWh[i]; nb++; }
                    kwh /= 1000.0;
                    if (nb > 0) kwhTxt = string.Format("~ {0:0.00} kWh{1}", kwh, CostSuffix(kwh));
                }

                using (var br = new SolidBrush(tDim))
                {
                    g.DrawString(title, fCap, br, S(12), top + S(6));
                    SizeF ks = g.MeasureString(kwhTxt, fTiny);
                    g.DrawString(kwhTxt, fTiny, br, W - S(12) - ks.Width, top + S(7));
                }

                var barsRect = new Rectangle(S(14), top + S(24), W - S(28), S(84));
                if (viewMode == 1) DrawBarsDay(g, barsRect, avgs, acFlags, endHour);
                else DrawBarsWeek(g, barsRect, weekWh, endDay);
            }

            sfC.Dispose();
        }

        // caller holds sync
        double[] HourlyAvgsLocked(out DateTime endHour, out bool[] acFlags)
        {
            DateTime now = DateTime.Now;
            endHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            var res = new double[24];
            acFlags = new bool[24];
            for (int i = 0; i < 24; i++)
            {
                DateTime h = endHour.AddHours(i - 23);
                double[] b;
                if (hourly.TryGetValue(h, out b) && b[1] > 0)
                {
                    res[i] = b[0] / b[1];
                    acFlags[i] = b[2] >= b[3] && (b[2] + b[3]) > 0;
                }
                else res[i] = double.NaN;
            }
            // fold live (uncommitted) minute into the current hour so the last bar is never empty
            if (minCount > 0)
            {
                double liveAvg = minSum / minCount;
                if (double.IsNaN(res[23]))
                {
                    res[23] = liveAvg;
                    acFlags[23] = minAc * 2 >= minCount;
                }
                else res[23] = (res[23] + liveAvg) / 2.0;
            }
            return res;
        }

        // caller holds sync
        double[] WeeklyWhLocked(out DateTime endDay)
        {
            endDay = DateTime.Today;
            var res = new double[7];
            for (int i = 0; i < 7; i++)
            {
                DateTime day = endDay.AddDays(i - 6);
                double[] d;
                res[i] = daily.TryGetValue(day, out d) && d[1] > 0 ? d[0] / 60.0 : double.NaN;
            }
            return res;
        }

        void DrawSpark(Graphics g, Rectangle r, PowerSample[] pts, Color accent)
        {
            DateTime now = DateTime.Now, t0 = now.AddMinutes(-SparkMinutes);
            double max = 10;
            foreach (var p in pts) if (p.Time >= t0 && p.Watts > max) max = p.Watts;
            max *= 1.15;

            using (var pen = new Pen(tGridA))
                g.DrawLine(pen, r.Left, r.Bottom, r.Right, r.Bottom);
            using (var br = new SolidBrush(tFaint))
                g.DrawString(SparkMinutes + " min", fTiny, br, r.Left, r.Bottom + S(1));

            // split into segments where sampling gaps exceed ~10s so the line doesn't
            // bridge periods with no data
            var segs = new List<List<PointF>>();
            List<PointF> cur = null;
            DateTime prevT = DateTime.MinValue;
            int total = 0;
            foreach (var p in pts)
            {
                if (p.Time < t0) continue;
                float x = r.Left + (float)((p.Time - t0).TotalSeconds / (SparkMinutes * 60.0)) * r.Width;
                float y = r.Bottom - (float)(p.Watts / max) * r.Height;
                if (cur == null || (p.Time - prevT).TotalSeconds > 10)
                {
                    cur = new List<PointF>();
                    segs.Add(cur);
                }
                cur.Add(new PointF(x, y));
                prevT = p.Time;
                total++;
            }
            if (total < 2)
            {
                using (var br = new SolidBrush(tFaint))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("collecting...", fTiny, br, r, sf);
                    sf.Dispose();
                }
                return;
            }

            PointF lastPt = PointF.Empty;
            foreach (var seg in segs)
            {
                if (seg.Count == 0) continue;
                lastPt = seg[seg.Count - 1];
                if (seg.Count < 2) continue;
                using (var line = new GraphicsPath())
                {
                    line.AddLines(seg.ToArray());
                    using (var fill = (GraphicsPath)line.Clone())
                    {
                        fill.AddLine(seg[seg.Count - 1], new PointF(seg[seg.Count - 1].X, r.Bottom));
                        fill.AddLine(new PointF(seg[seg.Count - 1].X, r.Bottom), new PointF(seg[0].X, r.Bottom));
                        using (var fb = new SolidBrush(Color.FromArgb(42, accent)))
                            g.FillPath(fb, fill);
                    }
                    using (var lp = new Pen(accent, 1.6f))
                        g.DrawPath(lp, line);
                }
            }
            using (var db = new SolidBrush(accent))
                g.FillEllipse(db, lastPt.X - S(2), lastPt.Y - S(2), S(4), S(4));
        }

        void DrawBarsDay(Graphics g, Rectangle r, double[] avgs, bool[] acFlags, DateTime endHour)
        {
            var plot = new Rectangle(r.Left, r.Top + S(12), r.Width, r.Height - S(26));

            double max = 10;
            foreach (var a in avgs) if (!double.IsNaN(a) && a > max) max = a;
            max = Math.Ceiling(max / 5.0) * 5.0;

            DrawGrid(g, r, plot, string.Format("{0:0} W", max));

            float slot = plot.Width / 24f;
            float bw = Math.Max(2f, slot * 0.62f);

            for (int i = 0; i < 24; i++)
            {
                DateTime hour = endHour.AddHours(i - 23);
                float cx = plot.Left + slot * i + slot / 2f;
                double a = avgs[i];

                if (double.IsNaN(a))
                {
                    using (var br = new SolidBrush(tGridA))
                        g.FillRectangle(br, cx - bw / 2f, plot.Bottom - 2, bw, 2);
                }
                else
                {
                    // AC hours use the badge blue; battery hours use the load color
                    Color c = acFlags[i] ? tBlue : WattColor(a);
                    FillBar(g, cx, bw, plot, (float)(a / max), c, i == 23);
                }

                if (hour.Hour % 6 == 0)
                    DrawTick(g, hour.ToString("HH"), cx, plot.Bottom);
            }
        }

        void DrawBarsWeek(Graphics g, Rectangle r, double[] wh, DateTime endDay)
        {
            var plot = new Rectangle(r.Left, r.Top + S(12), r.Width, r.Height - S(26));

            double max = 50;
            foreach (var a in wh) if (!double.IsNaN(a) && a > max) max = a;
            max = Math.Ceiling(max / 50.0) * 50.0;

            string maxLbl = max >= 1000 ? string.Format("{0:0.0} kWh", max / 1000.0) : string.Format("{0:0} Wh", max);
            DrawGrid(g, r, plot, maxLbl);

            float slot = plot.Width / 7f;
            float bw = Math.Max(4f, slot * 0.5f);

            for (int i = 0; i < 7; i++)
            {
                DateTime day = endDay.AddDays(i - 6);
                float cx = plot.Left + slot * i + slot / 2f;
                double a = wh[i];

                if (double.IsNaN(a))
                {
                    using (var br = new SolidBrush(tGridA))
                        g.FillRectangle(br, cx - bw / 2f, plot.Bottom - 2, bw, 2);
                }
                else
                {
                    FillBar(g, cx, bw, plot, (float)(a / max), tBlue, i == 6);
                }

                DrawTick(g, day.ToString("dd"), cx, plot.Bottom);
            }
        }

        void DrawGrid(Graphics g, Rectangle r, Rectangle plot, string maxLabel)
        {
            using (var pen = new Pen(tGridB) { DashStyle = DashStyle.Dot })
            {
                g.DrawLine(pen, plot.Left, plot.Top, plot.Right, plot.Top);
                g.DrawLine(pen, plot.Left, plot.Top + plot.Height / 2, plot.Right, plot.Top + plot.Height / 2);
            }
            using (var br = new SolidBrush(tFaint))
                g.DrawString(maxLabel, fTiny, br, plot.Left, r.Top - S(1));
            using (var pen = new Pen(tGridA))
                g.DrawLine(pen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        }

        void FillBar(Graphics g, float cx, float bw, Rectangle plot, float frac, Color c, bool current)
        {
            float h = Math.Max(2f, frac * plot.Height);
            float x = cx - bw / 2f;
            using (var br = new SolidBrush(Color.FromArgb(current ? 255 : 200, c)))
            {
                var rect = new RectangleF(x, plot.Bottom - h, bw, h);
                float rad = Math.Min(bw / 2f, S(2));
                using (var bp = new GraphicsPath())
                {
                    bp.AddArc(rect.X, rect.Y, rad * 2, rad * 2, 180, 90);
                    bp.AddArc(rect.Right - rad * 2, rect.Y, rad * 2, rad * 2, 270, 90);
                    bp.AddLine(rect.Right, rect.Bottom, rect.Left, rect.Bottom);
                    bp.CloseFigure();
                    g.FillPath(br, bp);
                }
            }
        }

        void DrawTick(Graphics g, string lbl, float cx, int bottom)
        {
            SizeF ls = g.MeasureString(lbl, fTiny);
            using (var br = new SolidBrush(tFaint))
                g.DrawString(lbl, fTiny, br, cx - ls.Width / 2f, bottom + S(2));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fMain.Dispose(); fSub.Dispose(); fTiny.Dispose(); fCap.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
