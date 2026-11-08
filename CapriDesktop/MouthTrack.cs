namespace CapriDesktop;

/// <summary>
/// Copied from the Cyberpunk module (Voxta.Modules.Cyberpunk/Services/MouthTrack.cs) with the analysis unchanged; here it
/// takes samples (decoded by NAudio) instead of reading a wav file.
///
/// Lip sync from the audio itself: a line's wav, read in 20 ms frames, becomes five mouth channels the game's patched
/// face graph plays on the speaker (VoxtaLipsync.archive): jaw (how open), round (oo/oh/w), wide (ee/i/s), closed
/// (m/b/p) and bite (f/v).
///
/// - Loudness opens the jaw.
/// - Where a vowel's energy sits picks the lips: dark (energy low, under ~1.2 kHz) rounds them, bright (energy up in the
///   2–3 kHz band) spreads them.
/// - Hiss (energy above 4 kHz) is a fricative: strong is s/sh (teeth together, lips wide), weak is f/v (lip under teeth).
/// - A short dip inside speech is a stop (m, b, p): lips together.
///
/// Encoded for the game as five hex digits (0–f) per frame, at <see cref="FramesPerSecond"/>.
/// </summary>
public static class MouthTrack
{
    public const int FramesPerSecond = 50;

    // How far the jaw opens at the loudest syllable (0–1 of the face's full jaw open). 0.75 looked exaggerated.
    public const double JawScale = 0.5;

    /// <summary>samples: mono, -1..1, `rate` per second.</summary>
    public static string? FromSamples(double[] samples, int rate)
    {
        if (rate < FramesPerSecond || samples.Length < rate / 10) return null;
        var hop = rate / FramesPerSecond;
        var frames = samples.Length / hop;
        const int fftSize = 1024;
        var window = new double[fftSize];
        for (var i = 0; i < fftSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (fftSize - 1));

        var level = new double[frames];
        var dark = new double[frames];
        var bright = new double[frames];
        var hiss = new double[frames];
        var re = new double[fftSize];
        var im = new double[fftSize];
        var binHz = (double)rate / fftSize;
        for (var f = 0; f < frames; f++)
        {
            var centre = f * hop + hop / 2;
            double sum = 0;
            for (var i = 0; i < fftSize; i++)
            {
                var idx = centre - fftSize / 2 + i;
                var s = idx >= 0 && idx < samples.Length ? samples[idx] : 0;
                if (i >= fftSize / 2 - hop / 2 && i < fftSize / 2 + hop / 2) sum += s * s;
                re[i] = s * window[i];
                im[i] = 0;
            }
            level[f] = 10 * Math.Log10(sum / hop + 1e-10);
            Fft(re, im);
            double low = 0, mid = 0, high = 0, total = 0;
            for (var b = 1; b < fftSize / 2; b++)
            {
                var hz = b * binHz;
                var e = re[b] * re[b] + im[b] * im[b];
                if (hz < 150) continue;
                total += e;
                if (hz < 1200) low += e;
                else if (hz < 3200) mid += e;
                else if (hz > 4000) high += e;
            }
            total += 1e-9;
            dark[f] = low / total;
            bright[f] = mid / total;
            hiss[f] = high / total;
        }

        // Loudness relative to the line: its loud parts are "open", 35 dB under them is silence.
        var sorted = level.OrderBy(x => x).ToArray();
        var peak = sorted[(int)(sorted.Length * 0.95)];
        var floor = peak - 30;
        var open = level.Select(l => Math.Clamp((l - floor) / 30.0, 0, 1)).ToArray();

        // Syllables: the jaw follows each one, open at its loud peak and dropping between them — loudness against the
        // quietest and loudest of the ~200 ms around it (a flat "loud" level held the jaw at one height all line long).
        var syllable = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (var k = Math.Max(0, f - 5); k <= Math.Min(frames - 1, f + 5); k++)
            {
                lo = Math.Min(lo, level[k]);
                hi = Math.Max(hi, level[k]);
            }
            syllable[f] = hi - lo < 3 ? 0.6 : Math.Clamp((level[f] - lo) / (hi - lo), 0, 1);
        }

