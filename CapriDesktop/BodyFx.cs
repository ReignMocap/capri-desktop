using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CapriDesktop;

/// <summary>Her hologram effects on her full body, as in the game (capri.lua tear / lineOf / tickZap / tickGlitch):
/// two coloured ghosts of her behind (cyan, pink) knocked sideways, her picture cut into bands shoved about, and a
/// line of light she's squeezed into when she zaps. Sizes are the game's, in its 1024 x 1536 canvas's pixels,
/// scaled to her size here.</summary>
public sealed class BodyFx
{
    public const int StripCount = 12;
    public const double ZapOut = 0.34, ZapIn = 0.40;
    // little glitches while she's just there: every 2.5-7 s
    static readonly (double, double) GlitchEvery = (2.5, 7);
    const double CanvasH = 1536;

    readonly Grid _stage, _body;
    readonly Rectangle _ghostCyan, _ghostPink, _line;
    readonly Image[] _strips = new Image[StripCount];
    readonly TranslateTransform[] _shove = new TranslateTransform[StripCount];
    readonly ScaleTransform[] _squeeze = new ScaleTransform[StripCount];
    readonly ScaleTransform _ghostScale1 = new(), _ghostScale2 = new(), _lineScale = new();
    readonly TranslateTransform _ghostMove1 = new(), _ghostMove2 = new();
    readonly Random _rng = new();
    double _shovedAt = -1, _glitchAt = -1, _glitchUntil = -1, _glitchAmp;
    string? _glitch;

