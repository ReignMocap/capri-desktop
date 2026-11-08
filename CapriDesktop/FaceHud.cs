using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CapriDesktop;

/// <summary>Her face HUD form, as in the game (capri.lua, "her eyes form"): her eyes on their green panel and her mouth
/// under them. Quiet, her eyes are never still (glances, looks, bored and sleepy after a while, a rare silly one);
/// talking, a look for each sentence's mood, her mouth shaped by her voice, her mood on it when shut, the whole face
/// pulsing with each word; blinks, glitches (jitter, echo, flicker, squash, bars with static), switching on and off.
/// Drawn in the eyes pictures' own pixels (1024 x 341 eyes, from tools/prep_face.py), scaled to fit.</summary>
public sealed class FaceHud
{
    public const double W = 1024, H = 341;               // the eyes panel
    public const double CanvasW = 1100, CanvasH = 640;   // room round it for the mouth, the pulse and jolts
    const double OffX = (CanvasW - W) / 2, OffY = 30;
    // her mouth under the eyes: its middle MOUTH_X, MOUTH_Y from theirs, at MOUTH_SIZE of its picture
    const double MouthX = -7.5, MouthY = 242, MouthSize = 0.735;
    public const double OnSeconds = 0.45, OffSeconds = 0.32;
    const double Pulse = 0.06, BlinkFor = 0.12, StaticChance = 0.5, MouthHold = 0.06, BackToIdle = 1.2;
    const double BoredAfter = 25, SleepyAfter = 60;
    static readonly (double, double) PlainFor = (1.2, 3.5), BlinkEvery = (2.5, 6), GlitchEvery = (3, 8), GlitchTalking = (1.5, 4);
    public static readonly Color Green = Color.FromRgb(3, 249, 148);

