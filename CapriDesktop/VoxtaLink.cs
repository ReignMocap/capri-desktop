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

    public bool IsReady => _sessionId is not null;

    public async Task StartAsync()
    {
        var url = _settings.VoxtaUrl.TrimEnd('/') + "/hub";
        _hub = new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                if (!string.IsNullOrWhiteSpace(_settings.VoxtaApiKey))
                    o.AccessTokenProvider = () => Task.FromResult<string?>(_settings.VoxtaApiKey);
            })
            .WithAutomaticReconnect()
            .Build();
        _hub.On<JsonElement>("ReceiveMessage", OnMessage);
        _hub.Reconnecting += _ => { _sessionId = null; Status?.Invoke("Lost Voxta, reconnecting…"); return Task.CompletedTask; };
        _hub.Reconnected += _ => Authenticate();
        _hub.Closed += error => { _sessionId = null; _ = ConnectLoop(); return Task.CompletedTask; };
        await ConnectLoop();
    }

    /// <summary>Keeps trying until Voxta answers (it may not be running yet).</summary>
    async Task ConnectLoop()
    {
        while (!_stop.IsCancellationRequested && _hub!.State == HubConnectionState.Disconnected)
        {
            Status?.Invoke("Connecting to Voxta…");
            try
            {
                await _hub.StartAsync(_stop.Token);
                await Authenticate();
                return;
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                Status?.Invoke($"Can't reach Voxta at {_settings.VoxtaUrl}. Is it running?");
                try { await Task.Delay(5000, _stop.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    Task Authenticate() => Send(new JsonObject
    {
        ["$type"] = "authenticate",
        ["client"] = "CapriDesktop",
        ["clientVersion"] = "0.3",
        ["scope"] = new JsonArray("role:app"),
        // Her voice comes as a link to a wav per sentence, which we download and play ourselves ("Url"; "None" would
        // make Voxta play it on its own speakers, with no lip sync). No mic yet ("None" would make Voxta record it).
        ["capabilities"] = new JsonObject
        {
            ["audioInput"] = "Disabled",
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
                Status?.Invoke("Finding Capri…");
                _ = Send(new JsonObject { ["$type"] = "loadCharactersList" });
                break;

            case "charactersListLoaded":
                OnCharacters(m);
                break;

            case "chatStarted":
                _resuming = false;
                _sessionId = Guid.Parse(Str(m, "sessionId")!);
                _settings.ChatId = Guid.Parse(Str(m, "chatId")!);
                _settings.Save();
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

    void OnCharacters(JsonElement m)
    {
        var name = _settings.CharacterName;
        var found = m.GetProperty("characters").EnumerateArray()
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