    public BodyFx(Grid stage, Grid body, Grid stripsHost, Rectangle ghostCyan, Rectangle ghostPink, Rectangle line)
    {
        (_stage, _body, _ghostCyan, _ghostPink, _line) = (stage, body, ghostCyan, ghostPink, line);
        _ghostCyan.RenderTransformOrigin = _ghostPink.RenderTransformOrigin = new Point(0.5, 0.5);
        _ghostCyan.RenderTransform = new TransformGroup { Children = { _ghostScale1, _ghostMove1 } };
        _ghostPink.RenderTransform = new TransformGroup { Children = { _ghostScale2, _ghostMove2 } };
        _line.RenderTransformOrigin = new Point(0.5, 0.5);
        _line.RenderTransform = _lineScale;
        for (var k = 0; k < StripCount; k++)
        {
            _shove[k] = new TranslateTransform();
            _squeeze[k] = new ScaleTransform();
            var s = new Image
            {
                Stretch = Stretch.Uniform, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { _squeeze[k], _shove[k] } },
            };
            _strips[k] = s;
            stripsHost.Children.Add(s);
        }
        stage.SizeChanged += (_, _) => Layout();
    }

    /// <summary>Display pixels per game canvas pixel.</summary>
    double K => _stage.ActualHeight / CanvasH;

    void Layout()
    {
        var w = _stage.ActualWidth;
        var h = _stage.ActualHeight / StripCount;
        for (var k = 0; k < StripCount; k++)
            _strips[k].Clip = new RectangleGeometry(new Rect(0, k * h, w, h + 0.5));
        _line.Width = 18 * K;
        _line.Height = _stage.ActualHeight * 0.92;
    }

    /// <summary>Everything back to plain.</summary>
    public void Clear()
    {
        _body.Opacity = 1;
        _ghostCyan.Opacity = _ghostPink.Opacity = 0;
        _ghostMove1.X = _ghostMove2.X = 0;
        _ghostScale1.ScaleX = _ghostScale2.ScaleX = 1;
        foreach (var s in _strips) s.Opacity = 0;
        for (var k = 0; k < StripCount; k++) { _shove[k].X = 0; _squeeze[k].ScaleX = 1; }
        _line.Opacity = 0;
        _lineScale.ScaleX = 1;
    }

    /// <summary>Torn: her picture hidden behind its bands, each shoved sideways by up to `amp` (new shoves every few
    /// hundredths of a second), squeezed across to `squeeze`, at `alpha`; the colour ghosts split by `split`.</summary>
    public void Tear(ImageSource? picture, double now, double amp, double squeeze, double alpha, double split)
    {
        _body.Opacity = 0;
        var reshove = now - _shovedAt > 0.035;
        if (reshove) _shovedAt = now;
        for (var k = 0; k < StripCount; k++)
        {
            if (!ReferenceEquals(_strips[k].Source, picture)) _strips[k].Source = picture;
            if (reshove)
            {
                var dx = (_rng.NextDouble() * 2 - 1) * amp;
                if (_rng.NextDouble() < 0.35) dx = 0;
                _shove[k].X = dx * K;
            }
            _squeeze[k].ScaleX = squeeze;
            _strips[k].Opacity = alpha * (0.6 + _rng.NextDouble() * 0.4);
        }
        Ghosts(picture, split, split > 0 ? alpha * 0.45 : 0, squeeze);
    }

    void Ghosts(ImageSource? picture, double split, double opacity, double squeeze)
    {
        SetMask(_ghostCyan, picture);
        SetMask(_ghostPink, picture);
        _ghostMove1.X = -split * K;
        _ghostMove2.X = split * K;
        _ghostCyan.Opacity = _ghostPink.Opacity = opacity;
        _ghostScale1.ScaleX = _ghostScale2.ScaleX = squeeze;
    }

    static void SetMask(Rectangle ghost, ImageSource? picture)
    {
        if (ghost.OpacityMask is ImageBrush b && ReferenceEquals(b.ImageSource, picture)) return;
        ghost.OpacityMask = picture is null ? null : new ImageBrush(picture) { Stretch = Stretch.Uniform };
    }

    public void LineOf(double alpha, double width)
    {
        _line.Opacity = alpha;
        _lineScale.ScaleX = width;
    }

    /// <summary>One frame of a zap out (t from 0 to ZapOut): tearing apart, then squeezed into a line of light.</summary>
    public void Out(ImageSource? picture, double now, double t)
    {
        var k = Math.Clamp(t / ZapOut, 0, 1);
        if (k < 0.6)
        {
            Tear(picture, now, 18 + 90 * k / 0.6, 1, 1, 6 + 26 * k / 0.6);
            LineOf(0, 1);
        }
        else
        {
            var q = (k - 0.6) / 0.4;
            Tear(picture, now, 40 * (1 - q), 1 - 0.97 * q, 1 - q, 10 * (1 - q));
            LineOf(q, 1 + 2 * q);
        }
    }

    /// <summary>One frame of a zap in (t from 0 to ZapIn): a line of light widening, then the bands snapping back.</summary>
    public void In(ImageSource? picture, double now, double t)
    {
        var k = Math.Clamp(t / ZapIn, 0, 1);
        if (k < 0.3)
        {
            var q = k / 0.3;
            Tear(picture, now, 0, 0.03 + 0.5 * q, q, 0);
            LineOf(1, 3 - q);
        }
        else
        {
            var q = (k - 0.3) / 0.7;
            Tear(picture, now, 70 * (1 - q), 0.53 + 0.47 * q, 1, 24 * (1 - q));
            LineOf(1 - q, 1);
        }
    }

    /// <summary>Little glitches while she's just there: a quick tear or a colour shiver, now and then. Returns true
    /// as one starts (for its sound).</summary>
    public bool Glitch(ImageSource? picture, double now)
    {
        if (_glitch is not null)
        {
            if (now >= _glitchUntil) { Clear(); _glitch = null; }
            else if (_glitch == "tear") Tear(picture, now, _glitchAmp, 1, 1, 4);
            else
            {
                _body.Opacity = 1;
                Ghosts(picture, 0, 0.5, 1);
                _ghostMove1.X = -(6 + _rng.NextDouble() * _glitchAmp) * K;
                _ghostMove2.X = (6 + _rng.NextDouble() * _glitchAmp) * K;
            }
            return false;
        }
        if (_glitchAt < 0) _glitchAt = now + GlitchEvery.Item1 + _rng.NextDouble() * (GlitchEvery.Item2 - GlitchEvery.Item1);
        if (now < _glitchAt) return false;
        _glitchAt = -1;
        _glitch = _rng.NextDouble() < 0.55 ? "tear" : "split";
        _glitchUntil = now + 0.08 + _rng.NextDouble() * 0.14;
        _glitchAmp = _glitch == "tear" ? 10 + _rng.NextDouble() * 40 : 8 + _rng.NextDouble() * 20;
        return true;
    }

    public void StopGlitch()
    {
        if (_glitch is null) return;
        _glitch = null;
        _glitchAt = -1;
        Clear();
    }
}
