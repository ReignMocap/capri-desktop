using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CapriDesktop;

/// <summary>Her lightning from where she was to where she lands when she zips (the game's trail, in her pink): a
/// see-through, click-through window over the screen between the two for half a second, the bolt re-jagged every few
/// hundredths of a second and fading.</summary>
public static class Bolt
{
    const double Seconds = 0.5, Margin = 60;

    public static void Strike(Point from, Point to)
    {
        var left = Math.Min(from.X, to.X) - Margin;
        var top = Math.Min(from.Y, to.Y) - Margin;
        var w = Math.Abs(to.X - from.X) + 2 * Margin;
        var h = Math.Abs(to.Y - from.Y) + 2 * Margin;
        var a = new Point(from.X - left, from.Y - top);
        var b = new Point(to.X - left, to.Y - top);

        var glow = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(255, 60, 170)), StrokeThickness = 7,
            StrokeLineJoin = PenLineJoin.Round, Opacity = 0.75,
            Effect = new BlurEffect { Radius = 10 },
        };
        var core = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(255, 215, 245)), StrokeThickness = 2.2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new System.Windows.Controls.Canvas { Width = w, Height = h };
        canvas.Children.Add(glow);
        canvas.Children.Add(core);
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Left = left, Top = top, Width = w, Height = h, Content = canvas,
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80 | 0x08000000);   // click-through, tool, no-activate
        };
        var rng = new Random();
        void Jag()
        {
            var pts = new PointCollection();
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var (nx, ny) = (-dy / len, dx / len);
            var steps = Math.Clamp((int)(len / 28), 6, 60);
            for (var i = 0; i <= steps; i++)
            {
                var t = (double)i / steps;
                var off = i == 0 || i == steps ? 0 : (rng.NextDouble() * 2 - 1) * Math.Min(26, len * 0.08);
                pts.Add(new Point(a.X + dx * t + nx * off, a.Y + dy * t + ny * off));
            }
            glow.Points = pts;
            core.Points = pts;
        }
        Jag();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - started).TotalSeconds;
            if (t >= Seconds) { timer.Stop(); win.Close(); return; }
            Jag();
            canvas.Opacity = t < Seconds * 0.5 ? 1 : 1 - (t - Seconds * 0.5) / (Seconds * 0.5);
        };
        win.Show();
        timer.Start();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
