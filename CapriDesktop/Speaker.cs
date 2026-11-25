using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using NAudio.Wave;

namespace CapriDesktop;

/// <summary>One sentence of hers: its text, mood word, and (once downloaded) its voice and mouth track.</summary>
public sealed class Line
{
    public required Guid MessageId { get; init; }
    public required int StartIndex { get; init; }
    public required int EndIndex { get; init; }
    public required string Text { get; init; }
    public string? Affect { get; init; }
    public Task<Voice?>? Download { get; set; }
    public Voice? Voice { get; set; }
    /// <summary>Seconds it lasts: its audio's length, or a reading pace when there's no voice.</summary>
    public double Seconds { get; set; }
}

/// <summary>A sentence's audio: the wav, its length, and its mouth track (MouthTrack: 5 hex digits per 20 ms).</summary>
public sealed record Voice(byte[] Wav, double Seconds, string? Mouth);

/// <summary>Plays her sentences one after another as Voxta sends them. Each one's audio starts downloading the moment
/// it arrives (Voxta makes the voice when it's asked for), so the next is usually ready when the last one ends.
/// Everything here runs on the UI thread through <see cref="Pump"/>, called every frame.</summary>
public sealed class Speaker : IDisposable
{
    const double TextOnlyCharsPerSecond = 16;   // no voice: how long a sentence "takes"

    readonly HttpClient _http;
    readonly Queue<Line> _queue = new();
    readonly HashSet<Guid> _ended = [];         // replies Voxta has finished sending
    readonly Stopwatch _clock = new();
    WaveOut? _out;
    volatile bool _outStopped;

    public Speaker(Settings settings)
    {
        _http = new HttpClient { BaseAddress = new Uri(settings.VoxtaUrl.TrimEnd('/') + "/") };
        Settings = settings;
    }

    Settings Settings { get; }

    public Line? Current { get; private set; }
    public double Elapsed => _clock.Elapsed.TotalSeconds;
    public bool Busy => Current is not null || _queue.Count > 0;

    public event Action<Line>? LineStarted;
    public event Action<Line>? LineEnded;
    public event Action<Guid>? MessageFinished;   // all of a reply has been said

    public void Add(Line line, string? audioUrl)
    {
        if (Settings.Voice && !string.IsNullOrEmpty(audioUrl)) line.Download = Fetch(audioUrl);
        _queue.Enqueue(line);
    }

    public void ReplyEnded(Guid messageId)
    {
        _ended.Add(messageId);
        if (!Saying(messageId)) Finish(messageId);
    }

    /// <summary>Stop talking now (Voxta interrupted her, or you did): drop what's left, tell Voxta it's done.</summary>
    public void Interrupt()
    {
        StopAudio();
        var ids = _queue.Select(l => l.MessageId).ToList();
        if (Current is { } playing) ids.Add(playing.MessageId);
        ids = ids.Distinct().ToList();
        if (Current is { } c) LineEnded?.Invoke(c);
        Current = null;
        _queue.Clear();
        foreach (var id in ids) MessageFinished?.Invoke(id);
        _ended.Clear();
    }

    /// <summary>The mouth channels now (jaw, round, wide, closed, bite; 0..1), or null with no voice playing.</summary>
    public double[]? Mouth()
    {
        var m = Current?.Voice?.Mouth;
        if (m is null) return null;
        var frame = (int)(Elapsed * MouthTrack.FramesPerSecond);
        if (frame * 5 + 5 > m.Length) return [0, 0, 0, 0, 0];
        var v = new double[5];
        for (var i = 0; i < 5; i++) v[i] = Convert.ToInt32(m[frame * 5 + i].ToString(), 16) / 15.0;
        return v;
    }

    public void Pump()
    {
        if (Current is { } line)
        {
            var done = line.Voice is not null ? _outStopped : Elapsed >= line.Seconds;
            if (!done) return;
            StopAudio();
            Current = null;
            LineEnded?.Invoke(line);
            if (_ended.Contains(line.MessageId) && !Saying(line.MessageId)) Finish(line.MessageId);
        }
        if (_queue.Count == 0) return;
        var next = _queue.Peek();
        if (next.Download is { IsCompleted: false }) return;   // still coming
        _queue.Dequeue();
        next.Voice = next.Download?.Result;
        next.Seconds = next.Voice?.Seconds ?? Math.Max(1.2, next.Text.Length / TextOnlyCharsPerSecond);
        Current = next;
        if (next.Voice is not null) Play(next.Voice);
        _clock.Restart();
        Log.Write($"says {next.Seconds:0.0}s voice={(next.Voice is not null ? "yes" : "no")} " +
                  $"mouth={(next.Voice?.Mouth is { } m ? m.Length / 5 + " frames" : "none")} mood={next.Affect ?? "-"}: {next.Text}");
        LineStarted?.Invoke(next);
    }

    bool Saying(Guid messageId) => Current?.MessageId == messageId || _queue.Any(l => l.MessageId == messageId);

    void Finish(Guid messageId)
    {
        _ended.Remove(messageId);
        MessageFinished?.Invoke(messageId);
    }

    async Task<Voice?> Fetch(string url)
    {
        try
        {
            // (her key as it is now: pairing can give her one while she's running)
            using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimStart('/'));
            if (!string.IsNullOrWhiteSpace(Settings.VoxtaApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.VoxtaApiKey);
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var wav = await response.Content.ReadAsByteArrayAsync();
            return await Task.Run(() => Decode(wav));
        }
        catch (Exception e)
        {
            Log.Write("voice download failed: " + e.Message);
            return null;    // she says it without a voice
        }
    }

    /// <summary>Its length and mouth track, from the samples (mixed down to one channel).</summary>
    static Voice? Decode(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var provider = reader.ToSampleProvider();
        var channels = provider.WaveFormat.Channels;
        var rate = provider.WaveFormat.SampleRate;
        var all = new List<double>();
        var buffer = new float[rate * channels];
        int got;
        while ((got = provider.Read(buffer.AsSpan())) > 0)
            for (var i = 0; i + channels <= got; i += channels)
            {
                double sum = 0;
                for (var c = 0; c < channels; c++) sum += buffer[i + c];
                all.Add(sum / channels);
            }
        if (all.Count == 0) return null;
        return new Voice(wav, (double)all.Count / rate, MouthTrack.FromSamples(all.ToArray(), rate));
    }

    void Play(Voice voice)
    {
        _outStopped = false;
        var reader = new WaveFileReader(new MemoryStream(voice.Wav));
        _out = new WaveOut();
        _out.PlaybackStopped += (_, _) => { _outStopped = true; reader.Dispose(); };
        _out.Init(reader);
        _out.Volume = (float)Math.Clamp(Settings.Volume, 0, 1);
        _out.Play();
    }

    void StopAudio()
    {
        if (_out is null) return;
        var o = _out;
        _out = null;
        o.Stop();
        o.Dispose();
    }

    public void Dispose()
    {
        StopAudio();
        _http.Dispose();
    }
}
