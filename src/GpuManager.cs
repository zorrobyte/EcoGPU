using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EcoGPU
{
    public enum GpuState { Unknown, On, Off, Error, NotFound }

    public class DisplayDevice
    {
        public string InstanceId;
        public string Name;
        public int ErrorCode; // ConfigManagerErrorCode: 0 = working, 22 = disabled
        public bool Present;

        public string Vendor
        {
            get
            {
                var id = InstanceId.ToUpperInvariant();
                if (id.Contains("VEN_10DE")) return "NVIDIA";
                if (id.Contains("VEN_8086")) return "Intel";
                if (id.Contains("VEN_1002") || id.Contains("VEN_1022")) return "AMD";
                return "Other";
            }
        }
    }

    public class SwitchResult
    {
        public bool Success;
        public bool RebootRequired;
        public string Message;
    }

    public static class GpuManager
    {
        public const int CodeDisabled = 22;

        public static List<DisplayDevice> GetDisplayDevices()
        {
            var list = new List<DisplayDevice>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID, Name, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE PNPClass = 'Display'"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    var id = mo["PNPDeviceID"] as string;
                    if (string.IsNullOrEmpty(id) || !id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new DisplayDevice
                    {
                        InstanceId = id,
                        Name = (mo["Name"] as string) ?? id,
                        ErrorCode = mo["ConfigManagerErrorCode"] == null ? 0 : Convert.ToInt32(mo["ConfigManagerErrorCode"]),
                        Present = mo["Present"] == null || (bool)mo["Present"],
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// The discrete GPU: the configured device if set, otherwise the NVIDIA adapter
        /// (or, on all-AMD laptops, the second AMD adapter).
        /// </summary>
        public static DisplayDevice FindDiscrete(List<DisplayDevice> devices, string overrideId)
        {
            if (!string.IsNullOrEmpty(overrideId))
                return devices.FirstOrDefault(d => string.Equals(d.InstanceId, overrideId, StringComparison.OrdinalIgnoreCase));

            var nvidia = devices.FirstOrDefault(d => d.Vendor == "NVIDIA" && d.Present);
            if (nvidia != null) return nvidia;

            var amd = devices.Where(d => d.Vendor == "AMD" && d.Present).ToList();
            if (amd.Count >= 2 && devices.All(d => d.Vendor != "Intel"))
                return amd.FirstOrDefault(d => d.Name.IndexOf("RX", StringComparison.OrdinalIgnoreCase) >= 0) ?? amd.Last();
            return null;
        }

        /// <summary>A working integrated GPU that can keep driving the screen.</summary>
        public static DisplayDevice FindWorkingIntegrated(List<DisplayDevice> devices, DisplayDevice discrete)
        {
            return devices.FirstOrDefault(d => d.Present && d.ErrorCode == 0 &&
                (discrete == null || !string.Equals(d.InstanceId, discrete.InstanceId, StringComparison.OrdinalIgnoreCase)) &&
                (d.Vendor == "Intel" || d.Vendor == "AMD"));
        }

        public static GpuState StateOf(DisplayDevice d)
        {
            if (d == null) return GpuState.NotFound;
            if (d.ErrorCode == 0) return GpuState.On;
            if (d.ErrorCode == CodeDisabled) return GpuState.Off;
            return GpuState.Error;
        }

        public static SwitchResult Disable(string instanceId) => RunPnpUtil("/disable-device", instanceId, CodeDisabled);

        public static SwitchResult Enable(string instanceId) => RunPnpUtil("/enable-device", instanceId, 0);

        /// <summary>
        /// The device's current D-state (0 = awake, 3 = asleep), or -1 if unknown.
        /// Reading this doesn't wake the GPU, unlike asking the driver (nvidia-smi).
        /// </summary>
        public static int GetPowerState(string instanceId)
        {
            try
            {
                string escaped = instanceId.Replace("\\", "\\\\").Replace("'", "\\'");
                using (var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID = '{escaped}'"))
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var inParams = mo.GetMethodParameters("GetDeviceProperties");
                        inParams["devicePropertyKeys"] = new[] { "DEVPKEY_Device_PowerData" };
                        var result = mo.InvokeMethod("GetDeviceProperties", inParams, null);
                        var props = result?["deviceProperties"] as ManagementBaseObject[];
                        var data = props?.FirstOrDefault()?["Data"] as byte[];
                        // CM_POWER_DATA: ULONG PD_Size; DEVICE_POWER_STATE PD_MostRecentPowerState (1 = D0 … 4 = D3)
                        if (data != null && data.Length >= 8) return BitConverter.ToInt32(data, 4) - 1;
                    }
            }
            catch (Exception ex) { Log.Write("Power state read failed: " + ex.Message); }
            return -1;
        }

        public static SwitchResult Restart(string instanceId) => RunPnpUtil("/restart-device", instanceId, 0);

        // ---- Safety checks (same idea as G-Helper / Legion Toolkit) ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

        const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

        /// <summary>
        /// True if a screen is being driven by the discrete GPU (e.g. an external monitor
        /// on a port wired to the dGPU, or the laptop is in dGPU-only/MUX mode).
        /// Turning the GPU off would black out that screen.
        /// </summary>
        public static bool IsDisplayOnDevice(DisplayDevice device)
        {
            if (device == null) return false;
            // "PCI\VEN_10DE&DEV_2C58&SUBSYS_30101A58&REV_A1" — EnumDisplayDevices reports the hardware id without the instance suffix.
            string hwPrefix = device.InstanceId.Split('\\').Take(2).Aggregate((a, b) => a + "\\" + b);
            for (int i = 0; ; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
                if (dd.DeviceID != null && dd.DeviceID.StartsWith(hwPrefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static readonly HashSet<string> SystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dwm", "csrss", "winlogon", "services", "lsass", "smss", "wininit", "svchost", "fontdrvhost",
            "nvcontainer", "nvdisplay.container", "nvsettings", "nvsphelper64", "nvwmi64", "nvcplui",
            "explorer", "taskhostw", "sihost", "runtimebroker", "shellexperiencehost", "searchhost",
            "startmenuexperiencehost", "textinputhost", "applicationframehost", "systemsettings",
            "dllhost", "conhost", "ctfmon", "EcoGPU",
        };

        static string NvidiaSmi(string args)
        {
            string exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (!System.IO.File.Exists(exe)) return null;
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode == 0 ? output : null;
                }
            }
            catch { return null; }
        }

        /// <summary>GPU load in percent, or -1 if unknown (non-NVIDIA or nvidia-smi missing).</summary>
        public static int GetNvidiaUtilization()
        {
            var output = NvidiaSmi("--query-gpu=utilization.gpu --format=csv,noheader,nounits");
            if (output != null && int.TryParse(output.Split('\n')[0].Trim(), out int util)) return util;
            return -1;
        }

        /// <summary>User apps that currently hold a context on the NVIDIA GPU.</summary>
        public static List<string> GetNvidiaApps()
        {
            var names = new List<string>();
            var output = NvidiaSmi("");
            if (output == null) return names;
            // |    0   N/A  N/A           12345    C+G   ...\Discord\app-1.0\Discord.exe      N/A      |
            var rx = new Regex(@"^\|\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(?:C\+G|C|G)\s+", RegexOptions.Multiline);
            foreach (Match m in rx.Matches(output))
            {
                try
                {
                    var name = Process.GetProcessById(int.Parse(m.Groups[1].Value)).ProcessName;
                    if (!SystemProcesses.Contains(name) && !names.Contains(name)) names.Add(name);
                }
                catch { }
            }
            return names;
        }

        /// <summary>Restart NVIDIA's container service after re-enabling (G-Helper does the same; NVIDIA Control Panel/App can break otherwise).</summary>
        public static void EnsureNvidiaServices()
        {
            foreach (var name in new[] { "NVDisplay.ContainerLocalSystem" })
            {
                try
                {
                    using (var sc = new System.ServiceProcess.ServiceController(name))
                    {
                        if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                        {
                            sc.Start();
                            Log.Write("Started service " + name);
                        }
                    }
                }
                catch (InvalidOperationException) { } // service not installed
                catch (Exception ex) { Log.Write("Service " + name + ": " + ex.Message); }
            }
        }

        static SwitchResult RunPnpUtil(string verb, string instanceId, int expectedCode)
        {
            var psi = new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"),
                Arguments = verb + " \"" + instanceId + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string output;
            int exit;
            using (var p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { }
                    return new SwitchResult { Message = "pnputil timed out" };
                }
                exit = p.ExitCode;
            }
            Log.Write($"pnputil {verb} exit={exit}: {output.Trim().Replace(Environment.NewLine, " | ")}");

            // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED
            if (exit == 3010)
                return new SwitchResult { Success = true, RebootRequired = true, Message = "Restart Windows to finish switching." };

            var dev = GetDisplayDevices().FirstOrDefault(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
            int code = dev?.ErrorCode ?? -1;
            if (code == expectedCode)
                return new SwitchResult { Success = true };
            return new SwitchResult { Message = $"Switch not confirmed (pnputil exit {exit}, device code {code})." };
        }
    }
}
