using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CapriDesktop;

/// <summary>Capri on the desktop. Her full self (breathing, blinking, glitching, talking in pictures picked for each
/// sentence) or her face HUD (eyes on their green panel, mouth under them); she zips about the screen in a zap with a
/// bolt of lightning, and switches between her forms the same way. Click her to type to her through Voxta; she answers
/// out loud, her mouth moving with her voice, her words in a speech bubble. Mouse wheel over her: bigger or smaller.
/// The window is see-through: Windows sends clicks on its empty pixels to whatever is behind her.</summary>
public partial class MainWindow : Window
{
    // Breathing, as in the game (capri.lua): the frames go round in BreathLoop seconds, each fading into the next.
    const double BreathLoop = 4.8;
    // Blinking, as in the game: every 2.5-6 s, eyes off for 0.12 s, sometimes a second blink 0.09 s later.
    const double BlinkMin = 2.5, BlinkMax = 6.0, BlinkHold = 0.12, DoubleGap = 0.09, DoubleChance = 0.2;
    // As in the game: her mouth-open picture shows while the jaw is over this; after her last sentence she holds her
    // pose this long before settling; after you speak she thinks for up to ThinkFor seconds.
    const double JawOpen = 0.2, BackToIdle = 1.2, ThinkFor = 8;
    const double MinReadSeconds = 3, ReadCharsPerSecond = 25;
    // on her own: a zip somewhere else, and (Look: switch on her own) a change of form, every so often while quiet
    (double, double) ZipEvery => (_settings.ZipMin, _settings.ZipMax);
    (double, double) FormEvery => (_settings.FormMin, _settings.FormMax);
    const double OtherScreenChance = 0.15, ZipAtLeast = 250;

    static readonly (string Name, double Body, double Face)[] Sizes =
        [("Small", 300, 240), ("Medium", 420, 340), ("Large", 560, 480)];
    static readonly (string Name, double Volume)[] Volumes = [("25%", 0.25), ("50%", 0.5), ("75%", 0.75), ("100%", 1.0)];

