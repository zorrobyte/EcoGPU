using System;
using System.Diagnostics;
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
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 20000 };

        ToolStripMenuItem statusItem, powerItem, autoItem, releaseItem, startupItem, notifyItem;

        string gpuName = "Discrete GPU";
        int powerState = -1;    // D-state while enabled: 0 awake, 3 asleep
        GpuState state = GpuState.Unknown;
        bool working, suspended;

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

            watch.Tick += (s, e) => Watch();
            watch.Start();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            Log.Write($"EcoGPU started. AutoRelease={settings.AutoRelease}, AC={OnAC()}");
            EnsureEnabled();
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
                if (e.Mode == PowerModes.Suspend) suspended = true;
                else if (e.Mode == PowerModes.Resume) suspended = false;
                awakeSince = null; // start the awake timer fresh after any plug/unplug or wake
                UpdateMenu();
            }, null);
        }

        /// <summary>
        /// EcoGPU 1.0 could leave the GPU disabled. A disabled GPU has no driver to power
        /// its PCIe port down, so it's worse than an enabled idle one: turn it back on.
        /// </summary>
        void EnsureEnabled()
        {
            working = true;
            Task.Run(() =>
            {
                string toast = null;
                try
                {
                    var dgpu = GpuManager.FindDiscrete(GpuManager.GetDisplayDevices(), settings.DeviceInstanceId);
                    if (dgpu != null) gpuName = dgpu.Name;
                    if (GpuManager.StateOf(dgpu) == GpuState.Off)
                    {
                        Log.Write("GPU was disabled; enabling it");
                        var r = GpuManager.Enable(dgpu.InstanceId);
                        if (r.Success) GpuManager.EnsureNvidiaServices();
                        toast = r.Success
                            ? (r.RebootRequired ? r.Message : "Re-enabled the GPU. It powers itself off when idle, which saves more than disabling it.")
                            : "Couldn't enable the GPU. " + r.Message;
                    }
                }
                catch (Exception ex) { Log.Write("Startup check failed: " + ex); }

                ui.Post(_ =>
                {
                    working = false;
                    RefreshState();
                    UpdateMenu();
                    if (toast != null && settings.Notifications)
                        tray.ShowBalloonTip(5000, "EcoGPU", toast, ToolTipIcon.Info);
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
            autoItem = new ToolStripMenuItem("Free GPU from apps on battery", null, (s, e) =>
            {
                settings.AutoRelease = !settings.AutoRelease; settings.Save(); awakeSince = null; UpdateMenu();
            });
            releaseItem = new ToolStripMenuItem("Release GPU now", null, (s, e) => Release(auto: false));

            startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            notifyItem = new ToolStripMenuItem("Show notifications", null, (s, e) =>
            {
                settings.Notifications = !settings.Notifications; settings.Save(); UpdateMenu();
            });
            var options = new ToolStripMenuItem("Options");
            options.DropDownItems.AddRange(new ToolStripItem[]
            {
                startupItem, notifyItem, new ToolStripSeparator(),
                new ToolStripMenuItem("Open log", null, (s, e) => OpenFile(Log.FilePath)),
                new ToolStripMenuItem("Open settings folder", null, (s, e) => { settings.Save(); OpenFile(Settings.Dir); }),
            });

            menu.Items.AddRange(new ToolStripItem[]
            {
                statusItem, powerItem, new ToolStripSeparator(),
                autoItem, releaseItem, new ToolStripSeparator(),
                options,
                new ToolStripMenuItem("About EcoGPU", null, (s, e) => OpenFile(RepoUrl)),
                new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()),
            });
            menu.Opening += (s, e) => { startupItem.Checked = Startup.IsEnabled(); RefreshState(); UpdateMenu(); };
            return menu;
        }

        /// <summary>Re-read the GPU's state and whether it's awake. Doesn't wake the GPU.</summary>
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
            if (working) stateText = "releasing…";
            statusItem.Text = (state == GpuState.NotFound ? "Discrete GPU" : gpuName) + ": " + stateText;
            powerItem.Text = OnAC() ? "Plugged in" : "On battery";

            autoItem.Checked = settings.AutoRelease;
            releaseItem.Enabled = state == GpuState.On && !working;
            notifyItem.Checked = settings.Notifications;

            bool asleep = state == GpuState.Off || (state == GpuState.On && powerState == 3);
            tray.Icon = Icons.Get(working ? GpuState.Unknown : asleep ? GpuState.Off : state);
            string tip = "EcoGPU: GPU " + (asleep ? "asleep" : state == GpuState.On ? "awake" : stateText);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void ToggleStartup()
        {
            bool ok = Startup.IsEnabled() ? Startup.Disable() : Startup.Enable();
            if (!ok) tray.ShowBalloonTip(4000, "EcoGPU", "Couldn't update the startup task.", ToolTipIcon.Warning);
            startupItem.Checked = Startup.IsEnabled();
        }

        /// <summary>
        /// On battery: if the GPU has been awake for a while without real work, apps are just
        /// holding it (browsers, Discord, overlays...). Restart it so they let go and NVIDIA's
        /// runtime power management can switch it fully off (D3cold).
        /// </summary>
        void Watch()
        {
            if (suspended || working) return;
            if (!settings.AutoRelease || OnAC())
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
            Log.Write("EcoGPU exited");
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }
}
