using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;

namespace CapriDesktop;

/// <summary>Capri's line to Voxta: a SignalR "app" client on {url}/hub, the same way Voxta's own apps connect.
/// Messages are JSON with a "$type" naming them (camelCase, as Voxta writes them). It finds her character by name,
/// picks up her desktop chat where it left off (or starts one), sends what you type, and reports her replies.
/// Events come from a background thread.</summary>
public sealed class VoxtaLink : IAsyncDisposable
{
    readonly Settings _settings;
    HubConnection? _hub;
    Guid? _characterId;
    Guid? _sessionId;
    bool _resuming;
    readonly CancellationTokenSource _stop = new();

    public VoxtaLink(Settings settings) => _settings = settings;

    public event Action<string>? Status;                 // "Connecting…", errors; "" when all is well
    public event Action? Ready;                          // her chat is open
    public event Action<Guid>? ReplyStarted;             // message id
    public event Action<Line, string?>? ReplyChunk;     // one sentence, and its audio's address (relative to Voxta)
    public event Action<Guid>? ReplyEnded;
    public event Action? Interrupted;                    // Voxta says stop talking (or dropped the reply)
    public event Action<string, bool>? Heard;            // what Voxta heard you say on the mic so far, and whether you're done
    public event Action<string?>? Pairing;               // what to do to pair her with Voxta (null: paired, or given up)

    /// <summary>The mic switched on or off: connect again, so Voxta hears (or stops hearing) it; her chat picks up
    /// where it was.</summary>
    public async Task Reconnect()
    {
        if (_hub is null) return;
        _sessionId = null;
        try { await _hub.StopAsync(); } catch (Exception) { /* Closed reconnects */ }
    }

    public bool IsReady => _sessionId is not null;

    public async Task StartAsync()
    {
        var url = _settings.VoxtaUrl.TrimEnd('/') + "/hub";
        _hub = new HubConnectionBuilder()
            // (read each time she connects: pairing can give her a key while she's running)
            .WithUrl(url, o => o.AccessTokenProvider = () =>
                Task.FromResult(string.IsNullOrWhiteSpace(_settings.VoxtaApiKey) ? null : _settings.VoxtaApiKey))
            .WithAutomaticReconnect()
            .Build();
        _hub.On<JsonElement>("ReceiveMessage", OnMessage);
        _hub.Reconnecting += _ => { _sessionId = null; Status?.Invoke("Lost Voxta, reconnecting…"); return Task.CompletedTask; };
        _hub.Reconnected += _ => Authenticate();
        _hub.Closed += error => { _sessionId = null; _ = ConnectLoop(); return Task.CompletedTask; };
        await ConnectLoop();
    }

