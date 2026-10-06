using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EcoGPU
{
    public class TrayApp : ApplicationContext
    {
        const string RepoUrl = "https://github.com/zorrobyte/EcoGPU";
        const int BusyThresholdPercent = 10;

        readonly Settings settings = Settings.Load();
        readonly NotifyIcon tray;
        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer retry = new System.Windows.Forms.Timer { Interval = 30000 };
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 20000 };

        ToolStripMenuItem statusItem, powerItem, optimizedItem, standardItem, ecoItem, forceOffItem, releaseItem;
        ToolStripMenuItem startupItem, restoreItem, notifyItem;

        string gpuName = "Discrete GPU";
        int powerState = -1;    // D-state while enabled: 0 awake, 3 asleep
        GpuState state = GpuState.Unknown;
        bool working, rerun, suspended;
        bool deferred;          // wanted Off but held back (busy / display attached)
        string deferNotified;   // reason already shown, so we don't repeat the toast every retry

        // Auto-release (Optimized mode on battery)
        DateTime? awakeSince;               // when we first saw the GPU awake
        DateTime lastRelease = DateTime.MinValue;
        DateTime nextReleaseAllowed = DateTime.MinValue;
        int releaseStreak;                  // releases in a row where something grabbed the GPU again soon after
        string holders = "";                // apps seen on the GPU at the last release
        bool stuckNotified;

        public TrayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            tray = new NotifyIcon { Visible = true, Text = "EcoGPU", Icon = Icons.Get(GpuState.Unknown) };
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(tray, null);
            };

            debounce.Tick += (s, e) => { debounce.Stop(); Apply("power change"); };
            retry.Tick += (s, e) => { if (deferred) Apply("retry"); else retry.Stop(); };
            watch.Tick += (s, e) => Watch();
            watch.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            Log.Write($"EcoGPU started. Mode={settings.Mode}, AC={OnAC()}");
            Apply("startup");
        }

        static bool OnAC()
        {
            var line = SystemInformation.PowerStatus.PowerLineStatus;
            return line != PowerLineStatus.Offline; // Unknown (no battery) counts as plugged in
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            ui.Post(_ =>
            {
                switch (e.Mode)
                {
                    case PowerModes.Suspend:
                        suspended = true;
                        debounce.Stop();
                        break;
                    case PowerModes.Resume:
                        suspended = false;
                        ScheduleApply(Math.Max(5, settings.SwitchDelaySeconds));
                        break;
                    case PowerModes.StatusChange:
                        UpdateMenu();
                        deferNotified = null;
                        ScheduleApply(settings.SwitchDelaySeconds);
                        break;
                }
            }, null);
        }

        void ScheduleApply(int seconds)
        {
            debounce.Stop();
            debounce.Interval = Math.Max(1, seconds) * 1000;
            debounce.Start();
        }

        GpuState Desired()
        {
            switch (settings.Mode)
            {
                case Mode.Standard: return GpuState.On;
                case Mode.Eco: return GpuState.Off;
                default: return GpuState.On; // Optimized: stays enabled; NVIDIA powers it off when idle and Watch() frees it from apps
            }
        }

        /// <summary>Bring the GPU into the state the current mode asks for.</summary>
        void Apply(string reason, bool force = false)
        {
            if (suspended) return;
            if (working) { rerun = true; return; }
            working = true;
            var desired = Desired();
            UpdateMenu();

            Task.Run(() =>
            {
                string toast = null, toastTitle = "EcoGPU";
                bool warn = false, nowDeferred = false;
                try
                {
                    var devices = GpuManager.GetDisplayDevices();
                    var dgpu = GpuManager.FindDiscrete(devices, settings.DeviceInstanceId);
                    var current = GpuManager.StateOf(dgpu);
                    if (dgpu != null) gpuName = dgpu.Name;
                    Log.Write($"Apply ({reason}): mode={settings.Mode} ac={OnAC()} gpu={current} want={desired}{(force ? " force" : "")}");

                    if (dgpu == null)
                    {
                        state = GpuState.NotFound;
                    }
                    else if (desired == GpuState.On && current != GpuState.On)
                    {
                        var r = GpuManager.Enable(dgpu.InstanceId);
                        if (r.Success) GpuManager.EnsureNvidiaServices();
                        toast = r.Success ? (r.RebootRequired ? r.Message : "GPU turned on.") : "Couldn't turn the GPU on. " + r.Message;
                        warn = !r.Success || r.RebootRequired;
                    }
                    else if (desired == GpuState.Off && current == GpuState.On)
                    {
                        string holdReason = null;
                        if (GpuManager.FindWorkingIntegrated(devices, dgpu) == null)
                            holdReason = "No working integrated GPU found (is the laptop in dGPU-only / MUX mode?). Staying on.";
                        else if (GpuManager.IsDisplayOnDevice(dgpu))
                            holdReason = "A display is connected to the discrete GPU. Staying on until it's unplugged.";
                        else if (!force && IsBusy())
                        {
                            var apps = GpuManager.GetNvidiaApps();
                            holdReason = "The GPU is busy" + (apps.Count > 0 ? " (" + string.Join(", ", apps.Take(4)) + ")" : "") +
                                ". It will turn off once it's idle, or choose \"Turn off now\".";
                        }

                        if (holdReason != null)
                        {
                            nowDeferred = true;
                            if (deferNotified != holdReason) { toast = holdReason; warn = true; deferNotified = holdReason; }
                            Log.Write("Held: " + holdReason);
                        }
                        else
                        {
                            var apps = GpuManager.GetNvidiaApps();
                            var r = GpuManager.Disable(dgpu.InstanceId);
                            if (r.Success && dgpu.Vendor == "NVIDIA") GpuManager.RestartNvidiaDisplayService();
                            toast = r.Success
                                ? (r.RebootRequired ? r.Message : "GPU turned off to save battery." +
                                   (apps.Count > 0 ? " Apps moved off it: " + string.Join(", ", apps.Take(5)) + "." : ""))
                                : "Couldn't turn the GPU off. " + r.Message;
                            warn = !r.Success || r.RebootRequired;
                            deferNotified = null;
                        }
                    }

                    if (dgpu != null)
                    {
                        var after = GpuManager.FindDiscrete(GpuManager.GetDisplayDevices(), settings.DeviceInstanceId);
                        state = GpuManager.StateOf(after);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Apply failed: " + ex);
                    toast = "Error: " + ex.Message;
                    warn = true;
                }

                ui.Post(_ =>
                {
                    working = false;
                    deferred = nowDeferred;
                    if (deferred) retry.Start(); else retry.Stop();
                    UpdateMenu();
                    if (toast != null && settings.Notifications)
                        tray.ShowBalloonTip(5000, toastTitle, toast, warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    if (rerun) { rerun = false; Apply("queued"); }
                }, null);
            });
        }

        /// <summary>G-Helper's rule: over 10% load, checked twice a second apart.</summary>
        static bool IsBusy()
        {
            int u = GpuManager.GetNvidiaUtilization();
            if (u <= BusyThresholdPercent) return false;
            Thread.Sleep(1000);
            return GpuManager.GetNvidiaUtilization() > BusyThresholdPercent;
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem { Enabled = false };
            powerItem = new ToolStripMenuItem { Enabled = false };
            optimizedItem = new ToolStripMenuItem("Optimized: sleeps when idle, freed from apps on battery", null, (s, e) => SetMode(Mode.Optimized));
            standardItem = new ToolStripMenuItem("Standard: always on", null, (s, e) => SetMode(Mode.Standard));
            ecoItem = new ToolStripMenuItem("Eco: disabled", null, (s, e) => SetMode(Mode.Eco));
            forceOffItem = new ToolStripMenuItem("Turn off now (ignore busy check)", null, (s, e) => { deferNotified = null; Apply("forced", force: true); });
            releaseItem = new ToolStripMenuItem("Release GPU now (make apps let go)", null, (s, e) => Release(auto: false));

            startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            restoreItem = new ToolStripMenuItem("Turn GPU back on when exiting", null, (s, e) =>
            {
                settings.RestoreOnExit = !settings.RestoreOnExit; settings.Save(); UpdateMenu();
            });
            notifyItem = new ToolStripMenuItem("Show notifications", null, (s, e) =>
            {
                settings.Notifications = !settings.Notifications; settings.Save(); UpdateMenu();
            });
            var options = new ToolStripMenuItem("Options");
            options.DropDownItems.AddRange(new ToolStripItem[]
            {
                startupItem, restoreItem, notifyItem, new ToolStripSeparator(),
                new ToolStripMenuItem("Open log", null, (s, e) => OpenFile(Log.FilePath)),
                new ToolStripMenuItem("Open settings folder", null, (s, e) => { settings.Save(); OpenFile(Settings.Dir); }),
            });

            menu.Items.AddRange(new ToolStripItem[]
            {
                statusItem, powerItem, new ToolStripSeparator(),
                optimizedItem, standardItem, ecoItem, new ToolStripSeparator(),
                forceOffItem, releaseItem, new ToolStripSeparator(),
                options,
                new ToolStripMenuItem("About EcoGPU", null, (s, e) => OpenFile(RepoUrl)),
                new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()),
            });
            menu.Opening += (s, e) => { startupItem.Checked = Startup.IsEnabled(); RefreshState(); UpdateMenu(); };
            return menu;
        }

        /// <summary>Re-read the GPU's state (it may have been changed in Device Manager) and whether it's awake.</summary>
        void RefreshState()
        {
            if (working) return;
            try
            {
                var dgpu = GpuManager.FindDiscrete(GpuManager.GetDisplayDevices(), settings.DeviceInstanceId);
                state = GpuManager.StateOf(dgpu);
                powerState = state == GpuState.On ? GpuManager.GetPowerState(dgpu.InstanceId) : -1;
            }
            catch (Exception ex) { Log.Write("Refresh failed: " + ex.Message); }
        }

        void UpdateMenu()
        {
            string stateText;
            switch (state)
            {
                case GpuState.On:
                    stateText = powerState == 0 ? "awake" + (holders != "" ? " (" + holders + ")" : "") : powerState == 3 ? "asleep, powered off" : "on";
                    break;
                case GpuState.Off: stateText = "disabled"; break;
                case GpuState.NotFound: stateText = "not found"; break;
                case GpuState.Error: stateText = "error, see log"; break;
                default: stateText = "checking…"; break;
            }
            if (working) stateText = "switching…";
            statusItem.Text = (state == GpuState.NotFound ? "Discrete GPU" : gpuName) + ": " + stateText;
            powerItem.Text = OnAC() ? "Plugged in" : "On battery";

            optimizedItem.Checked = settings.Mode == Mode.Optimized;
            standardItem.Checked = settings.Mode == Mode.Standard;
            ecoItem.Checked = settings.Mode == Mode.Eco;
            forceOffItem.Visible = deferred;
            releaseItem.Enabled = state == GpuState.On && !working;
            restoreItem.Checked = settings.RestoreOnExit;
            notifyItem.Checked = settings.Notifications;

            // Enabled but powered off by the driver saves as much as disabled, so it gets the leaf too.
            tray.Icon = Icons.Get(working ? GpuState.Unknown : state == GpuState.On && powerState == 3 ? GpuState.Off : state);
            string tip = "EcoGPU: GPU " + (state == GpuState.Off ? "off" : state == GpuState.On ? "on" : stateText) + " · " + settings.Mode;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void SetMode(Mode mode)
        {
            settings.Mode = mode;
            settings.Save();
            deferNotified = null;
            Apply("mode changed");
        }

        void ToggleStartup()
        {
            bool ok = Startup.IsEnabled() ? Startup.Disable() : Startup.Enable();
            if (!ok) tray.ShowBalloonTip(4000, "EcoGPU", "Couldn't update the startup task.", ToolTipIcon.Warning);
            startupItem.Checked = Startup.IsEnabled();
        }

        /// <summary>
        /// Optimized mode on battery: if the GPU has been awake for a while without real work,
        /// apps are just holding it (browsers, Discord, overlays...). Restart it so they let go and
        /// NVIDIA's runtime power management can switch it fully off (D3cold).
        /// Disabling the device instead leaves its PCIe port powered, so this saves more.
        /// </summary>
        void Watch()
        {
            if (suspended || working) return;
            if (settings.Mode != Mode.Optimized || !settings.AutoRelease || OnAC())
            {
                awakeSince = null;
                releaseStreak = 0;
                stuckNotified = false;
                return;
            }

            RefreshState();
            UpdateMenu();
            if (state != GpuState.On || powerState != 0)
            {
                awakeSince = null;
                // Stayed asleep well past the last release: whatever held it is gone.
                if (releaseStreak > 0 && DateTime.Now - lastRelease > TimeSpan.FromMinutes(10)) { releaseStreak = 0; stuckNotified = false; }
                return;
            }

            if (awakeSince == null) awakeSince = DateTime.Now;
            if (DateTime.Now - awakeSince.Value < TimeSpan.FromSeconds(settings.AwakeGraceSeconds)) return;
            if (DateTime.Now < nextReleaseAllowed) return;

            Release(auto: true);
        }

        /// <summary>Restart the GPU so apps let go of it (Legion Toolkit's "Deactivate GPU").</summary>
        void Release(bool auto)
        {
            if (working) return;
            working = true;
            UpdateMenu();
            Task.Run(() =>
            {
                string msg = null;
                bool released = false, busy = false;
                try
                {
                    var dgpu = GpuManager.FindDiscrete(GpuManager.GetDisplayDevices(), settings.DeviceInstanceId);
                    if (dgpu == null) msg = auto ? null : "Discrete GPU not found.";
                    else if (GpuManager.IsDisplayOnDevice(dgpu)) msg = auto ? null : "A display is connected to the discrete GPU, so it can't be released.";
                    else if (auto && IsBusy())
                    {
                        busy = true; // a game or render job: leave it alone
                        Log.Write("Auto-release skipped: GPU busy");
                    }
                    else
                    {
                        var apps = GpuManager.GetNvidiaApps();
                        holders = string.Join(", ", apps.Take(5));
                        var r = GpuManager.Restart(dgpu.InstanceId);
                        released = r.Success;
                        Log.Write($"{(auto ? "Auto-release" : "Release")}: {(r.Success ? "ok" : r.Message)}; apps on GPU: {(holders == "" ? "none listed" : holders)}");
                        if (!auto)
                            msg = r.Success ? "GPU released. It powers off once nothing uses it." : "Release failed. " + r.Message;
                    }
                }
                catch (Exception ex) { Log.Write("Release failed: " + ex); msg = auto ? null : "Error: " + ex.Message; }

                ui.Post(_ =>
                {
                    working = false;
                    awakeSince = null;
                    if (released)
                    {
                        // Grabbed again within 5 minutes of the last release: back off so we don't keep restarting it.
                        releaseStreak = DateTime.Now - lastRelease < TimeSpan.FromMinutes(5) ? releaseStreak + 1 : 0;
                        lastRelease = DateTime.Now;
                        int backoff = Math.Min(60 * (1 << Math.Min(releaseStreak, 4)), 15 * 60);
                        nextReleaseAllowed = DateTime.Now.AddSeconds(auto ? backoff : 0);
                        if (auto && releaseStreak >= 2 && !stuckNotified)
                        {
                            stuckNotified = true;
                            msg = "Something keeps waking the GPU" + (holders != "" ? ": " + holders : "") +
                                  ". Close it, or set it to \"Power saving\" in Settings > System > Display > Graphics.";
                        }
                    }
                    else if (busy)
                    {
                        nextReleaseAllowed = DateTime.Now.AddSeconds(60);
                    }
                    UpdateMenu();
                    if (msg != null && settings.Notifications)
                        tray.ShowBalloonTip(5000, "EcoGPU", msg, auto ? ToolTipIcon.Warning : ToolTipIcon.Info);
                }, null);
            });
        }

        static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        void ExitApp()
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            watch.Stop();
            debounce.Stop();
            retry.Stop();
            if (settings.RestoreOnExit)
            {
                try
                {
                    var dgpu = GpuManager.FindDiscrete(GpuManager.GetDisplayDevices(), settings.DeviceInstanceId);
                    if (GpuManager.StateOf(dgpu) == GpuState.Off)
                    {
                        Log.Write("Exit: turning GPU back on");
                        if (GpuManager.Enable(dgpu.InstanceId).Success) GpuManager.EnsureNvidiaServices();
                    }
                }
                catch (Exception ex) { Log.Write("Restore on exit failed: " + ex.Message); }
            }
            Log.Write("EcoGPU exited");
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }
}