        // Vowel colour against this speaker's own average in the line: the low band always carries most of the energy, so
        // a fixed threshold read nearly every frame as "oo" and pursed the lips all line long.
        var voiced = Enumerable.Range(0, frames).Where(f => open[f] > 0.3 && hiss[f] <= 0.45).ToArray();
        var balances = voiced.Select(f => bright[f] - dark[f]).ToArray();
        var mean = balances.Length > 0 ? balances.Average() : 0;
        var spread = balances.Length > 1 ? Math.Sqrt(balances.Select(b => (b - mean) * (b - mean)).Average()) : 1;
        if (spread < 1e-3) spread = 1;

        var jaw = new double[frames];
        var round = new double[frames];
        var wide = new double[frames];
        var closed = new double[frames];
        var bite = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            var o = open[f];
            var fricative = hiss[f] > 0.45 && o > 0.15;
            if (fricative)
            {
                // s / sh: teeth together, lips spread; weak hiss: f / v, lower lip under the teeth.
                var strong = o > 0.45;
                jaw[f] = 0.06;
                wide[f] = strong ? 0.6 : 0.2;
                bite[f] = strong ? 0 : 0.7;
                continue;
            }
            // Talking opens the jaw only part way (the jaw channel at 1 is a yawn), most on each syllable's peak.
            jaw[f] = o * (0.35 + 0.65 * syllable[f]) * JawScale;
            if (o > 0.3)
            {
                // Vowel colour: darker than this speaker's usual (oo, oh) rounds, brighter (ee, eh) spreads.
                var z = (bright[f] - dark[f] - mean) / spread;
                round[f] = Math.Clamp((-z - 0.15) / 1.1, 0, 1) * 0.8;
                wide[f] = Math.Clamp((z - 0.35) / 1.3, 0, 1) * 0.8;
            }
        }
        // Stops: a short dip (≤ 120 ms) with speech on both sides is lips together (m, b, p).
        for (var f = 0; f < frames; f++)
        {
            if (open[f] > 0.2) continue;
            int a = f, b = f;
            while (a > 0 && open[a] <= 0.2) a--;
            while (b < frames - 1 && open[b] <= 0.2) b++;
            if (b - a <= 6 && open[a] > 0.35 && open[b] > 0.35)
                closed[f] = 0.9;
        }

        // Smooth (faces don't snap) and lead the sound a little (lips move before you hear them).
        Smooth(jaw, 0.55, 0.35);
        Smooth(round, 0.4, 0.25);
        Smooth(wide, 0.4, 0.25);
        Smooth(closed, 0.7, 0.4);
        Smooth(bite, 0.6, 0.35);
        const int lead = 2;  // 40 ms
        var chars = new char[frames * 5];
        for (var f = 0; f < frames; f++)
        {
            var src = Math.Min(frames - 1, f + lead);
            chars[f * 5] = Hex(jaw[src]);
            chars[f * 5 + 1] = Hex(round[src]);
            chars[f * 5 + 2] = Hex(wide[src]);
            chars[f * 5 + 3] = Hex(closed[src]);
            chars[f * 5 + 4] = Hex(bite[src]);
        }
        return new string(chars);
    }

    private static char Hex(double v) => "0123456789abcdef"[(int)Math.Round(Math.Clamp(v, 0, 1) * 15)];

    // Rises at `attack`, falls at `release` of the way per frame.
    private static void Smooth(double[] v, double attack, double release)
    {
        var y = 0.0;
        for (var i = 0; i < v.Length; i++)
        {
            y += (v[i] - y) * (v[i] > y ? attack : release);
            v[i] = y;
        }
    }

    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

}
