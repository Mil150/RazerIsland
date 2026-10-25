using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RazerIsland.Core;

namespace RazerIsland.Discord;

/// <summary>
/// Стан мікрофона Discord через локальний RPC (іменований канал discord-ipc-N) у фоновому потоці.
/// UI читає стан опитуванням (<see cref="Snapshot"/>), як і в PowerShell-версії.
/// </summary>
public sealed class DiscordMicService : IDisposable
{
    public const string RedirectUri = "http://localhost";

    public sealed record State(string Status, string LastError, bool Connected, bool Mute, bool Deaf, int Version, bool HasClient);

    private readonly object _gate = new();
    private string _clientId, _clientSecret, _token, _refresh;
    private string _status = "", _lastError = "";
    private bool _mute, _deaf, _connected, _wantAuthorize, _tokenChanged;
    private int _version;

    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _conn;
    private Task? _worker;

    public DiscordMicService(Settings s)
    {
        _clientId = s.DiscordClientId; _clientSecret = s.DiscordClientSecret;
        _token = s.DiscordToken; _refresh = s.DiscordRefresh;
    }

    public void Start() => _worker = Task.Run(() => RunAsync(_stop.Token));

    public State Snapshot()
    {
        lock (_gate) return new State(_status, _lastError, _connected, _mute, _deaf, _version, _clientId.Length > 0);
    }

    /// <summary>Нові токени після авторизації/оновлення (один раз), щоб зберегти їх у налаштуваннях.</summary>
    public (string Token, string Refresh)? TakeChangedTokens()
    {
        lock (_gate)
        {
            if (!_tokenChanged) return null;
            _tokenChanged = false;
            return (_token, _refresh);
        }
    }

    /// <summary>Підключити з новими Client ID/Secret і запросити авторизацію в Discord.</summary>
    public void Connect(string clientId, string clientSecret)
    {
        lock (_gate)
        {
            _clientId = clientId; _clientSecret = clientSecret;
            _token = ""; _refresh = ""; _lastError = ""; _status = "";
            _wantAuthorize = true;
        }
        DropConnection();
    }

    public void Disconnect()
    {
        lock (_gate) { _clientId = ""; _clientSecret = ""; _token = ""; _refresh = ""; }
        DropConnection();
    }

