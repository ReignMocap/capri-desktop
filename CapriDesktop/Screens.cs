using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace CapriDesktop;

/// <summary>The usable area (inside the taskbar) of each screen, in WPF's units.</summary>
public static class Screens
{
    public static List<Rect> WorkAreas(Visual forDpi)
    {
        var list = new List<Rect>();
        var toDip = PresentationSource.FromVisual(forDpi)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var w = info.Work;
                var a = toDip.Transform(new Point(w.Left, w.Top));
                var b = toDip.Transform(new Point(w.Right, w.Bottom));
                list.Add(new Rect(a, b));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The work area of the screen this point is on (or the nearest one).</summary>
    public static Rect At(Visual forDpi, Point p)
    {
        var all = WorkAreas(forDpi);
        if (all.Count == 0) return SystemParameters.WorkArea;
        foreach (var r in all)
            if (r.Contains(p)) return r;
        return all.OrderBy(r => Distance(r, p)).First();
    }

    static double Distance(Rect r, Point p)
    {
        var dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
        var dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
        return dx * dx + dy * dy;
    }

    [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public RectI Monitor; public RectI Work; public uint Flags; }
    delegate bool MonitorProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
