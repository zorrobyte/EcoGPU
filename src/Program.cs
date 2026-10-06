using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace EcoGPU
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--write-icon")
            {
                Icons.WriteIco(args[1]);
                return;
            }
            if (args.Length == 1 && args[0] == "--enable-startup") { Environment.Exit(Startup.Enable() ? 0 : 1); }
            if (args.Length == 1 && args[0] == "--disable-startup") { Environment.Exit(Startup.Disable() ? 0 : 1); }

            using (var mutex = new Mutex(true, "Global\\EcoGPU-SingleInstance", out bool first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);
                Application.Run(new TrayApp());
            }
        }
    }

    /// <summary>Tray icons drawn at runtime: a chip, filled when the GPU is on, hollow with a green leaf when off.</summary>
    static class Icons
    {
        static readonly Color OnColor = Color.FromArgb(255, 176, 32);
        static readonly Color OffColor = Color.FromArgb(150, 150, 150);
        static readonly Color Leaf = Color.FromArgb(60, 200, 90);
        static readonly Color Bad = Color.FromArgb(230, 70, 60);

        static readonly System.Collections.Generic.Dictionary<GpuState, Icon> Cache = new System.Collections.Generic.Dictionary<GpuState, Icon>();

        public static Icon Get(GpuState state)
        {
            if (Cache.TryGetValue(state, out var icon)) return icon;
            int size = SystemInformation.SmallIconSize.Width;
            using (var bmp = Draw(state, Math.Max(16, size)))
                icon = Icon.FromHandle(bmp.GetHicon());
            Cache[state] = icon;
            return icon;
        }

        static Bitmap Draw(GpuState state, int s)
        {
            var bmp = new Bitmap(s, s);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float u = s / 16f;
                Color c = state == GpuState.On ? OnColor : OffColor;
                if (state == GpuState.Unknown) c = Color.FromArgb(200, 200, 200);

                // pins
                using (var pin = new Pen(c, 1.2f * u))
                    for (int i = 0; i < 3; i++)
                    {
                        float p = (4.5f + i * 3.5f) * u;
                        g.DrawLine(pin, p, 0.5f * u, p, 3 * u);
                        g.DrawLine(pin, p, 13 * u, p, 15.5f * u);
                        g.DrawLine(pin, 0.5f * u, p, 3 * u, p);
                        g.DrawLine(pin, 13 * u, p, 15.5f * u, p);
                    }
                var body = new RectangleF(3 * u, 3 * u, 10 * u, 10 * u);
                using (var path = Rounded(body, 1.8f * u))
                {
                    if (state == GpuState.On)
                        using (var b = new SolidBrush(c)) g.FillPath(b, path);
                    else
                        using (var p = new Pen(c, 1.5f * u)) g.DrawPath(p, path);
                }

                if (state == GpuState.Off)
                {
                    // leaf
                    using (var leaf = new GraphicsPath())
                    {
                        leaf.AddBezier(5 * u, 11 * u, 5 * u, 6 * u, 8 * u, 5 * u, 11.5f * u, 4.5f * u);
                        leaf.AddBezier(11.5f * u, 4.5f * u, 11.5f * u, 9 * u, 9 * u, 11.5f * u, 5 * u, 11 * u);
                        using (var b = new SolidBrush(Leaf)) g.FillPath(b, leaf);
                    }
                }
                else if (state == GpuState.Error || state == GpuState.NotFound)
                {
                    using (var b = new SolidBrush(Bad)) g.FillEllipse(b, 8 * u, 8 * u, 7.5f * u, 7.5f * u);
                }
            }
            return bmp;
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Writes a multi-size .ico (PNG entries) for the exe icon.</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Draw(GpuState.Off, sizes[i]))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs[i] = ms.ToArray();
                }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var png in pngs) w.Write(png);
            }
        }
    }
}
