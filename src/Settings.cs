using System;
using System.Collections.Generic;
using System.IO;

namespace EcoGPU
{
    public enum Mode
    {
        Optimized, // off on battery, on when plugged in
        Standard,  // always on
        Eco,       // always off
    }

    public class Settings
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EcoGPU");
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");

        public Mode Mode = Mode.Optimized;
        public bool RestoreOnExit = true;
        public bool Notifications = true;
        public int SwitchDelaySeconds = 5;
        public string DeviceInstanceId = "";

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                if (values.TryGetValue("Mode", out var m) && Enum.TryParse(m, true, out Mode mode)) s.Mode = mode;
                if (values.TryGetValue("RestoreOnExit", out var r) && bool.TryParse(r, out var rb)) s.RestoreOnExit = rb;
                if (values.TryGetValue("Notifications", out var n) && bool.TryParse(n, out var nb)) s.Notifications = nb;
                if (values.TryGetValue("SwitchDelaySeconds", out var d) && int.TryParse(d, out var di)) s.SwitchDelaySeconds = Math.Max(0, di);
                if (values.TryGetValue("DeviceInstanceId", out var id)) s.DeviceInstanceId = id;
            }
            catch (Exception ex)
            {
                Log.Write("Failed to load settings: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[]
                {
                    "Mode=" + Mode,
                    "RestoreOnExit=" + RestoreOnExit,
                    "Notifications=" + Notifications,
                    "SwitchDelaySeconds=" + SwitchDelaySeconds,
                    "; Leave empty to auto-detect the discrete GPU",
                    "DeviceInstanceId=" + DeviceInstanceId,
                });
            }
            catch (Exception ex)
            {
                Log.Write("Failed to save settings: " + ex.Message);
            }
        }
    }

    public static class Log
    {
        public static readonly string FilePath = Path.Combine(Settings.Dir, "EcoGPU.log");
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