    // a sentence's mood -> the looks for it (the game's EYES_LOOK)
    static readonly Dictionary<string, string[]> Look = new(StringComparer.OrdinalIgnoreCase)
    {
        ["happy"] = ["eyes_happy"], ["joy"] = ["eyes_happy", "eyes_laugh", "eyes_sparkle"],
        ["chuckling"] = ["eyes_laugh", "eyes_happy"], ["laughing"] = ["eyes_laugh"], ["amused"] = ["eyes_laugh", "eyes_smug"],
        ["excited"] = ["eyes_excited", "eyes_sparkle"], ["impressed"] = ["eyes_sparkle", "eyes_excited"],
        ["amazed"] = ["eyes_sparkle", "eyes_surprised"], ["soft"] = ["eyes_happy", "eyes_heart"],
        ["flirty"] = ["eyes_flirty", "eyes_heart", "eyes_smug"], ["sarcastic"] = ["eyes_smug", "eyes_roll"],
        ["angry"] = ["eyes_angry"], ["furious"] = ["eyes_angry"], ["frustrated"] = ["eyes_angry", "eyes_roll"],
        ["annoyed"] = ["eyes_suspicious", "eyes_suspicious_2", "eyes_roll"], ["disgusted"] = ["eyes_suspicious_2", "eyes_roll"],
        ["sighing"] = ["eyes_bored_1", "eyes_roll", "eyes_sad"],
        ["sad"] = ["eyes_sad"], ["crying"] = ["eyes_sad"], ["sobbing"] = ["eyes_sad"], ["shaky"] = ["eyes_sad", "eyes_look_down"],
        ["surprised"] = ["eyes_surprised"], ["breathless"] = ["eyes_surprised"],
        ["afraid"] = ["eyes_scared"], ["scared"] = ["eyes_scared"], ["nervous"] = ["eyes_scared", "eyes_look_down_left"],
        ["embarrassed"] = ["eyes_look_down", "eyes_suspicious_3", "eyes_look_left", "eyes_look_down_right"],
    };
    static readonly string[] Glance = ["eyes_look_left", "eyes_look_right", "eyes_look_up", "eyes_look_down",
        "eyes_look_up_left", "eyes_look_up_right", "eyes_look_down_left", "eyes_look_down_right"];
    static readonly Dictionary<string, string> Opposite = new()
    {
        ["eyes_look_left"] = "eyes_look_right", ["eyes_look_right"] = "eyes_look_left",
        ["eyes_look_up"] = "eyes_look_down", ["eyes_look_down"] = "eyes_look_up",
        ["eyes_look_up_left"] = "eyes_look_down_right", ["eyes_look_down_right"] = "eyes_look_up_left",
        ["eyes_look_up_right"] = "eyes_look_down_left", ["eyes_look_down_left"] = "eyes_look_up_right",
    };
    static readonly string[] MoodLooks = ["eyes_squint", "eyes_suspicious", "eyes_suspicious_2", "eyes_suspicious_3", "eyes_smug"];
    static readonly string[] Bored = ["eyes_bored_1", "eyes_bored_2"];
    static readonly string[] Rare = ["eyes_roll", "eyes_cross", "eyes_dizzy", "eyes_spiral", "eyes_sparkle"];
    const string Plain = "eyes_default";
    // her mood on her shut mouth, from the look in her eyes: (talking, quiet) (the game's Mouth.MOOD)
    static readonly Dictionary<string, (string Talking, string Quiet)> MouthMood = new()
    {
        ["eyes_happy"] = ("mouth_smile", "mouth_smile"), ["eyes_heart"] = ("mouth_smile", "mouth_smile"),
        ["eyes_sparkle"] = ("mouth_smile", "mouth_grin"), ["eyes_excited"] = ("mouth_smile", "mouth_grin"),
        ["eyes_laugh"] = ("mouth_smile", "mouth_laugh"),
        ["eyes_smug"] = ("mouth_smirk", "mouth_smirk"), ["eyes_flirty"] = ("mouth_smirk", "mouth_smirk"),
        ["eyes_suspicious_3"] = ("mouth_smirk", "mouth_smirk"),
        ["eyes_angry"] = ("mouth_frown", "mouth_frown"), ["eyes_sad"] = ("mouth_frown", "mouth_frown"),
        ["eyes_scared"] = ("mouth_frown", "mouth_frown"),
        ["eyes_surprised"] = ("mouth_closed", "mouth_round"),
        ["eyes_roll"] = ("mouth_pout", "mouth_pout"), ["eyes_bored_1"] = ("mouth_pout", "mouth_pout"),
        ["eyes_bored_2"] = ("mouth_pout", "mouth_pout"), ["eyes_suspicious"] = ("mouth_pout", "mouth_pout"),
        ["eyes_suspicious_2"] = ("mouth_pout", "mouth_pout"),
        ["eyes_cross"] = ("mouth_tongue", "mouth_tongue"), ["eyes_dizzy"] = ("mouth_tongue", "mouth_tongue"),
        ["eyes_spiral"] = ("mouth_tongue", "mouth_tongue"),
    };

    readonly Dictionary<string, BitmapSource> _art = [];
    readonly Random _rng = new();
    readonly Grid _root;
    readonly Rectangle _panel;
    readonly Image _eyes, _echo, _mouth;
    readonly Rectangle[] _bars = new Rectangle[3];
    readonly ScaleTransform _scale = new();
    readonly TranslateTransform _move = new(), _echoMove = new();

    // her state
    double _bornAt = -99, _leavingAt = -1;
    object? _line;
    string _look = Plain;
    double _lastSpoke = -1, _quietFrom = -1;
    string? _idlePart, _queued;
    double _idleUntil = -1, _blinkAt = -1, _glitchAt = -1;
    (string Kind, double From, double Len, bool Static)? _glitch;
    string? _shown, _mouthShown;
    double _mouthAt = -1, _pulse;

    public bool Loaded => _art.Count > 0;

    public FaceHud(string artFolder, Canvas host)
    {
        var dir = System.IO.Path.Combine(artFolder, "face");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.png"))
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.UriSource = new Uri(f);
                b.EndInit();
                b.Freeze();
                _art[System.IO.Path.GetFileNameWithoutExtension(f)] = b;
            }

