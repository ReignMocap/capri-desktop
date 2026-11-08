using System.Text.RegularExpressions;

namespace CapriDesktop;

/// <summary>Which picture she says a sentence in, the game's rules (capri.lua, "Her poses"):
///   1. gesture words in it (hi / bye: wave, "look", "over there": point, "nice", "good job": thumbs, hacking talk:
///      cast, "peace": peace), else
///   2. how she feels (the sentence's mood word from Voxta), if there's a picture for it, else
///   3. calm talking: her idle with her mouth moving, or one of its family's gestures; now and then another family.
/// Never the same picture twice in a row when there's another. A picture with no mouth-open version is a flash:
/// shown for a moment at the sentence's start, then a calm one. Her back to you, looking over her shoulder (back_), or
/// side on (side_): now and then on a calm sentence or a quiet stretch. Quiet, she stands in her family's idle and
/// moves to another family's every 20-40 s (when <see cref="ChangesOnHerOwn"/>).</summary>
public sealed class PosePicker
{
    public const double FlashSeconds = 0.8;
    const double OtherFamily = 0.2;
    const double BackChance = 0.12, SideChance = 0.05;
    static readonly (double, double) NewFamilyQuiet = (20, 40);

    // a mood word -> the kinds of picture that show it, best first (the game's FEELING)
    static readonly Dictionary<string, string[]> Feeling = new(StringComparer.OrdinalIgnoreCase)
    {
        ["happy"] = ["happy", "peace", "thumbs", "love"],
        ["chuckling"] = ["laugh", "happy"],
        // her Voxta voice (Cappi) also tags lines with these; the game's list doesn't have them
        ["laughing"] = ["laugh", "happy"],
        ["excited"] = ["happy", "peace", "thumbs", "love"],
        ["flirty"] = ["flirty", "love"],
        ["soft"] = ["love"],
        ["angry"] = ["angry", "annoyed"],
        ["annoyed"] = ["annoyed", "angry"],
        ["sighing"] = ["annoyed"],
        ["sad"] = ["sad"],
        ["surprised"] = ["surprised"],
        ["afraid"] = ["surprised", "sad"],
        ["shaky"] = ["surprised", "sad"],
        ["embarrassed"] = ["embarrassed", "think"],
    };

    // on the sentence lowercased with spaces round it (the game's GESTURES, as regexes)
    static readonly (string Kind, Regex Words)[] Gestures =
    [
        ("wave", new(@"^ (hey|hi|hello|heya|hiya|yo) ")),
        ("wave", new(@" bye[\s\p{P}]| see you| see ya| catch you later| later, | gotta go")),
        ("cast", new(@" hack| breach| daemon| quickhack| netrun| jack in| jacking in| ice[\s\p{P}]| firewall| upload| virus| short circuit| fry ")),
        ("point", new(@" look at | look!| look,| over there| right there| that one| see that| this way| that way| there!")),
        ("thumbs", new(@" nice[\s\p{P}]| good job| well done| nailed it| awesome| you got it| perfect[\s\p{P}]| deal[\s\p{P}]| sounds good| way to go")),
        ("peace", new(@" peace[\s\p{P}]| peace out")),
    ];

    readonly Random _rng = new();
    readonly Dictionary<string, List<Pose>> _kinds = [];
    readonly Dictionary<int, Pose> _idleOf = [];

    public PosePicker(IEnumerable<Pose> poses)
    {
        foreach (var p in poses)
        {
            var kind = p.Name.Split('_')[0];
            if (!_kinds.TryGetValue(kind, out var list)) _kinds[kind] = list = [];
            list.Add(p);
            if (p.IsIdle) _idleOf[p.Family] = p;
        }
    }

    /// <summary>Her family now: the idle she stands in, and whose gestures she talks with.</summary>
    public int Family { get; set; }

    public Pose Idle => _idleOf.GetValueOrDefault(Family) ?? _idleOf.Values.First();

    /// <summary>Quiet, she settles into another idle now and then on her own.</summary>
    public bool ChangesOnHerOwn { get; set; } = true;

    Pose? _quietTurned;       // a quiet stretch with her back turned (or side on) instead of her idle
    double _nextFamilyAt = -1;