    readonly Settings _settings = Settings.Load();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };
    readonly Random _rng = new();
    readonly VoxtaLink _voxta;
    readonly Speaker _speaker;
    readonly Sfx _sfx;
    List<Pose> _poses = [];
    PosePicker _picker = null!;
    BodyFx _fx = null!;
    FaceHud _face = null!;
    string _form = "body";        // the form on screen: "body" or "face"
    Pose? _shown;                 // the picture on screen (her full self)
    Pose? _linePose, _flash;      // the picture for the sentence she's saying, and the flash before it
    double _flashUntil, _backToIdleAt, _thinkingUntil;
    double _breathStart, _nextBlink, _blinkEnd, _secondBlink = -1;
    double _zipAt = -1, _formAt = -1;

    // a zap: out of one form, moved (and/or switched), into the other
    sealed class Zap
    {
        public required double At { get; init; }
        public required string From { get; init; }
        public required string To { get; init; }
        public Point? Feet { get; init; }         // where she lands (bottom middle), or null: where she is
        public double? Height { get; init; }      // the size she lands at (her full self), or null: as she was
        public bool Applied { get; set; }
        public double InAt { get; set; }
    }
    Zap? _zap;

    // her words in the bubble: what she's said so far in this reply, and how much of the sentence now is showing
    readonly StringBuilder _said = new();
    int _lineFrom;
    double _bubbleHideAt = -1, _mouthFlipAt;
    bool _mouthOpen;
    string _status = "";

    public MainWindow()
    {
        InitializeComponent();
        if (Demo)
        {
            Settings.ReadOnly = true;
            (_settings.Form, _settings.Height, _settings.FaceWidth, _settings.ZipOnHerOwn) = ("body", 420, 340, false);
            (_settings.FeetX, _settings.FeetY, _settings.FaceX, _settings.FaceY) = (null, null, null, null);
        }
        _voxta = new VoxtaLink(_settings);
        _speaker = new Speaker(_settings);
        _sfx = new Sfx(_settings);
        try { _poses = Art.Load(_settings.ArtFolder); }
        catch (Exception e)
        {
            MessageBox.Show($"Couldn't load Capri's art from {_settings.ArtFolder}:\n{e.Message}", "Capri");
        }
        if (!_poses.Any(p => p.IsIdle))
        {
            MessageBox.Show($"No art found in {_settings.ArtFolder}. Run tools\\prep_art.py first.", "Capri");
            Close();
            return;
        }
        _picker = new PosePicker(_poses)
        {
            ChangesOnHerOwn = _settings.IdlesOnHerOwn, QuietFor = (_settings.IdleMin, _settings.IdleMax),
        };
        _picker.Family = (Idles.FirstOrDefault(p => p.Name == _settings.Pose) ?? Idles.First()).Family;
        _fx = new BodyFx(Stage, Body, Strips, GhostCyan, GhostPink, ZapLine);
        _face = new FaceHud(_settings.ArtFolder, FaceCanvas) { ShowPanel = _settings.FacePanel };
        Stage.Height = _settings.Height;
        FaceBox.Width = _settings.FaceWidth;
        Holder.Opacity = Math.Clamp(_settings.Opacity, 0.15, 1);
        if (_settings.Form == "face" && _face.Loaded) ShowForm("face");
        _breathStart = Now;
        ScheduleBlink(Now);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        BuildMenu();
        MouseLeftButtonDown += OnLeftDown;
        MouseWheel += OnWheel;
        Input.KeyDown += OnInputKey;
        Loaded += (_, _) =>
        {
            PlaceAtStart();
            if (_form == "face") _face.SwitchOn(Now);
            if (Demo) RunDemo();
            else WireVoxta();
        };
    }

    // ---- a test run: python-free, mouse-free check of her zips and forms (CapriDesktop.exe --demo) ----

    static bool Demo => Environment.GetCommandLineArgs().Contains("--demo");

    async void RunDemo()
    {
        Log.Write("demo: start");
        if (Environment.GetCommandLineArgs().Contains("--settings"))
        {
            OpenSettings();
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            // (a picture of it, for checking: settings-demo.png beside the log)
            if (_settingsWindow?.Content is FrameworkElement page)
            {
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)page.ActualWidth, (int)page.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(page);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var file = System.IO.File.Create(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapriDesktop", "settings-demo.png"));
                png.Save(file);
            }
            Close();
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--poses")) { await PosesDemo(); Close(); return; }
        if (Environment.GetCommandLineArgs().Contains("--sizes"))
        {
            // five zips, each to a random size (60-520 px tall here)
            (_settings.RandomSize, _settings.SizeMin, _settings.SizeMax) = (true, 60, 520);
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                ZipSomewhere();
                await Task.Delay(TimeSpan.FromSeconds(1.2));
                Log.Write($"demo: landed {Stage.ActualHeight:0} px tall, {ActualWidth:0} x {ActualHeight:0} window, feet {FeetNow().X:0},{FeetNow().Y:0}");
            }
            Close();
            return;
        }
        var work = SystemParameters.WorkArea;
        SetFeet(new Point(work.Left + 300, work.Bottom));
        Remember();
        async Task Wait(double s) => await Task.Delay(TimeSpan.FromSeconds(s));
        void Where(string what) => Log.Write($"demo: {what}: {_form} at {FeetNow().X:0},{FeetNow().Y:0}");
        await Wait(2);
        StartZap("body", new Point(work.Left + 900, work.Bottom));
        await Wait(2);
        Where("body zipped");
        _settings.Form = "face";      // as the Look menu does
        await Wait(2);
        Where("face first time");
        SetFeet(new Point(work.Left + 450, work.Top + 500));   // as dragging her does
        Remember();
        Where("face put here");
        ZipSomewhere();               // (her face: nothing)
        await Wait(1.5);
        Where("face after a zip asked for");
        _settings.Form = "body";
        await Wait(2);
        Where("body back");
        ZipSomewhere();
        await Wait(2);
        Where("body zipped again");
        _settings.Form = "face";
        await Wait(2);
        Where("face back");
        Log.Write("demo: done");
        Close();
    }

    /// <summary>--demo --poses: which pictures loaded, which her rules pick for a few sentences and quiet stretches,
    /// each one put on screen and saved as a picture (poses-demo-*.png beside the log).</summary>
    async Task PosesDemo()
    {
        var kinds = _poses.GroupBy(p => p.Name.Split('_')[0]).Select(g => $"{g.Key} {g.Count()}");
        Log.Write("demo: loaded " + string.Join(", ", kinds));
        var picks = new List<(string Why, Pose Pose)>();
        foreach (var (text, affect) in new[] { ("Hi there, choom!", (string?)null), ("Get lost.", "angry"),
                     ("Ha, you wish.", "laughing"), ("Look over there!", null), ("Nice one.", null),
                     ("Oh. Um.", "embarrassed"), ("Come here, you.", "flirty") })
        {
            var (pose, flash) = _picker.ForLine(text, affect, null);
            picks.Add(($"\"{text}\" ({affect ?? "-"})", flash ?? pose));
        }
        if (_picker.Think(null) is { } think) picks.Add(("thinking", think));
        // calm sentences and quiet stretches, many times over: how often she's turned away
        var turned = new Dictionary<string, int>();
        for (var i = 0; i < 400; i++)
        {
            var p = _picker.ForLine("Sure.", null, null).Pose;
            var k = p.Name.Split('_')[0];
            turned[k] = turned.GetValueOrDefault(k) + 1;
        }
        Log.Write("demo: 400 calm sentences: " + string.Join(", ", turned.Select(t => $"{t.Key} {t.Value}")));
        _picker.QuietFor = (0, 0);
        Pose? back = null;
        for (var i = 0; i < 300 && back is null; i++)
        {
            var q = _picker.Quiet(i * 2.0);
            if (q.Name.StartsWith("back_") || q.Name.StartsWith("side_")) back = q;
        }
        if (back is not null) picks.Add(("quiet, turned away", back));
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapriDesktop");
        var n = 0;
        foreach (var (why, pose) in picks)
        {
            Log.Write($"demo: {why} -> {pose.Name}");
            _linePose = pose;
            _flash = null;
            _backToIdleAt = Now + 10;
            await Task.Delay(400);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Stage.ActualWidth, (int)Stage.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(Stage);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var file = System.IO.File.Create(System.IO.Path.Combine(dir, $"poses-demo-{n++}.png"));
            png.Save(file);
        }
    }

    double Now => _clock.Elapsed.TotalSeconds;
    IEnumerable<Pose> Idles => _poses.Where(p => p.IsIdle);
    bool Thinking => Now < _thinkingUntil && _speaker.Current is null;
    bool Quiet => !_speaker.Busy && !Thinking && InputBox.Visibility != Visibility.Visible && _zap is null;

    // ---- Voxta ----

    void WireVoxta()
    {
        void Ui(Action a) => Dispatcher.BeginInvoke(a);
        _voxta.Status += s => Ui(() => { _status = s; ShowStatus(); });
        _voxta.ReplyStarted += _ => Ui(() => { _said.Clear(); _lineFrom = 0; });
        _voxta.ReplyChunk += (line, url) => Ui(() => _speaker.Add(line, url));
        _voxta.ReplyEnded += id => Ui(() => _speaker.ReplyEnded(id));
        _voxta.Interrupted += () => Ui(_speaker.Interrupt);
        _voxta.Heard += (text, done) => Ui(() => OnHeard(text, done));
        _speaker.LineStarted += OnLineStarted;
        _speaker.LineEnded += _ => _backToIdleAt = Now + BackToIdle;
        _speaker.MessageFinished += id =>
        {
            _ = _voxta.Finished(id);
            _bubbleHideAt = Now + Math.Max(MinReadSeconds, _said.Length / ReadCharsPerSecond);
        };
        _ = _voxta.StartAsync();
    }

    void OnLineStarted(Line line)
    {
        _ = _voxta.Playing(line);
        _thinkingUntil = 0;
        _bubbleHideAt = -1;
        var (pose, flash) = _picker.ForLine(line.Text, line.Affect, _linePose);
        Log.Write($"  in {pose.Name}{(flash is null ? "" : " after a flash of " + flash.Name)}");
        if (pose.IsIdle || pose.Name.StartsWith("talk_")) _picker.Family = pose.Family;
        _linePose = pose;
        _flash = flash;
        _flashUntil = flash is null ? 0 : Now + PosePicker.FlashSeconds;
        if (_said.Length > 0) _said.Append(' ');
        _lineFrom = _said.Length;
        _said.Append(line.Text.Trim());
        Bubble.Visibility = Visibility.Visible;
    }

    void OpenInput()
    {
        InputBox.Visibility = Visibility.Visible;
        ShowStatus();
        Activate();
        Input.Focus();
    }

    void CloseInput()
    {
        InputBox.Visibility = Visibility.Collapsed;
        ShowStatus();
    }

    void ShowStatus()
    {
        StatusText.Text = _status;
        // problems always show; "connecting…" only while the type box is open
        var problem = _status.StartsWith("Can't") || _status.StartsWith("No ") || _status.StartsWith("Voxta:");
        StatusText.Visibility = _status != "" && (problem || InputBox.Visibility == Visibility.Visible)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseInput(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = Input.Text.Trim();
        if (text == "") return;
        if (!_voxta.IsReady) { _status = "Not connected to Voxta yet."; ShowStatus(); return; }
        Input.Clear();
        _speaker.Interrupt();      // you talked over her
        _ = _voxta.Say(text);
        _thinkingUntil = Now + ThinkFor;
        _said.Clear();
        BubbleText.Text = "…";
        Bubble.Visibility = Visibility.Visible;
        _bubbleHideAt = -1;
    }

    // ---- every frame ----

    void Tick()
    {
        if (_picker is null) return;
        var now = Now;
        _speaker.Pump();
        TickBubble(now);
        TickBody(now);
        if (_form == "face" || _zap is { From: "face" } || _zap is { To: "face", Applied: true })
        {
            var line = _speaker.Current;
            if (_face.Step(now, line, line?.Affect, VoiceNow(now), Thinking) && _settings.Glitches)
                GlitchSound();
        }
        TickZap(now);
        OnHerOwn(now);
    }

    void TickBody(double now)
    {
        var pose = PictureNow(now);
        if (!ReferenceEquals(pose, _shown))
        {
            if (pose.IsIdle && !(_shown?.IsIdle ?? false)) _breathStart = now;
            _shown = pose;
            NextFrame.Opacity = 0;
            if (pose.IsIdle) _settings.Pose = pose.Name;
        }
        if (_speaker.Current is not null && ReferenceEquals(pose, _linePose)) ShowMouth(pose, now);
        else if (pose.IsIdle && _speaker.Current is null) ShowBreath(pose, now);
        else { Frame.Source = pose.Frames[0]; NextFrame.Opacity = 0; }
        TickBlink(pose, now);
        if (_form == "body" && _zap is null)
        {
            if (_settings.Glitches) { if (_fx.Glitch(Frame.Source, now)) GlitchSound(); }
            else _fx.StopGlitch();
        }
    }

    void GlitchSound()
    {
        if (_rng.NextDouble() < 0.35) _sfx.Play($"capri_glitch_{_rng.Next(1, 4)}", 0.12 + _rng.NextDouble() * 0.08);
    }

    Pose PictureNow(double now)
    {
        if (_flash is not null && now < _flashUntil) { _picker.Busy(); return _flash; }
        if (_linePose is not null && (_speaker.Busy || now < _backToIdleAt)) { _picker.Busy(); return _linePose; }
        _linePose = null;
        if (Thinking && _picker.Think(null) is { } think)
        {
            _picker.Busy();
            return _shown is { } s && s.Name.StartsWith("think_") ? s : think;
        }
        return _picker.Quiet(now);
    }

    /// <summary>What Voxta hears you say on the mic, in her bubble as you say it. Once you're done it goes to her as
    /// your message (Voxta only transcribes; sending it is the app's job, as in Voxta's own web app), and she thinks.</summary>
    void OnHeard(string text, bool done)
    {
        text = text.Trim();
        if (text == "") return;
        _said.Clear();
        BubbleText.Text = $"“{text}”";
        Bubble.Visibility = Visibility.Visible;
        BubbleScroll.ScrollToEnd();
        _bubbleHideAt = -1;
        if (!done) return;
        Log.Write($"heard: {text}");
        if (!_voxta.IsReady) return;
        _speaker.Interrupt();      // you talked over her
        _ = _voxta.Say(text);
        _thinkingUntil = Now + ThinkFor;
    }

    /// <summary>Her voice's mouth channels now; with no voice (switched off, or it didn't come) while she's saying
    /// something, her jaw just opens and shuts at a talking pace.</summary>
    double[]? VoiceNow(double now)
    {
        if (_speaker.Current is null) return null;
        if (_speaker.Mouth() is { } m) return m;
        if (now >= _mouthFlipAt)
        {
            _mouthOpen = !_mouthOpen;
            _mouthFlipAt = now + (_mouthOpen ? 0.09 : 0.06) + _rng.NextDouble() * 0.08;
        }
        return _mouthOpen ? [0.45, 0, 0, 0, 0] : [0, 0, 0, 0, 0];
    }

    void ShowMouth(Pose pose, double now)
    {
        var open = VoiceNow(now) is { } v && v[0] > JawOpen;
        Frame.Source = open && pose.Mouth is not null ? pose.Mouth : pose.Frames[0];
        NextFrame.Opacity = 0;
    }

    void ShowBreath(Pose pose, double now)
    {
        var frames = pose.Frames;
        var n = frames.Length;
        if (n < 2) { Frame.Source = frames[0]; return; }
        var at = (now - _breathStart) % BreathLoop / BreathLoop * n;   // 0..n: which frame, and how far into the next
        var i = (int)at;
        Frame.Source = frames[i % n];
        NextFrame.Source = frames[(i + 1) % n];
        NextFrame.Opacity = at - i;
    }

    /// <summary>Her words come up as she says them: the sentence she's on, spread over its length.</summary>
    void TickBubble(double now)
    {
        if (_speaker.Current is { } line && _said.Length > 0)
        {
            var part = Math.Clamp(_speaker.Elapsed / Math.Max(0.1, line.Seconds), 0, 1);
            var upTo = _lineFrom + (int)Math.Round((_said.Length - _lineFrom) * part);
            var text = _said.ToString(0, Math.Clamp(upTo, 0, _said.Length));
            if (BubbleText.Text != text)
            {
                BubbleText.Text = text;
                BubbleScroll.ScrollToEnd();
            }
        }
        else if (_said.Length > 0 && BubbleText.Text.Length < _said.Length)
            BubbleText.Text = _said.ToString();
        if (_bubbleHideAt > 0 && now >= _bubbleHideAt && !_speaker.Busy && InputBox.Visibility != Visibility.Visible)
        {
            Bubble.Visibility = Visibility.Collapsed;
            _bubbleHideAt = -1;
        }
    }

    void TickBlink(Pose pose, double now)
    {
        if (pose.Blink is null) { Blink.Visibility = Visibility.Hidden; return; }
        if (now >= _nextBlink && _blinkEnd < _nextBlink)
        {
            _blinkEnd = now + BlinkHold;
            _secondBlink = _rng.NextDouble() < DoubleChance ? _blinkEnd + DoubleGap : -1;
        }
        else if (_secondBlink > 0 && now >= _secondBlink)
        {
            _blinkEnd = now + BlinkHold;
            _secondBlink = -1;
        }
        var shut = now < _blinkEnd;
        Blink.Source = pose.Blink;
        Blink.Visibility = shut ? Visibility.Visible : Visibility.Hidden;
        if (!shut && _secondBlink < 0 && _blinkEnd >= _nextBlink) ScheduleBlink(now);
    }

    void ScheduleBlink(double now) => _nextBlink = now + BlinkMin + _rng.NextDouble() * (BlinkMax - BlinkMin);

    // ---- zipping about, and switching forms ----

    /// <summary>Off somewhere else, and/or into her other form: torn apart into a line of light (her face: switched
    /// off), lightning to where she lands, and back together there (her face: switched on).</summary>
    void StartZap(string to, Point? feet, double? height = null)
    {
        if (_zap is not null) return;
        if (to == "face" && !_face.Loaded) to = "body";
        var now = Now;
        _zap = new Zap { At = now, From = _form, To = to, Feet = feet, Height = height };
        _fx.StopGlitch();
        if (_form == "face") { _face.SwitchOff(now); _sfx.Play("capri_zap_out", 0.3); }
        else _sfx.Play("capri_zap_out");
        Log.Write($"zap {_form} -> {to}{(feet is { } f ? $" to {f.X:0},{f.Y:0}" : "")}");
    }

    void TickZap(double now)
    {
        if (_zap is not { } z) return;
        var t = now - z.At;
        var outFor = z.From == "body" ? BodyFx.ZapOut : FaceHud.OffSeconds;
        if (t < outFor)
        {
            if (z.From == "body") _fx.Out(Frame.Source, now, t);
            return;
        }
        if (!z.Applied)
        {
            z.Applied = true;
            z.InAt = now;
            var wasAt = HerMiddle();
            var feet = z.Feet ?? FeetNow();
            // (her landing look set up before the window moves: moving it shows whatever is drawn at once)
            if (z.To == "body")
            {
                _picker.NewFamily();   // up in another idle as she lands, as in the game
                if (z.Height is { } h) Stage.Height = _settings.Height = h;
                TickBody(now);
                _fx.In(Frame.Source, now, 0);
            }
            else if (z.From == "body") _fx.Clear();
            if (z.To != _form) ShowForm(z.To);
            UpdateLayout();
            SetFeet(feet);
            Remember();
            var landed = HerMiddle();
            if ((landed - wasAt).Length > 30) Bolt.Strike(wasAt, landed);
            _sfx.Play("capri_zap_in", 0.45);
            if (z.To == "face")
            {
                _face.SwitchOn(now);
                _sfx.Play($"capri_glitch_{_rng.Next(1, 4)}", 0.2);
            }
            return;
        }
        var ti = now - z.InAt;
        if (z.To == "body")
        {
            if (ti < BodyFx.ZapIn) { _fx.In(Frame.Source, now, ti); return; }
            _fx.Clear();
        }
        else if (ti < FaceHud.OnSeconds) return;
        _zap = null;
    }

    void ShowForm(string form)
    {
        _form = form;
        Stage.Visibility = form == "body" ? Visibility.Visible : Visibility.Collapsed;
        FaceBox.Visibility = form == "face" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Somewhere else to be: on the taskbar of this screen (now and then another), well away from here; her
    /// face anywhere on it.</summary>
    Point ZipSpot(string form)
    {
        var areas = Screens.WorkAreas(this);
        var area = Screens.At(this, HerMiddle());
        if (areas.Count > 1 && _rng.NextDouble() < OtherScreenChance)
            area = areas.Where(a => a != area).ElementAt(_rng.Next(areas.Count - 1));
        var w = Math.Max(ActualWidth, 340);
        var here = FeetNow();
        Point spot = here;
        for (var tries = 0; tries < 12; tries++)
        {
            var x = area.Left + w / 2 + _rng.NextDouble() * Math.Max(1, area.Width - w);
            var y = form == "body"
                ? area.Bottom
                : area.Top + FaceHeight() + 190 + _rng.NextDouble() * Math.Max(1, area.Height - FaceHeight() - 190);
            spot = new Point(x, Math.Min(area.Bottom, y));
            if ((spot - here).Length >= ZipAtLeast || !area.Contains(here)) break;
        }
        return spot;
    }

    double FaceHeight() => FaceBox.Width * FaceHud.CanvasH / FaceHud.CanvasW;

    /// <summary>Zips and form changes she does on her own, while quiet.</summary>
    void OnHerOwn(double now)
    {
        // the form asked for in the menu (or, on her own, the one she's switched to)
        var want = _settings.Form == "auto" ? null : _settings.Form;
        if (want is not null && want != _form && _zap is null && _face.Loaded) { SwitchTo(want); return; }
        if (_settings.Form == "auto" && _face.Loaded)
        {
            if (_formAt < 0) _formAt = now + Within(FormEvery);
            if (now >= _formAt && Quiet) { _formAt = -1; SwitchTo(_form == "body" ? "face" : "body"); return; }
        }
        // (only her full self zips about; her face stays where you put it)
        if (_settings.ZipOnHerOwn && _form == "body")
        {
            if (_zipAt < 0) _zipAt = now + Within(ZipEvery);
            if (now >= _zipAt && Quiet) { _zipAt = -1; ZipSomewhere(); }
        }
    }

    void ZipSomewhere()
    {
        if (_form != "body") return;
        // (now and then tiny, now and then big: a random size in the range set, when that's on)
        double? size = _settings.RandomSize
            ? Math.Round(Within((Math.Min(_settings.SizeMin, _settings.SizeMax), Math.Max(_settings.SizeMin, _settings.SizeMax))))
            : null;
        StartZap("body", ZipSpot("body"), size);
    }

    /// <summary>Into her other form, at its own spot: her face where you last put it, her body where it last was.
    /// (The first time: her face a little up from where her body is, her body on the taskbar under her face.)</summary>
    void SwitchTo(string form) => StartZap(form, SpotOf(form));

    Point SpotOf(string form)
    {
        var (x, y) = form == "face" ? (_settings.FaceX, _settings.FaceY) : (_settings.FeetX, _settings.FeetY);
        if (x is { } sx && y is { } sy && OnSomeScreen(sx, sy)) return new Point(sx, sy);
        var area = Screens.At(this, HerMiddle());
        var feet = FeetNow();
        return form == "body"
            ? new Point(feet.X, area.Bottom)
            : new Point(feet.X, Math.Min(area.Bottom, Math.Max(area.Top + FaceHeight() + 190, feet.Y - 160)));
    }

    double Within((double, double) r) => r.Item1 + _rng.NextDouble() * (r.Item2 - r.Item1);

    // ---- clicking, dragging, the wheel ----

    void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || _zap is not null) return;
        var (left, top) = (Left, Top);
        // where the press was, asked before DragMove (afterwards the mouse no longer counts as over her)
        var at = e.GetPosition(Holder);
        var onHer = at.X >= 0 && at.Y >= 0 && at.X <= Holder.ActualWidth && at.Y <= Holder.ActualHeight;
        DragMove();
        var moved = Math.Abs(Left - left) > 2 || Math.Abs(Top - top) > 2;
        if (moved) { Remember(); return; }
        // a click on her, not a drag: open or close the type box
        if (onHer)
        {
            if (InputBox.Visibility == Visibility.Visible) CloseInput();
            else OpenInput();
        }
    }

    /// <summary>The wheel over her: bigger (up) or smaller (down), her feet staying where they are.</summary>
    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Holder.IsMouseOver || _zap is not null) return;
        var f = Math.Pow(1.1, e.Delta / 120.0);
        if (_form == "body") Resize(() => Stage.Height = _settings.Height = Math.Clamp(_settings.Height * f, 60, 1100));
        else Resize(() => FaceBox.Width = _settings.FaceWidth = Math.Clamp(_settings.FaceWidth * f, 140, 1200));
        e.Handled = true;
    }

    // ---- where she stands ----

    Point FeetNow() => new(Left + ActualWidth / 2, Top + ActualHeight);
    Point HerMiddle() => new(Left + ActualWidth / 2, Top + 190 + (ActualHeight - 190) / 2);

    void SetFeet(Point feet)
    {
        Left = feet.X - ActualWidth / 2;
        Top = feet.Y - ActualHeight;
    }

    void PlaceAtStart()
    {
        var (x, y) = _form == "face" ? (_settings.FaceX, _settings.FaceY) : (_settings.FeetX, _settings.FeetY);
        if (x is { } sx && y is { } sy && OnSomeScreen(sx, sy)) SetFeet(new Point(sx, sy));
        else if (_form == "face") { SetFeet(SpotOf("face")); Remember(); }
        else StandOnTaskbar();
    }

    /// <summary>Bottom right of the main screen, her feet on the taskbar.</summary>
    void StandOnTaskbar()
    {
        var work = SystemParameters.WorkArea;
        SetFeet(new Point(work.Right - ActualWidth / 2 - 40, work.Bottom));
        Remember();
    }

    static bool OnSomeScreen(double x, double y)
    {
        return x >= SystemParameters.VirtualScreenLeft && x <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && y >= SystemParameters.VirtualScreenTop && y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    }

    void Resize(Action apply)
    {
        var feet = FeetNow();
        apply();
        UpdateLayout();
        SetFeet(feet);
        Remember();
    }

    /// <summary>Where she is now, kept for the form she's in.</summary>
    void Remember()
    {
        var feet = FeetNow();
        if (_form == "face") (_settings.FaceX, _settings.FaceY) = (feet.X, feet.Y);
        else (_settings.FeetX, _settings.FeetY) = (feet.X, feet.Y);
        _settings.Save();
    }

    // ---- right-click menu ----

    void BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action click)
        {
            var i = new MenuItem { Header = header };
            i.Click += (_, _) => click();
            return i;
        }
        var refresh = new List<Action>();   // (ticks brought up to date as the menu opens: Settings can change them)
        MenuItem Toggle(string header, Func<bool> get, Action<bool> set)
        {
            var i = new MenuItem { Header = header, IsCheckable = true, IsChecked = get() };
            i.Click += (_, _) => { set(i.IsChecked); _settings.Save(); ApplySettings(); };
            refresh.Add(() => i.IsChecked = get());
            return i;
        }
        menu.Opened += (_, _) => refresh.ForEach(r => r());

        menu.Items.Add(Item("Talk to her", OpenInput));
        menu.Items.Add(Toggle("Mic (talk to her out loud)", () => _settings.Mic, v =>
        {
            _settings.Mic = v;
            _settings.Save();
            _ = _voxta.Reconnect();
        }));
        menu.Items.Add(Toggle("Voice", () => _settings.Voice, v => _settings.Voice = v));
        var volume = new MenuItem { Header = "Volume" };
        foreach (var (name, v) in Volumes)
            volume.Items.Add(new MenuItem { Header = name, IsCheckable = true, Tag = v });
        foreach (MenuItem i in volume.Items) i.Click += (_, _) => { _settings.Volume = (double)i.Tag; _settings.Save(); };
        volume.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem i in volume.Items) i.IsChecked = Math.Abs((double)i.Tag - _settings.Volume) < 0.01;
        };
        menu.Items.Add(volume);
        menu.Items.Add(new Separator());

        // her look: her full self, her face HUD, or switching between them on her own; and the face's options
        var look = new MenuItem { Header = "Look" };
        foreach (var (name, form) in new[] { ("Full body", "body"), ("Face HUD", "face"), ("Switch on her own", "auto") })
            look.Items.Add(new MenuItem { Header = name, IsCheckable = true, Tag = form, IsEnabled = form == "body" || _face.Loaded });
        foreach (var i in look.Items.OfType<MenuItem>().ToList())
            i.Click += (_, _) => { _settings.Form = (string)i.Tag; _formAt = -1; _settings.Save(); };
        look.Items.Add(new Separator());
        look.Items.Add(Toggle("Face HUD: green panel", () => _settings.FacePanel, v => { _settings.FacePanel = v; _face.ShowPanel = v; }));
        look.SubmenuOpened += (_, _) =>
        {
            foreach (var i in look.Items.OfType<MenuItem>()) if (i.Tag is string f) i.IsChecked = f == _settings.Form;
        };
        menu.Items.Add(look);

        var poses = new MenuItem { Header = "Pose" };
        foreach (var p in Idles)
        {
            var item = new MenuItem { Header = p.Name.Replace("idle_", "Idle "), IsCheckable = true, Tag = p };
            item.Click += (_, _) => { _picker.SetFamily(p.Family); _settings.Pose = p.Name; _settings.Save(); };
            poses.Items.Add(item);
        }
        poses.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem i in poses.Items) i.IsChecked = ReferenceEquals(i.Tag, _picker.Idle);
        };
        menu.Items.Add(poses);

        var sizes = new MenuItem { Header = "Size (or the mouse wheel)" };
        foreach (var s in Sizes)
        {
            var item = new MenuItem { Header = s.Name, IsCheckable = true };
            item.Click += (_, _) => Resize(() =>
            {
                if (_form == "body") Stage.Height = _settings.Height = s.Body;
                else FaceBox.Width = _settings.FaceWidth = s.Face;
            });
            sizes.Items.Add(item);
        }
        sizes.SubmenuOpened += (_, _) =>
        {
            for (var i = 0; i < Sizes.Length; i++)
                ((MenuItem)sizes.Items[i]).IsChecked = _form == "body"
                    ? Math.Abs(Sizes[i].Body - _settings.Height) < 1
                    : Math.Abs(Sizes[i].Face - _settings.FaceWidth) < 1;
        };
        menu.Items.Add(sizes);
        menu.Items.Add(new Separator());

        var zip = Item("Zip somewhere", ZipSomewhere);
        refresh.Add(() => zip.IsEnabled = _form == "body");   // (her face stays put)
        menu.Items.Add(zip);
        menu.Items.Add(Toggle("Zip around on her own", () => _settings.ZipOnHerOwn, v => { _settings.ZipOnHerOwn = v; _zipAt = -1; }));
        menu.Items.Add(Toggle("Change idles on her own", () => _settings.IdlesOnHerOwn, v => _settings.IdlesOnHerOwn = _picker.ChangesOnHerOwn = v));
        menu.Items.Add(Toggle("Glitches", () => _settings.Glitches, v => _settings.Glitches = v));
        menu.Items.Add(Toggle("Sound effects", () => _settings.Effects, v => _settings.Effects = v));
        menu.Items.Add(Item("Back to the taskbar", () =>
        {
            var work = SystemParameters.WorkArea;
            StartZap(_form, new Point(work.Right - Math.Max(ActualWidth, 300) / 2 - 40,
                _form == "body" ? work.Bottom : work.Bottom - 40));
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", OpenSettings));
        menu.Items.Add(Item("Quit", Close));

        ContextMenu = menu;
    }

    SettingsWindow? _settingsWindow;

    void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings, ApplySettings);
        _settingsWindow.Show();
    }

    /// <summary>Settings changed (in the Settings window or the menu): her behaviour picks them up now.</summary>
    void ApplySettings()
    {
        _picker.ChangesOnHerOwn = _settings.IdlesOnHerOwn;
        _picker.QuietFor = (_settings.IdleMin, _settings.IdleMax);
        _face.ShowPanel = _settings.FacePanel;
        Holder.Opacity = Math.Clamp(_settings.Opacity, 0.15, 1);
        _zipAt = _formAt = -1;   // the next zip and form change timed afresh
        if (Math.Abs(FaceBox.Width - _settings.FaceWidth) > 0.5) Resize(() => FaceBox.Width = _settings.FaceWidth);
        if (!_settings.Glitches) _fx.StopGlitch();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _speaker.Dispose();
        _settings.Save();
        base.OnClosed(e);
        await _voxta.DisposeAsync();
    }

    // ---- keep her out of Alt+Tab ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow);
    }

    const int GwlExStyle = -20, WsExToolWindow = 0x80;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