    private void DropConnection()
    {
        CancellationTokenSource? c;
        lock (_gate) c = _conn;
        try { c?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void SetStatus(string s) { lock (_gate) _status = s; }
    private string GetStatus() { lock (_gate) return _status; }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            string clientId;
            lock (_gate) clientId = _clientId;
            if (clientId.Length == 0)
            {
                SetStatus("notconfigured");
                await Wait(2, stop);
                continue;
            }

            NamedPipeClientStream? pipe = null;
            using var conn = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_gate) _conn = conn;
            try
            {
                for (int i = 0; i < 10 && pipe is null; i++)
                {
                    var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                    try { await p.ConnectAsync(300, conn.Token); pipe = p; }
                    catch { p.Dispose(); if (conn.IsCancellationRequested) throw; }
                }
                if (pipe is null) { SetStatus("nodiscord"); await Wait(3, stop); continue; }

                await Session(pipe, clientId, conn.Token);
            }
            catch
            {
                var st = GetStatus();
                if (st is "ok" or "authorizing") SetStatus("nodiscord");
            }
            finally
            {
                lock (_gate) { _connected = false; _conn = null; }
                pipe?.Dispose();
            }

            var status = GetStatus();
            await Wait(status is "needauth" or "denied" or "autherror" ? 5 : 2, stop);
        }
    }

    // чекати seconds, але прокинутися раніше, якщо попросили авторизацію
    private async Task Wait(int seconds, CancellationToken stop)
    {
        for (int t = 0; t < seconds * 4 && !stop.IsCancellationRequested; t++)
        {
            lock (_gate) if (_wantAuthorize) return;
            try { await Task.Delay(250, stop); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task Session(NamedPipeClientStream pipe, string clientId, CancellationToken ct)
    {
        await SendFrame(pipe, 0, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, ct);
        await ReadJson(pipe, ct); // READY

        string token, refresh;
        lock (_gate) { token = _token; refresh = _refresh; }
        bool authed = false;
        if (token.Length > 0)
        {
            authed = await TryAuth(pipe, token, ct);
            if (!authed && refresh.Length > 0)
            {
                try
                {
                    await GetToken(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, ct);
                    lock (_gate) token = _token;
                    authed = await TryAuth(pipe, token, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        if (!authed)
        {
            bool want;
            lock (_gate) want = _wantAuthorize;
            if (!want) { SetStatus("needauth"); throw new InvalidOperationException("need auth"); }
            SetStatus("authorizing");
            await SendFrame(pipe, 1, new JsonObject
            {
                ["cmd"] = "AUTHORIZE",
                ["args"] = new JsonObject { ["client_id"] = clientId, ["scopes"] = new JsonArray("rpc", "rpc.voice.read") },
                ["nonce"] = Nonce(),
            }, ct);
            var r = await ReadJson(pipe, ct); // чекає, поки користувач натисне «Авторизувати» в Discord
            lock (_gate) _wantAuthorize = false;
            if (Str(r, "evt") == "ERROR")
            {
                lock (_gate) { _lastError = Str(r?["data"], "message"); _status = "denied"; }
                throw new InvalidOperationException("denied");
            }
            try
            {
                await GetToken(new() { ["grant_type"] = "authorization_code", ["code"] = Str(r?["data"], "code"), ["redirect_uri"] = RedirectUri }, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (_gate) { _lastError = "Не вдалося отримати токен: " + e.Message; _status = "autherror"; }
                throw;
            }
            lock (_gate) token = _token;
            if (!await TryAuth(pipe, token, ct))
            {
                lock (_gate) { _lastError = "Discord не прийняв токен."; _status = "autherror"; }
                throw new InvalidOperationException("auth");
            }
        }

        await SendFrame(pipe, 1, new JsonObject { ["cmd"] = "SUBSCRIBE", ["evt"] = "VOICE_SETTINGS_UPDATE", ["args"] = new JsonObject(), ["nonce"] = Nonce() }, ct);
        await SendFrame(pipe, 1, new JsonObject { ["cmd"] = "GET_VOICE_SETTINGS", ["args"] = new JsonObject(), ["nonce"] = Nonce() }, ct);
        SetStatus("ok");
        while (!ct.IsCancellationRequested)
        {
            var m = await ReadJson(pipe, ct);
            if ((Str(m, "cmd") == "GET_VOICE_SETTINGS" || Str(m, "evt") == "VOICE_SETTINGS_UPDATE")
                && m?["data"] is JsonObject data && data["mute"] is JsonValue mute)
            {
                lock (_gate)
                {
                    _mute = mute.GetValue<bool>();
                    _deaf = data["deaf"] is JsonValue deaf && deaf.GetValue<bool>();
                    _connected = true;
                    _version++;
                }
            }
        }
    }

    private async Task<bool> TryAuth(Stream pipe, string token, CancellationToken ct)
    {
        await SendFrame(pipe, 1, new JsonObject { ["cmd"] = "AUTHENTICATE", ["args"] = new JsonObject { ["access_token"] = token }, ["nonce"] = Nonce() }, ct);
        var r = await ReadJson(pipe, ct);
        return Str(r, "evt") != "ERROR";
    }

    private async Task GetToken(Dictionary<string, string> form, CancellationToken ct)
    {
        lock (_gate) { form["client_id"] = _clientId; form["client_secret"] = _clientSecret; }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.Client.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form), cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
        var j = JsonNode.Parse(body);
        lock (_gate)
        {
            _token = Str(j, "access_token");
            _refresh = Str(j, "refresh_token");
            _tokenChanged = true;
        }
    }

    // ---- кадри RPC: [op:int32][len:int32][json] ----
    private static async Task SendRaw(Stream pipe, int op, string json, CancellationToken ct)
    {
        var b = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + b.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(b.Length).CopyTo(frame, 4);
        b.CopyTo(frame, 8);
        await pipe.WriteAsync(frame, ct);
        await pipe.FlushAsync(ct);
    }

    private static Task SendFrame(Stream pipe, int op, JsonNode obj, CancellationToken ct) => SendRaw(pipe, op, obj.ToJsonString(), ct);

    private static async Task<byte[]> ReadExact(Stream pipe, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        await pipe.ReadExactlyAsync(buf, ct);
        return buf;
    }

    private static async Task<JsonNode?> ReadJson(Stream pipe, CancellationToken ct)
    {
        while (true)
        {
            var h = await ReadExact(pipe, 8, ct);
            int op = BitConverter.ToInt32(h, 0), len = BitConverter.ToInt32(h, 4);
            var json = len > 0 ? Encoding.UTF8.GetString(await ReadExact(pipe, len, ct)) : "";
            if (op == 3) { await SendRaw(pipe, 4, json, ct); continue; } // ping → pong
            if (op == 2) throw new IOException("Discord закрив з'єднання: " + json);
            return json.Length > 0 ? JsonNode.Parse(json) : null;
        }
    }

    private static string Str(JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string Nonce() => Guid.NewGuid().ToString();

    public void Dispose()
    {
        _stop.Cancel();
        DropConnection();
        try { _worker?.Wait(1000); } catch { }
        _stop.Dispose();
    }
}