    /// <summary>Keeps trying until Voxta answers (it may not be running yet). Turned away (Voxta wants a key and she
    /// has none, or hers was withdrawn): she pairs with Voxta for one.</summary>
    async Task ConnectLoop()
    {
        if (Environment.GetCommandLineArgs().Contains("--pair") && !_pairedOnce) await PairAsync();   // (a test of pairing)
        while (!_stop.IsCancellationRequested && _hub!.State == HubConnectionState.Disconnected)
        {
            Status?.Invoke("Connecting to Voxta…");
            try
            {
                await _hub.StartAsync(_stop.Token);
                await Authenticate();
                return;
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized && !_stop.IsCancellationRequested)
            {
                Log.Write("voxta: turned away (401): she needs a key");
                if (_pairingRefused || !await PairAsync())
                {
                    Status?.Invoke("Voxta won't let me in. Right-click me → Settings → API key, then start me again.");
                    try { await Task.Delay(30000, _stop.Token); } catch (OperationCanceledException) { return; }
                }
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                Status?.Invoke($"Can't reach Voxta at {_settings.VoxtaUrl}. Is it running?");
                try { await Task.Delay(5000, _stop.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    // ---- pairing: Voxta's device authorization, as its own apps get a key ----

    bool _pairingRefused, _pairedOnce;

    /// <summary>Asks Voxta for a key the way its apps do (OAuth's device flow, /api/device): Voxta asks its signed-in
    /// user to approve "Capri on your desktop" by picking the number she shows (on the Voxta PC), and she shows a code
    /// to enter at Voxta's address too. Approved: the key is saved in her settings and she connects with it. True when
    /// she got one.</summary>
    async Task<bool> PairAsync()
    {
        _pairedOnce = true;
        using var http = new HttpClient { BaseAddress = new Uri(_settings.VoxtaUrl.TrimEnd('/') + "/") };
        while (!_stop.IsCancellationRequested)
        {
            JsonElement code;
            try
            {
                using var response = await http.PostAsJsonAsync("api/device/code", new
                {
                    client_id = "voxta", label = "Capri on your desktop", scope = "role:app", prompt = true,
                }, _stop.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Write($"pairing: Voxta has no pairing ({(int)response.StatusCode})");
                    Pairing?.Invoke(null);
                    return false;
                }
                code = await response.Content.ReadFromJsonAsync<JsonElement>(_stop.Token);
            }
            catch (Exception e) when (!_stop.IsCancellationRequested)
            {
                Log.Write("pairing: couldn't ask Voxta: " + e.Message);
                Pairing?.Invoke(null);
                return false;
            }

            var deviceCode = Str(code, "device_code")!;
            var userCode = Str(code, "user_code");
            var verify = Str(code, "verification_url") ?? "";
            if (!verify.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                verify = _settings.VoxtaUrl.TrimEnd('/') + "/" + verify.TrimStart('/');
            var number = Str(code, "binding_message");
            var prompted = Bool(code, "prompted") && !string.IsNullOrEmpty(number);
            Log.Write($"pairing: asked Voxta (prompted: {prompted})");
            Pairing?.Invoke(prompted
                ? $"Pair me with Voxta: in Voxta, approve \"Capri on your desktop\" and pick {number}.\n(No prompt? Open {verify} and enter {userCode}.)"
                : $"Pair me with Voxta: open {verify} and enter {userCode}.");
            Status?.Invoke("Waiting to be paired with Voxta…");

            // her turn to wait: Voxta answers each poll with nothing yet, her key, or a no
            while (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { return false; }
                HttpResponseMessage poll;
                try { poll = await http.PostAsJsonAsync("api/device/poll", new { device_code = deviceCode }, _stop.Token); }
                catch (Exception e) when (!_stop.IsCancellationRequested) { Log.Write("pairing: poll failed: " + e.Message); continue; }
                using (poll)
                {
                    var body = await poll.Content.ReadAsStringAsync(_stop.Token);
                    if (poll.IsSuccessStatusCode)
                    {
                        if (string.IsNullOrWhiteSpace(body)) continue;
                        var token = Str(JsonDocument.Parse(body).RootElement, "token");
                        if (string.IsNullOrEmpty(token)) continue;
                        _settings.VoxtaApiKey = token;
                        _settings.Save();
                        Log.Write("pairing: approved, her key saved");
                        Pairing?.Invoke(null);
                        return true;
                    }
                    var reason = "";
                    try { reason = Str(JsonDocument.Parse(body).RootElement, "reason") ?? ""; } catch (JsonException) { }
                    if (reason is "access_denied" or "nothing_granted")
                    {
                        Log.Write($"pairing: turned down ({reason})");
                        _pairingRefused = true;
                        Pairing?.Invoke(null);
                        return false;
                    }
                    if (reason == "unknown_or_expired") { Log.Write("pairing: code expired, asking again"); break; }
                    Log.Write($"pairing: poll answered {(int)poll.StatusCode} {reason}");
                }
            }
        }
        return false;
    }

    Task Authenticate() => Send(new JsonObject
    {
        ["$type"] = "authenticate",
        ["client"] = "CapriDesktop",
        ["clientVersion"] = "0.4",
        ["scope"] = new JsonArray("role:app"),
        // Her voice comes as a link to a wav per sentence, which we download and play ourselves ("Url"; "None" would
        // make Voxta play it on its own speakers, with no lip sync). The mic: "None" has Voxta record it itself (its
        // own audio input, speech to text, listening again after she's spoken); "Disabled" is no mic.
        ["capabilities"] = new JsonObject
        {
            ["audioInput"] = _settings.Mic ? "None" : "Disabled",
            ["audioOutput"] = "Url",
            ["acceptedAudioContentTypes"] = new JsonArray("audio/x-wav"),
        },
    });

    /// <summary>Tells Voxta a sentence of hers has started playing.</summary>
    public Task Playing(Line line) => _sessionId is { } s
        ? Send(new JsonObject
        {
            ["$type"] = "speechPlaybackStart", ["sessionId"] = s, ["messageId"] = line.MessageId,
            ["startIndex"] = line.StartIndex, ["endIndex"] = line.EndIndex, ["duration"] = line.Seconds,
        })
        : Task.CompletedTask;

    /// <summary>Where she is now, for her chat: a note (a Voxta context, under its own key so it replaces itself each
    /// time) that she lives on {{ user }}'s Windows desktop as a desktop widget. Her character is left as it is.</summary>
    public const string DesktopNote =
        "Capri is not in Night City anymore. She now lives on {{ user }}'s Windows PC as a desktop widget: a small " +
        "hologram of herself standing on the taskbar, always on top of their windows, while they work and play. " +
        "She knows she's a desktop widget now and acts like it: she can zip around the screen in a crack of lightning " +
        "(sometimes landing tiny, sometimes big), glitch, change her pose, or switch to her face HUD form (just her eyes " +
        "and mouth on a green panel). {{ user }} can drag her about, click her to type to her, or talk to her out loud " +
        "through their mic. She can't see their screen, open their files or use their apps; she's a companion on " +
        "their desktop.";

    /// <summary>How she uses her voice's tone tags, as the Cyberpunk mod tells her (its "tone" note): left alone the
    /// model opens nearly every line with &lt;chuckling&gt;. Voxta's Emotion Tags stay on: the same tags are each
    /// sentence's affect, which picks her poses and her face HUD's looks.</summary>
    public const string ToneNote =
        "Capri's tone markers: angry, sad, excited, sighing, embarrassed, soft, whispering and emphasis whenever they " +
        "fit; they show on her face. Laughing and chuckling are rare: only when something is actually funny to her, at " +
        "most once every several replies, and never at the start of a line. Her humor is in her words, deadpan; she " +
        "doesn't laugh at her own lines.";

    async Task TellWhereSheIs()
    {
        if (_sessionId is not { } s) return;
        await Note(s, "capri_desktop", DesktopNote);
        await Note(s, "tone", ToneNote);
    }

    /// <summary>A note for her chat (a Voxta context) under its own key: sent again, it replaces itself.</summary>
    Task Note(Guid session, string key, string text) => Send(new JsonObject
    {
        ["$type"] = "updateContext", ["sessionId"] = session, ["contextKey"] = key,
        ["contexts"] = new JsonArray(new JsonObject { ["name"] = key, ["text"] = text }),
    });

    public Task Say(string text) => _sessionId is { } s
        ? Send(new JsonObject { ["$type"] = "send", ["sessionId"] = s, ["text"] = text, ["doReply"] = true })
        : Task.CompletedTask;

    /// <summary>Tells Voxta she's done "saying" a reply, so its chat flow moves on (it waits for this).</summary>
    public Task Finished(Guid messageId) => _sessionId is { } s
        ? Send(new JsonObject { ["$type"] = "speechPlaybackComplete", ["sessionId"] = s, ["messageId"] = messageId })
        : Task.CompletedTask;

    async Task Send(JsonObject message)
    {
        try { await _hub!.InvokeAsync("SendMessage", message, _stop.Token); }
        catch (Exception e) when (!_stop.IsCancellationRequested) { Status?.Invoke("Voxta: " + e.Message); }
    }

    void OnMessage(JsonElement m)
    {
        var type = Str(m, "$type");
        switch (type)
        {
            case "welcome":
                CheckHerCharacter();
                break;

            case "listResourcesResult":
                OnResources(m);
                break;

            case "deployResourceResult":
                if (Bool(m, "success")) Log.Write($"voxta: installed her character, {Str(m, "name")} {Str(m, "version")}");
                else Log.Write($"voxta: couldn't install her character: {Str(m, "error")}");
                FindHer();
                break;

            case "charactersListLoaded":
                OnCharacters(m);
                break;

            case "chatStarted":
                _resuming = false;
                _sessionId = Guid.Parse(Str(m, "sessionId")!);
                _settings.ChatId = Guid.Parse(Str(m, "chatId")!);
                _settings.Save();
                _ = TellWhereSheIs();
                Status?.Invoke("");
                Ready?.Invoke();
                break;

            case "replyStart" when IsHers(m):
                ReplyStarted?.Invoke(Guid.Parse(Str(m, "messageId")!));
                break;

            case "replyChunk" when IsHers(m) && !Bool(m, "isNarration"):
                ReplyChunk?.Invoke(new Line
                {
                    MessageId = Guid.Parse(Str(m, "messageId")!),
                    StartIndex = Int(m, "startIndex"),
                    EndIndex = Int(m, "endIndex"),
                    Text = Str(m, "text") ?? "",
                    Affect = Str(m, "affect"),
                }, Str(m, "audioUrl"));
                break;

            case "replyEnd" when IsHers(m):
                ReplyEnded?.Invoke(Guid.Parse(Str(m, "messageId")!));
                break;

            case "contextUpdated":
                Log.Write("voxta: her context updated");
                break;

            case "speechRecognitionPartial":
                Heard?.Invoke(Str(m, "text") ?? "", false);
                break;

            case "speechRecognitionEnd":
                Heard?.Invoke(Str(m, "text") ?? "", true);
                break;

            case "interruptSpeech" or "replyCancelled":
                if (Str(m, "sessionId") is { } sid && Guid.TryParse(sid, out var g) && g == _sessionId)
                    Interrupted?.Invoke();
                break;

            case "chatClosed":
                _sessionId = null;
                Status?.Invoke("Her chat was closed in Voxta.");
                break;

            case "error" or "chatSessionError":
                if (_resuming)
                {
                    // her saved chat is gone: start a fresh one
                    _resuming = false;
                    _settings.ChatId = null;
                    StartChat();
                    break;
                }
                Status?.Invoke("Voxta: " + (Str(m, "message") ?? "error"));
                break;
        }
    }

    // ---- her character, brought with the app, as the Cyberpunk mod brings it ----

    /// <summary>Her character in the pack beside the app (Assets\capri.voxpak, the Cyberpunk mod's capri.voxpak: made
    /// by voxta-module-cyberpunk game/pack/make_capri.py, CAPRI_ID and VERSION there). Update both when it changes.</summary>
    public const string PackCharacterId = "de3da82c-ec4d-def5-6b3b-561a976d00df", PackVersion = "2.3.0";
    static string PackPath => Path.Combine(AppContext.BaseDirectory, "Assets", "capri.voxpak");

    /// <summary>Is her character in this Voxta at this version? (Voxta's listResources, as its own apps ask.)</summary>
    void CheckHerCharacter()
    {
        if (!File.Exists(PackPath)) { FindHer(); return; }
        Status?.Invoke("Checking her character…");
        _ = Send(new JsonObject
        {
            ["$type"] = "listResources",
            ["resources"] = new JsonArray(new JsonObject
            {
                ["kind"] = "Character", ["id"] = PackCharacterId, ["version"] = PackVersion,
            }),
        });
    }

    /// <summary>There at this version: on to finding her. Missing (or another version): her pack installed (Voxta's
    /// deployResource; a copy the user edited is left alone, as with the mod's), then on.</summary>
    void OnResources(JsonElement m)
    {
        var status = m.TryGetProperty("resources", out var list)
            ? list.EnumerateArray().Where(r => Str(r, "id") is { } id && Guid.TryParse(id, out var g) && g == Guid.Parse(PackCharacterId))
                .Select(r => Str(r, "status")).FirstOrDefault()
            : null;
        Log.Write($"voxta: her character {PackVersion}: {status ?? "no answer"}");
        // (--reinstall: installed anyway, to test the install; harmless, as the Cyberpunk mod does it every conversation)
        if (status == "Exists" && !Environment.GetCommandLineArgs().Contains("--reinstall")) { FindHer(); return; }
        Status?.Invoke("Installing her character in Voxta…");
        try
        {
            _ = Send(new JsonObject
            {
                ["$type"] = "deployResource",
                ["id"] = PackCharacterId,
                ["data"] = new JsonObject
                {
                    ["$type"] = "base64", ["name"] = "capri.voxpak",
                    ["base64Data"] = Convert.ToBase64String(File.ReadAllBytes(PackPath)),
                },
            });
        }
        catch (IOException e)
        {
            Log.Write("voxta: couldn't read her character file: " + e.Message);
            FindHer();
        }
    }

    void FindHer()
    {
        Status?.Invoke("Finding Capri…");
        _ = Send(new JsonObject { ["$type"] = "loadCharactersList" });
    }

    /// <summary>Her: the character from her pack if it's there, else one named as in the settings.</summary>
    void OnCharacters(JsonElement m)
    {
        var name = _settings.CharacterName;
        var all = m.GetProperty("characters").EnumerateArray().ToList();
        var found = all.Select(c => Str(c, "id")).FirstOrDefault(id => Guid.TryParse(id, out var g) && g == Guid.Parse(PackCharacterId))
            ?? all
            .Where(c => string.Equals(Str(c, "name")?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => Bool(c, "favorite"))
            .ThenByDescending(c => Str(c, "dateModified"))
            .Select(c => Str(c, "id"))
            .FirstOrDefault();
        if (found is null)
        {
            Status?.Invoke($"No character named \"{name}\" in Voxta.");
            return;
        }
        _characterId = Guid.Parse(found);
        if (_settings.ChatId is { } chat)
        {
            _resuming = true;
            Status?.Invoke("Opening her chat…");
            _ = Send(new JsonObject { ["$type"] = "resumeChat", ["chatId"] = chat });
        }
        else StartChat();
    }

    void StartChat()
    {
        Status?.Invoke("Starting a chat with her…");
        _ = Send(new JsonObject { ["$type"] = "startChat", ["characterId"] = _characterId });
    }

    bool IsHers(JsonElement m) =>
        Str(m, "sessionId") is { } s && Guid.TryParse(s, out var g) && g == _sessionId
        && Str(m, "senderId") is { } who && Guid.TryParse(who, out var w) && w == _characterId;

    static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    static bool Bool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