        host.Width = CanvasW;
        host.Height = CanvasH;
        _root = new Grid { Width = W, Height = CanvasH - OffY, RenderTransformOrigin = new Point(0.5, H / 2 / (CanvasH - OffY)) };
        _root.RenderTransform = new TransformGroup { Children = { _scale, _move } };
        Canvas.SetLeft(_root, OffX);
        Canvas.SetTop(_root, OffY);
        host.Children.Add(_root);
        var inner = new Canvas();
        _root.Children.Add(inner);
        _panel = new Rectangle { Width = W, Height = H, Fill = new SolidColorBrush(Green) };
        _echo = new Image { Width = W, Height = H, Opacity = 0, RenderTransform = _echoMove };
        _eyes = new Image { Width = W, Height = H };
        var mouthPic = _art.GetValueOrDefault("mouth_closed");
        var mw = (mouthPic?.PixelWidth ?? W) * MouthSize;
        var mh = (mouthPic?.PixelHeight ?? H) * MouthSize;
        _mouth = new Image { Width = mw, Height = mh, Stretch = Stretch.Fill };
        Canvas.SetLeft(_mouth, W / 2 + MouthX - mw / 2);
        Canvas.SetTop(_mouth, H / 2 + MouthY - mh / 2);
        inner.Children.Add(_panel);
        inner.Children.Add(_echo);
        inner.Children.Add(_eyes);
        inner.Children.Add(_mouth);
        for (var i = 0; i < 3; i++)
        {
            var light = i == 1;
            _bars[i] = new Rectangle
            {
                Width = W, Height = 6, Opacity = 0, RenderTransform = new TranslateTransform(),
                Fill = new SolidColorBrush(light ? Color.FromRgb(217, 255, 230) : Color.FromRgb(3, 20, 13)),
            };
            inner.Children.Add(_bars[i]);
        }
    }

    public bool ShowPanel { set => _panel.Visibility = value ? Visibility.Visible : Visibility.Hidden; }
    public bool Leaving => _leavingAt >= 0;

    /// <summary>Switching on: flickering in, jumping about, stretched, settling.</summary>
    public void SwitchOn(double now)
    {
        _bornAt = now;
        _leavingAt = -1;
        _glitch = null;
        _quietFrom = now;
    }

    /// <summary>Switching off: squeezed to a line, then a dot, gone (OffSeconds).</summary>
    public void SwitchOff(double now) => _leavingAt = now;

    bool Has(string name) => _art.ContainsKey(name);
    double Within((double, double) r) => r.Item1 + _rng.NextDouble() * Math.Max(0, r.Item2 - r.Item1);

    string? Pick(IEnumerable<string> list, string? not = null)
    {
        var have = list.Where(n => Has(n) && n != not).ToList();
        return have.Count == 0 ? null : have[_rng.Next(have.Count)];
    }

    /// <summary>Her next idle look, quiet: (look, until).</summary>
    (string, double) IdleNext(double now)
    {
        if (_queued is { } q) { _queued = null; return (q, now + 0.5 + _rng.NextDouble() * 0.8); }
        if (_idlePart is not null && _idlePart != Plain) return (Plain, now + Within(PlainFor));
        var quiet = now - (_quietFrom < 0 ? now : _quietFrom);
        var r = _rng.NextDouble();
        if (quiet > SleepyAfter && r < 0.15 && Has("eyes_sleepy")) return ("eyes_sleepy", now + 3 + _rng.NextDouble() * 4);
        if (quiet > BoredAfter && r < 0.35 && Pick(Bored) is { } b) return (b, now + 2.5 + _rng.NextDouble() * 3);
        r = _rng.NextDouble();
        if (r < 0.6 && Pick(Glance) is { } g)
        {
            if (_rng.NextDouble() < 0.3 && Has(Opposite[g])) _queued = Opposite[g];
            return (g, now + 0.6 + _rng.NextDouble() * 1.2);
        }
        if (r is >= 0.6 and < 0.85 && Pick(MoodLooks) is { } m) return (m, now + 1.5 + _rng.NextDouble() * 2.5);
        if (r is >= 0.85 and < 0.93 && Pick(Rare) is { } x) return (x, now + 0.8 + _rng.NextDouble() * 0.7);
        return (Plain, now + Within(PlainFor));
    }

    /// <summary>Her mouth from her voice's channels (jaw, round, wide, closed, bite): shut on silence and m/b/p, teeth
    /// on the lip for f/v, round for oo/w, wide for ee/s, else as open as the jaw.</summary>
    string MouthFor(double[]? v)
    {
        string? Have(string n) => Has(n) ? n : null;
        if (v is null || v[0] <= 0) return "mouth_closed";
        var (jaw, round, wide, closed, bite) = (v[0], v[1], v[2], v[3], v[4]);
        if (closed > 0.35 || jaw < 0.08) return "mouth_closed";
        if (bite > 0.4) return Have("mouth_bite") ?? "mouth_closed";
        if (round > 0.45 && round >= wide) return Have("mouth_round") ?? Have("mouth_half") ?? "mouth_closed";
        if (wide > 0.45 && jaw < 0.6) return Have("mouth_wide") ?? Have("mouth_half") ?? "mouth_closed";
        if (jaw > 0.55) return Have("mouth_open") ?? "mouth_closed";
        return Have("mouth_half") ?? (jaw > 0.2 ? Have("mouth_open") : null) ?? "mouth_closed";
    }

    /// <summary>One frame. line: the sentence she's saying (any object, new per sentence) or null; mouth: her voice's
    /// channels now, or null; thinking: waiting on her answer. Returns true as a glitch starts (for its sound).</summary>
    public bool Step(double now, object? line, string? affect, double[]? mouth, bool thinking, double scale = 1)
    {
        var started = false;
        if (line is not null)
        {
            if (!ReferenceEquals(line, _line))
            {
                _line = line;
                _look = Pick(affect is not null && Look.TryGetValue(affect, out var l) ? l : []) ?? Plain;
            }
            _lastSpoke = now;
            _idlePart = _queued = null;
            _idleUntil = -1;
        }
        else if (_lastSpoke >= 0 && now - _lastSpoke > BackToIdle)
        {
            _look = Plain;
            _line = null;
            _lastSpoke = -1;
            _quietFrom = now;
        }
        if (line is null && _lastSpoke < 0)
        {
            if (_quietFrom < 0) _quietFrom = now;
            if (_idleUntil < 0 || now >= _idleUntil) (_idlePart, _idleUntil) = IdleNext(now);
            _look = _idlePart ?? Plain;
            if (thinking && Has("eyes_loading")) _look = "eyes_loading";
        }

        // her blink
        if (_blinkAt < 0) _blinkAt = now + Within(BlinkEvery);
        var bt = now - _blinkAt;
        var blink = bt >= 0 && bt < BlinkFor && Has("eyes_blink");
        if (bt >= BlinkFor) _blinkAt = -1;
        var part = blink ? "eyes_blink" : _look;
        if (_glitch is { Static: true }) part = "eyes_static";
        if (Leaving && Has("eyes_offline")) part = "eyes_offline";
        if (part != _shown && _art.TryGetValue(part, out var pic))
        {
            _eyes.Source = _echo.Source = pic;
            // a new look for one of her sentences comes in with a little jolt; idle looks just change
            if (!blink && _shown is not null && part != Plain && _glitch is null && _line is not null)
                _glitch = ("jitter", now, 0.14, false);
            _shown = part;
        }

        // her glitches: now and then (more while she talks)
        if (_glitchAt < 0) _glitchAt = now + Within(line is not null ? GlitchTalking : GlitchEvery);
        if (now >= _glitchAt && _glitch is null && !Leaving)
        {
            string[] kinds = ["jitter", "echo", "flicker", "bars", "squash", "echo", "bars"];
            var kind = kinds[_rng.Next(kinds.Length)];
            _glitch = (kind, now, kind == "bars" ? 0.35 : 0.12 + _rng.NextDouble() * 0.14,
                kind == "bars" && Has("eyes_static") && _rng.NextDouble() < StaticChance);
            // a big one, quiet: dizzy for a moment after
            if (kind == "bars" && line is null && _rng.NextDouble() < 0.15 && Has("eyes_dizzy"))
                (_idlePart, _idleUntil, _queued) = ("eyes_dizzy", now + 0.35 + 1.1, null);
            _glitchAt = -1;
            started = true;
        }

        // her mouth: her mood on it when shut, each shape held a moment at least
        var mouthPart = MouthFor(mouth);
        if (mouthPart == "mouth_closed" && MouthMood.TryGetValue(_look, out var mm))
        {
            var p = line is not null ? mm.Talking : mm.Quiet;
            if (Has(p)) mouthPart = p;
        }
        if (mouthPart != _mouthShown && now - _mouthAt >= MouthHold && _art.TryGetValue(mouthPart, out var mpic))
        {
            _mouth.Source = mpic;
            _mouthShown = mouthPart;
            _mouthAt = now;
        }

        // her voice: a pulse with each word
        _pulse += ((mouth?[0] ?? 0) - _pulse) * 0.5;
        var k = scale * (1 + Pulse * _pulse);
        double sx = 1, sy = 1, dx = 0, dy = 0, op = 1;
        var on = now - _bornAt;
        if (Leaving)
        {
            var q = Math.Min(1, (now - _leavingAt) / OffSeconds);
            if (q < 0.55) { sy = 1 - 0.97 * (q / 0.55); sx = 1 + 0.15 * (q / 0.55); }
            else { sy = 0.03; sx = 1.15 * (1 - (q - 0.55) / 0.45) + 0.01; }
            dx = (_rng.NextDouble() - 0.5) * 30 * (1 - q);
            if (q >= 1) op = 0;
        }
        else if (on < OnSeconds)
        {
            var q = on / OnSeconds;
            op = _rng.NextDouble() < 0.25 + 0.75 * q ? 1 : 0;
            sx = 1 + (_rng.NextDouble() - 0.5) * 0.5 * (1 - q);
            sy = Math.Min(1, 0.05 + q * 1.6);
            dx = (_rng.NextDouble() - 0.5) * 80 * (1 - q);
            dy = (_rng.NextDouble() - 0.5) * 20 * (1 - q);
        }
        double echoOp = 0, echoX = 0;
        var barsOn = false;
        if (_glitch is { } g && now - g.From >= g.Len) _glitch = null;
        if (_glitch is { } gl && !Leaving && on >= OnSeconds)
        {
            double R(double n) => (_rng.NextDouble() - 0.5) * 2 * n;
            switch (gl.Kind)
            {
                case "jitter": dx = R(28); dy = R(8); break;
                case "echo": echoOp = 0.55; echoX = R(60); dx = R(10); break;
                case "flicker": op = _rng.NextDouble() < 0.5 ? 1 : 0.15; break;
                case "squash": sx = 1 + R(0.18); sy = 1 - Math.Abs(R(0.25)); dx = R(14); break;
                case "bars": barsOn = true; dx = R(6); break;
            }
        }
        _scale.ScaleX = k * sx;
        _scale.ScaleY = k * sy;
        _move.X = dx;
        _move.Y = dy;
        _root.Opacity = op;
        _echo.Opacity = echoOp;
        _echoMove.X = echoX;
        foreach (var b in _bars)
        {
            if (barsOn && _rng.NextDouble() < 0.8)
            {
                var h = 3 + _rng.NextDouble() * 12;
                b.Height = h;
                Canvas.SetTop(b, _rng.NextDouble() * (H - h));
                ((TranslateTransform)b.RenderTransform).X = (_rng.NextDouble() - 0.5) * 40;
                b.Opacity = 0.5 + _rng.NextDouble() * 0.4;
            }
            else b.Opacity = 0;
        }
        return started;
    }
}
