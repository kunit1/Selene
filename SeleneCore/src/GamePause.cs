    //  - frame watchdog: logs when the home animates below 45 fps (with the cause candidates), and restarts Playnite when
    //    WPF has fallen back to software rendering (happens after sleep / display switches; it never recovers by itself)
    public static class Smoothness
    {
        [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

        private static double lastFrame, sum;
        private static int frames, slowWindows;
        private static System.Windows.Threading.DispatcherTimer tierTimer;
        private static int softwareSeconds;
        private static bool restarting;

        public static void Start()
        {
            try
            {
                var pt = new PowerThrottling { Version = 1, ControlMask = 1 | 4, StateMask = 0 };  // execution speed + timer resolution: never throttled
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref pt, Marshal.SizeOf(typeof(PowerThrottling)));
                D3DKMTSetProcessSchedulingPriorityClass(GetCurrentProcess(), 3);  // above normal
            }
            catch (Exception ex) { HomeNav.Log("smooth: setup failed " + ex.Message); }
            HomeNav.Log("smooth: render tier " + Tier());
            System.Windows.Media.RenderCapability.TierChanged += (s, e) => HomeNav.Log("smooth: render tier changed -> " + Tier());
            System.Windows.Media.CompositionTarget.Rendering += OnFrame;
            tierTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tierTimer.Tick += (s, e) => CheckSoftware();
            tierTimer.Start();
        }

        private static int Tier() { return System.Windows.Media.RenderCapability.Tier >> 16; }  // 2 = full GPU, 0 = software

        private static bool GameRunning()
        {
            var api = HomeNav.Api;
            if (api != null) foreach (var g in api.Database.Games) if (g.IsRunning) return true;
            return false;
        }

        private static bool PlayniteOnScreen()
        {
            var w = System.Windows.Application.Current != null ? System.Windows.Application.Current.MainWindow : null;
            return w != null && w.IsVisible && w.WindowState != System.Windows.WindowState.Minimized && !CloseToTray.IsHidden;
        }

        private static void OnFrame(object sender, EventArgs e)
        {
            if (!PlayniteOnScreen()) { lastFrame = 0; return; }
            var re = e as System.Windows.Media.RenderingEventArgs;
            if (re == null) return;
            double t = re.RenderingTime.TotalMilliseconds;
            if (t == lastFrame) return;  // same frame reported twice
            double d = t - lastFrame;
            lastFrame = t;
            if (d > 250 || d <= 0) { frames = 0; sum = 0; return; }  // nothing was animating for a while
            frames++; sum += d;
            if (sum < 4000) return;
            double fps = frames * 1000.0 / sum;
            frames = 0; sum = 0;
            if (fps >= 45 || !PlayniteOnScreen()) { slowWindows = 0; return; }
            if (++slowWindows > 3) return;  // reported enough for this slow spell
            string game = "none";
            var api = HomeNav.Api;
            if (api != null) foreach (var g in api.Database.Games) if (g.IsRunning) { game = g.Name; break; }
            double cpu = 0;
            try { var me = System.Diagnostics.Process.GetCurrentProcess(); cpu = me.TotalProcessorTime.TotalSeconds; } catch { }
            HomeNav.Log("smooth: SLOW " + fps.ToString("0") + " fps on the " + (HomeNav.FindFocusedName() ?? "?") + " | render tier " + Tier()
                + " | game running: " + game + " | screen " + System.Windows.SystemParameters.PrimaryScreenWidth + " | playnite cpu " + cpu.ToString("0") + " s");
        }

        private static void CheckSoftware()
        {
            if (restarting) return;
            if (Tier() > 0 || !PlayniteOnScreen() || GameRunning()) { softwareSeconds = 0; return; }
            if (++softwareSeconds < 8) return;
            restarting = true;
            // the restart is the optional helper's job: without one, said once and left (it was tried, and logged, every few seconds)
            if (string.IsNullOrEmpty(SelenePaths.Helper) || !System.IO.File.Exists(SelenePaths.Helper)) { HomeNav.Log("smooth: Playnite is drawing without the GPU (software rendering); no helper to restart it"); return; }
            HomeNav.Log("smooth: Playnite is drawing without the GPU (software rendering) -> restarting it");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SelenePaths.Helper, "restart-playnite") { UseShellExecute = false }); }
            catch (Exception ex) { HomeNav.Log("smooth: restart failed " + ex.Message); restarting = false; }
        }
    }
