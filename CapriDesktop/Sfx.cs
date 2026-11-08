using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CapriDesktop;

/// <summary>Her sound effects (zaps, glitches): our own wavs, copied from the Cyberpunk module by
/// tools/prep_face.py into the art folder's sfx\. Each play is its own little player, so they can overlap.</summary>
public sealed class Sfx(Settings settings)
{
    // as in the game: effects play at this gain unless a call says otherwise
    public const double Default = 0.4;
    readonly Dictionary<string, byte[]?> _cache = [];

    public void Play(string name, double gain = Default)
    {
        if (!settings.Effects) return;
        var wav = Load(name);
        if (wav is null) return;
        try
        {
            var reader = new WaveFileReader(new MemoryStream(wav));
            var louder = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = (float)(gain * settings.SfxVolume) };
            var output = new WaveOut();
            output.PlaybackStopped += (_, _) => { output.Dispose(); reader.Dispose(); };
            output.Init(louder.ToWaveProvider());
            output.Play();
        }
        catch (Exception e) { Log.Write($"sound {name}: {e.Message}"); }
    }

    byte[]? Load(string name)
    {
        if (_cache.TryGetValue(name, out var got)) return got;
        var path = Path.Combine(settings.ArtFolder, "sfx", name + ".wav");
        got = File.Exists(path) ? File.ReadAllBytes(path) : null;
        _cache[name] = got;
        return got;
    }
}
