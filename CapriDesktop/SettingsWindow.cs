using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CapriDesktop;

/// <summary>Her settings, as the game's Capri tab has them: her zap sounds, how often she zaps on her own, her form
/// (and how long each, switching on her own), her face's size; and the desktop's own: idles on her own and how often,
/// glitches. Changes apply at once (and are saved); `changed` tells her window.</summary>
public sealed class SettingsWindow : Window
{
    static readonly Brush Back = new SolidColorBrush(Color.FromRgb(16, 20, 24));
    static readonly Brush Text = new SolidColorBrush(Color.FromRgb(232, 255, 246));
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(150, 176, 166));
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(3, 249, 148));

    readonly Settings _s;
    readonly Action _changed;
    readonly StackPanel _body = new() { Margin = new Thickness(18, 14, 18, 16) };

    public SettingsWindow(Settings settings, Action changed)
    {
        (_s, _changed) = (settings, changed);
        Title = "Capri settings";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Background = Back;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };

        Heading("Sounds");
        Slider("Her zap and glitch sounds", 0, 100, _s.SfxVolume * 100, "%", v => _s.SfxVolume = v / 100);

        Heading("Zipping about");
        Check("She zips around on her own", _s.ZipOnHerOwn, v => _s.ZipOnHerOwn = v);
        Range("Her zaps on her own (seconds)", 3, 120, () => (_s.ZipMin, _s.ZipMax), (a, b) => (_s.ZipMin, _s.ZipMax) = (a, b));
        Hint("Each time she picks a random time between the two. Only her full body zips about; her face HUD stays where you put it.");

        Heading("Her form");
        var forms = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        FrameworkElement? formTimes = null;
        foreach (var (name, form) in new[] { ("Full body", "body"), ("Face HUD", "face"), ("She switches on her own", "auto") })
        {
            var r = new RadioButton
            {
                Content = name, GroupName = "form", IsChecked = _s.Form == form, Foreground = Text,
                Margin = new Thickness(0, 0, 14, 4),
            };
            r.Checked += (_, _) =>
            {
                _s.Form = form;
                if (formTimes is not null) formTimes.Visibility = form == "auto" ? Visibility.Visible : Visibility.Collapsed;
                Save();
            };
            forms.Children.Add(r);
        }
        _body.Children.Add(forms);
        formTimes = Range("Each form for (seconds)", 10, 600, () => (_s.FormMin, _s.FormMax), (a, b) => (_s.FormMin, _s.FormMax) = (a, b));
        formTimes.Visibility = _s.Form == "auto" ? Visibility.Visible : Visibility.Collapsed;
        Slider("Face HUD size", 140, 1200, _s.FaceWidth, " px", v => _s.FaceWidth = v);
        Check("Face HUD: green panel behind her eyes", _s.FacePanel, v => _s.FacePanel = v);

        Heading("Standing about");
        Check("She changes idles on her own", _s.IdlesOnHerOwn, v => _s.IdlesOnHerOwn = v);
        Range("Quiet, a new idle every (seconds)", 5, 300, () => (_s.IdleMin, _s.IdleMax), (a, b) => (_s.IdleMin, _s.IdleMax) = (a, b));
        Check("Glitches", _s.Glitches, v => _s.Glitches = v);
    }

    void Save()
    {
        _s.Save();
        _changed();
    }

    void Heading(string text) => _body.Children.Add(new TextBlock
    {
        Text = text, Foreground = Green, FontSize = 15, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, _body.Children.Count == 0 ? 0 : 14, 0, 6),
    });

    void Hint(string text) => _body.Children.Add(new TextBlock
    {
        Text = text, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
    });

    void Check(string text, bool on, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = on, Foreground = Text, Margin = new Thickness(0, 2, 0, 6) };
        c.Click += (_, _) => { set(c.IsChecked == true); Save(); };
        _body.Children.Add(c);
    }

    (Slider, TextBlock) MakeSlider(double min, double max, double value)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 250, VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { Foreground = Text, Width = 70, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        return (s, t);
    }

    void Slider(string label, double min, double max, double value, string unit, Action<double> set)
    {
        _body.Children.Add(new TextBlock { Text = label, Foreground = Text, Margin = new Thickness(0, 2, 0, 2) });
        var (s, t) = MakeSlider(min, max, value);
        void Show() => t.Text = $"{s.Value:0}{unit}";
        Show();
        s.ValueChanged += (_, _) => Show();
        // saved when you let go, not on every step of the drag
        s.AddHandler(Thumb.DragCompletedEvent, new RoutedEventHandler((_, _) => { set(s.Value); Save(); }));
        s.PreviewMouseUp += (_, _) => { set(s.Value); Save(); };
        _body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { s, t }, Margin = new Thickness(0, 0, 0, 6) });
    }

    /// <summary>An { at least, at most } pair, as the game's tab has them: moving one past the other takes it along.</summary>
    FrameworkElement Range(string label, double min, double max, Func<(double, double)> get, Action<double, double> set)
    {
        var box = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        box.Children.Add(new TextBlock { Text = label, Foreground = Text, Margin = new Thickness(0, 0, 0, 2) });
        var (lo, hi) = get();
        var (a, at) = MakeSlider(min, max, lo);
        var (b, bt) = MakeSlider(min, max, hi);
        var busy = false;
        void Show()
        {
            at.Text = $"at least {a.Value:0} s";
            bt.Text = $"at most {b.Value:0} s";
        }
        Show();
        at.Width = bt.Width = 110;
        a.ValueChanged += (_, _) =>
        {
            if (busy) return;
            busy = true;
            if (b.Value < a.Value) b.Value = a.Value;
            busy = false;
            Show();
        };
        b.ValueChanged += (_, _) =>
        {
            if (busy) return;
            busy = true;
            if (a.Value > b.Value) a.Value = b.Value;
            busy = false;
            Show();
        };
        void Commit() { set(Math.Round(a.Value), Math.Round(b.Value)); Save(); }
        foreach (var s in new[] { a, b })
        {
            s.AddHandler(Thumb.DragCompletedEvent, new RoutedEventHandler((_, _) => Commit()));
            s.PreviewMouseUp += (_, _) => Commit();
        }
        box.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { a, at } });
        box.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { b, bt } });
        _body.Children.Add(box);
        return box;
    }
}