    /// <summary>Landing from a zap: up in another family's idle (now and then turned away), as in the game.</summary>
    public void NewFamily()
    {
        Family = OtherFamilyThan(Family);
        _nextFamilyAt = -1;
        _quietTurned = BackOrSide(null);
    }

    /// <summary>You picked her idle: that one, facing you.</summary>
    public void SetFamily(int family)
    {
        Family = family;
        _quietTurned = null;
        _nextFamilyAt = -1;
    }

    /// <summary>She's talking or thinking: the quiet stretch starts over after.</summary>
    public void Busy()
    {
        _quietTurned = null;
        _nextFamilyAt = -1;
    }

    /// <summary>Her picture while quiet: her family's idle, after a while another family's (now and then with her
    /// back turned or side on instead).</summary>
    public Pose Quiet(double now)
    {
        if (ChangesOnHerOwn)
        {
            if (_nextFamilyAt < 0)
                _nextFamilyAt = now + NewFamilyQuiet.Item1 + _rng.NextDouble() * (NewFamilyQuiet.Item2 - NewFamilyQuiet.Item1);
            else if (now >= _nextFamilyAt)
            {
                Family = OtherFamilyThan(Family);
                _nextFamilyAt = -1;
                _quietTurned = BackOrSide(null);
                Log.Write($"quiet: now in {(_quietTurned ?? Idle).Name}");
            }
        }
        return _quietTurned ?? Idle;
    }

    /// <summary>Now and then: her back turned, or side on (or null).</summary>
    Pose? BackOrSide(Pose? now)
    {
        var r = _rng.NextDouble();
        if (r < BackChance) return Pick("back", now);
        if (r < BackChance + SideChance) return Pick("side", now);
        return null;
    }
    public Pose? Think(Pose? now) => Pick("think", now);

    /// <summary>The picture for a sentence, and the flash before it (or null).</summary>
    public (Pose Pose, Pose? Flash) ForLine(string text, string? affect, Pose? now)
    {
        var kind = GestureFor(text);
        if (kind is null && affect is not null && Feeling.TryGetValue(affect, out var kinds))
            kind = kinds.FirstOrDefault(_kinds.ContainsKey);
        if (kind is not null && Pick(kind, now) is { } p)
            return p.Mouth is not null ? (p, null) : (Calm(now), p);
        return (Calm(now), null);
    }

    string? GestureFor(string text)
    {
        var t = " " + text.ToLowerInvariant() + " ";
        return Gestures.Where(g => _kinds.ContainsKey(g.Kind) && g.Words.IsMatch(t)).Select(g => g.Kind).FirstOrDefault();
    }

    /// <summary>Her idle (talking in it) or one of its family's gestures; now and then another family's, and always
    /// another's when hers has nothing but the picture she's in.</summary>
    Pose Calm(Pose? now)
    {
        if (BackOrSide(now) is { Mouth: not null } turned) return turned;
        if (_rng.NextDouble() < OtherFamily) Family = OtherFamilyThan(Family);
        var choices = Talkable(Family);
        if (choices.Count == 0 || (choices.Count == 1 && choices[0] == now))
        {
            Family = OtherFamilyThan(Family);
            choices = Talkable(Family);
        }
        return PickFrom(choices, now) ?? Idle;
    }

    List<Pose> Talkable(int family) =>
        _kinds.Where(k => k.Key is "idle" or "talk").SelectMany(k => k.Value)
            .Where(p => p.Family == family && p.Mouth is not null).ToList();

    int OtherFamilyThan(int family)
    {
        var others = _idleOf.Keys.Where(f => f != family).ToList();
        return others.Count == 0 ? family : others[_rng.Next(others.Count)];
    }

    Pose? Pick(string kind, Pose? now) => _kinds.TryGetValue(kind, out var list) ? PickFrom(list, now) : null;

    Pose? PickFrom(List<Pose> list, Pose? now)
    {
        if (list.Count == 0) return null;
        var choices = list.Count > 1 ? list.Where(p => p != now).ToList() : list;
        return choices[_rng.Next(choices.Count)];
    }
}
