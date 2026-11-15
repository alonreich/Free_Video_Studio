using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

internal sealed record ChatGptSignInResult(bool Success, string Message, bool Cancelled = false);

internal static class ChatGptAuth
{
    internal const string Tag = "CHATGPTSIGNIN_01";
    internal const string CredentialMarker = "chatgpt-plan";
    internal const string Issuer = "https://auth.openai.com";
    internal const string AuthorizeEndpoint = Issuer + "/api/accounts/authorize";
    internal const string TokenEndpoint = Issuer + "/api/accounts/oauth/token";
    internal const string RevokeEndpoint = Issuer + "/api/accounts/oauth/revoke";
    internal const string JwksUri = Issuer + "/.well-known/jwks.json";
    internal const string Resource = "https://api.openai.com/v1";
    internal const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    internal const string PlanScope = "chatgpt.tokens.use.direct";
    internal const string FirstRegistrationClientId = "dynamic_agent_client";
    internal const string AppName = "Free Video Studio";
    internal const string ManageUsageUrl = "https://chatgpt.com/settings/usage";
    internal const string CallbackPath = "/auth/callback";
    internal const int PreferredPort = 1455;
    internal static readonly TimeSpan SignInWait = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(2);

    private static readonly string[] TerminalRefreshErrors =
    {
        "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired",
        "refresh_token_invalidated", "refresh_token_reused",
    };

    internal sealed class Session
    {
        public string HostId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string IdToken { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime ExpiresUtc { get; set; }
        public string Scope { get; set; } = string.Empty;
        public bool WelcomeShown { get; set; }

        public bool HasPlan => RefreshToken.Length > 0 && ScopeHas(Scope, PlanScope);
    }

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim RefreshLane = new(1, 1);
    private static Session? _session;
    private static bool _loaded;

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio-ChatGPT/1.0");
        return client;
    });

    internal static string? FileOverrideForTests { get; set; }

    public static event Action? Changed;

    private static string SessionFile => FileOverrideForTests ?? ApplicationPaths.CreateDefault().ChatGptSignInFile;

    internal static bool IsMarker(string? key) => string.Equals(key?.Trim(), CredentialMarker, StringComparison.Ordinal);

    internal static bool IsSignedIn
    {
        get
        {
            lock (Gate) return LoadLocked()?.HasPlan == true;
        }
    }

    internal static string SignedInEmail
    {
        get
        {
            lock (Gate) return LoadLocked() is { HasPlan: true } s ? s.Email : string.Empty;
        }
    }

    internal static bool PlanActive(AiVendor vendor)
        => vendor == AiVendor.OpenAI && IsSignedIn && SettingsManager.Instance.AiMode != AiMode.LocalOnly;

    internal static bool WelcomeShown
    {
        get { lock (Gate) return LoadLocked()?.WelcomeShown == true; }
    }

    internal static void MarkWelcomeShown()
    {
        lock (Gate)
        {
            if (LoadLocked() is not { } s || s.WelcomeShown) return;
            s.WelcomeShown = true;
            SaveLocked(s);
        }
    }

    private static Session? LoadLocked()
    {
        if (_loaded) return _session;
        _loaded = true;
        try
        {
            string path = SessionFile;
            if (!File.Exists(path)) return _session = null;
            string clear = SecretProtector.Unprotect(File.ReadAllText(path));
            _session = string.IsNullOrWhiteSpace(clear) ? null : FromJson(clear);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            RuntimeLog.Swallowed(ex);
            _session = null;
        }
        return _session;
    }

    private static void SaveLocked(Session s)
    {
        _session = s;
        _loaded = true;
        try
        {
            string path = SessionFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string sealedText = SecretProtector.Protect(ToJson(s));
            if (sealedText.Length == 0) throw new IOException("The ChatGPT sign-in could not be sealed with Windows data protection.");
            AtomicJsonFile.WriteText(path, sealedText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Fail("ChatGPT", $"{Tag}: the sign-in could not be saved ({ex.GetType().Name}). It works until the app closes.");
        }
    }

    internal static string ToJson(Session s)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("hostId", s.HostId);
            w.WriteString("clientId", s.ClientId);
            w.WriteString("sub", s.Subject);
            w.WriteString("email", s.Email);
            w.WriteString("idToken", s.IdToken);
            w.WriteString("accessToken", s.AccessToken);
            w.WriteString("refreshToken", s.RefreshToken);
            w.WriteString("expiresUtc", s.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("scope", s.Scope);
            w.WriteBoolean("welcomeShown", s.WelcomeShown);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static Session? FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        DateTime.TryParse(S("expiresUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime exp);
        return new Session
        {
            HostId = S("hostId"),
            ClientId = S("clientId"),
            Subject = S("sub"),
            Email = S("email"),
            IdToken = S("idToken"),
            AccessToken = S("accessToken"),
            RefreshToken = S("refreshToken"),
            ExpiresUtc = exp,
            Scope = S("scope"),
            WelcomeShown = r.TryGetProperty("welcomeShown", out var ws) && ws.ValueKind == JsonValueKind.True,
        };
    }

    internal static bool ScopeHas(string? scope, string wanted)
        => (scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(wanted, StringComparer.Ordinal);

    internal static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    internal static string RandomToken(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static string AuthorizeUrl(string clientId, string hostId, string redirectUri, string state, string nonce, string challenge,
        bool firstRegistration, string? idTokenHint)
    {
        var q = new List<(string, string)>
        {
            ("response_type", "code"),
            ("client_id", clientId),
        };
        if (firstRegistration) q.Add(("agent_name_hint", AppName));
        q.Add(("ext_agent_host_id", hostId));
        q.Add(("redirect_uri", redirectUri));
        q.Add(("scope", Scopes));
        q.Add(("resource", Resource));
        q.Add(("state", state));
        q.Add(("nonce", nonce));
        q.Add(("code_challenge", challenge));
        q.Add(("code_challenge_method", "S256"));
        if (!firstRegistration && !string.IsNullOrWhiteSpace(idTokenHint)) q.Add(("id_token_hint", idTokenHint));
        return AuthorizeEndpoint + "?" + string.Join("&", q.Select(p => Uri.EscapeDataString(p.Item1) + "=" + Uri.EscapeDataString(p.Item2)));
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string k = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            string v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            map[k] = v;
        }
        return map;
    }

    public static async Task<ChatGptSignInResult> SignInAsync(Action<string>? progress, CancellationToken ct)
    {
        Session previous;
        lock (Gate)
        {
            previous = LoadLocked() ?? new Session();
            if (previous.HostId.Length == 0)
            {
                previous.HostId = "urn:uuid:" + Guid.NewGuid().ToString("D");
                SaveLocked(previous);
            }
        }

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, PreferredPort);
            listener.Start();
        }
        catch (SocketException ex)
        {
            RuntimeLog.Swallowed(ex);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirectUri = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{CallbackPath}";
            string verifier = RandomToken(48);
            string state = RandomToken();
            string nonce = RandomToken();
            bool first = previous.ClientId.Length == 0;
            string url = AuthorizeUrl(first ? FirstRegistrationClientId : previous.ClientId, previous.HostId, redirectUri, state, nonce,
                Challenge(verifier), first, previous.IdToken);

            progress?.Invoke("Opening ChatGPT in your browser. Finish signing in there…");
            ShellLauncher.Launch(new ProcessStartInfo { FileName = url, UseShellExecute = true });

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(SignInWait);
            Dictionary<string, string> callback;
            try
            {
                callback = await WaitForCallbackAsync(listener, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ChatGptSignInResult(false, "The ChatGPT sign-in took too long. Press Continue with ChatGPT to try again.");
            }

            if (callback.TryGetValue("error", out string? error))
            {
                bool cancelled = error is "access_denied" or "login_required" or "consent_required";
                return new ChatGptSignInResult(false, cancelled ? "ChatGPT sign-in was cancelled." : $"ChatGPT did not sign you in ({error}).", cancelled);
            }
            if (!callback.TryGetValue("state", out string? gotState) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(gotState), Encoding.UTF8.GetBytes(state)))
                return new ChatGptSignInResult(false, "The ChatGPT sign-in answer did not match. Please try again.");
            if (!callback.TryGetValue("code", out string? code) || code.Length == 0)
                return new ChatGptSignInResult(false, "ChatGPT did not send a sign-in code. Please try again.");

            callback.TryGetValue("client_id", out string? issued);
            if (!first && !string.IsNullOrEmpty(issued) && !string.Equals(issued, previous.ClientId, StringComparison.Ordinal))
                return new ChatGptSignInResult(false, "ChatGPT answered for a different app registration. Please try again.");
            string clientId = !string.IsNullOrEmpty(issued) ? issued : previous.ClientId;
            if (clientId.Length == 0 || clientId == FirstRegistrationClientId)
                return new ChatGptSignInResult(false, "ChatGPT did not register this app. Please try again.");

            progress?.Invoke("Signed in. Finishing up…");
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
                ["resource"] = Resource,
            };
            var (status, body) = await PostFormAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
            if (status != 200)
                return new ChatGptSignInResult(false, $"ChatGPT did not finish the sign-in (HTTP {status.ToString(CultureInfo.InvariantCulture)}). Please try again.");

            var tokens = ParseTokenResponse(body);
            if (tokens.IdToken.Length == 0 || tokens.AccessToken.Length == 0)
                return new ChatGptSignInResult(false, "ChatGPT sent an incomplete sign-in. Please try again.");

            var (ok, subject, email, why) = await ValidateIdTokenAsync(tokens.IdToken, clientId, nonce, ct).ConfigureAwait(false);
            if (!ok)
            {
                RuntimeLog.Fail("ChatGPT", $"{Tag}: ID token rejected ({why}).");
                return new ChatGptSignInResult(false, "The ChatGPT sign-in could not be verified. Please try again.");
            }
            if (!string.IsNullOrEmpty(previous.Subject) && previous.ClientId == clientId && previous.Subject != subject)
                RuntimeLog.Info("ChatGPT", $"{Tag}: a different ChatGPT account signed in on this registration.");

            if (!ScopeHas(tokens.Scope, PlanScope))
            {
                lock (Gate)
                {
                    previous.ClientId = clientId;
                    previous.Subject = subject;
                    previous.Email = email;
                    previous.AccessToken = previous.RefreshToken = previous.IdToken = string.Empty;
                    SaveLocked(previous);
                }
                Changed?.Invoke();
                return new ChatGptSignInResult(false,
                    "You signed in, but ChatGPT did not allow this app to use your plan. Using a ChatGPT plan here needs ChatGPT Plus or Pro, and saying yes to sharing your plan.");
            }

            lock (Gate)
            {
                previous.ClientId = clientId;
                previous.Subject = subject;
                previous.Email = email;
                previous.IdToken = tokens.IdToken;
                previous.AccessToken = tokens.AccessToken;
                previous.RefreshToken = tokens.RefreshToken;
                previous.ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn));
                previous.Scope = tokens.Scope;
                SaveLocked(previous);
            }
            AiKeyHealth.Clear(AiVendor.OpenAI, CredentialMarker);
            RuntimeLog.Info("ChatGPT", $"{Tag}: signed in with a ChatGPT plan.");
            Changed?.Invoke();
            return new ChatGptSignInResult(true, string.IsNullOrEmpty(email) ? "Signed in with ChatGPT." : $"Signed in with ChatGPT as {email}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ChatGptSignInResult(false, "ChatGPT sign-in was cancelled.", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or System.ComponentModel.Win32Exception or JsonException or CryptographicException or FormatException)
        {
            RuntimeLog.Fail("ChatGPT", $"{Tag}: sign-in failed ({ex.GetType().Name}).");
            return new ChatGptSignInResult(false, "Could not reach ChatGPT. Check your internet and try again.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            string? requestLine = await ReadRequestLineAsync(stream, ct).ConfigureAwait(false);
            string target = requestLine?.Split(' ') is { Length: >= 2 } parts && parts[0] == "GET" ? parts[1] : string.Empty;
            int q = target.IndexOf('?');
            string path = q < 0 ? target : target[..q];
            if (!string.Equals(path, CallbackPath, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 404, "Not found.", ct).ConfigureAwait(false);
                continue;
            }
            var map = ParseQuery(q < 0 ? string.Empty : target[(q + 1)..]);
            bool ok = map.ContainsKey("code") && !map.ContainsKey("error");
            await WriteResponseAsync(stream, 200, ok
                ? "You are signed in. You can close this tab and go back to Free Video Studio."
                : "The sign-in did not finish. You can close this tab and go back to Free Video Studio.", ct).ConfigureAwait(false);
            return map;
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) break;
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') bytes.Add(one[0]);
        }
        return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Free Video Studio</title></head>"
            + "<body style=\"font-family:Segoe UI,sans-serif;background:#111;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">"
            + "<p style=\"font-size:18px\">" + WebUtility.HtmlEncode(message) + "</p></body></html>";
        byte[] payload = Encoding.UTF8.GetBytes(html);
        string head = $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    internal readonly record struct TokenResponse(string AccessToken, string RefreshToken, string IdToken, long ExpiresIn, string Scope, string Error);

    internal static TokenResponse ParseTokenResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new TokenResponse("", "", "", 0, "", "");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new TokenResponse("", "", "", 0, "", "");
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long exp = r.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long x) ? x : 3600;
            string err = S("error");
            if (err.Length == 0 && r.TryGetProperty("error", out var eo) && eo.ValueKind == JsonValueKind.Object
                && eo.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                err = code.GetString() ?? "";
            return new TokenResponse(S("access_token"), S("refresh_token"), S("id_token"), exp, S("scope"), err);
        }
        catch (JsonException ex)
        {
            RuntimeLog.Swallowed(ex);
            return new TokenResponse("", "", "", 0, "", "");
        }
    }

    private static async Task<(int Status, string Body)> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using HttpResponseMessage response = await SharedHttp.Value.PostAsync(url, content, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ((int)response.StatusCode, body);
    }

    internal static (bool Ok, string Subject, string Email, string Why) CheckIdTokenClaims(string payloadJson, string clientId, string nonce, DateTime utcNow)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var r = doc.RootElement;
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (S("iss") != Issuer) return (false, "", "", "issuer");
        bool audOk = r.TryGetProperty("aud", out var aud) && (
            (aud.ValueKind == JsonValueKind.String && aud.GetString() == clientId)
            || (aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == clientId)));
        if (!audOk) return (false, "", "", "audience");
        if (!r.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number
            || DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime < utcNow.AddMinutes(-2))
            return (false, "", "", "expired");
        if (S("nonce") != nonce) return (false, "", "", "nonce");
        string sub = S("sub");
        if (sub.Length == 0) return (false, "", "", "subject");
        return (true, sub, S("email"), "");
    }

    private static async Task<(bool Ok, string Subject, string Email, string Why)> ValidateIdTokenAsync(string idToken, string clientId, string nonce, CancellationToken ct)
    {
        string[] parts = idToken.Split('.');
        if (parts.Length != 3) return (false, "", "", "format");
        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        string alg = header.RootElement.TryGetProperty("alg", out var a) ? a.GetString() ?? "" : "";
        string kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() ?? "" : "";
        if (alg != "RS256") return (false, "", "", "algorithm");

        using HttpResponseMessage response = await SharedHttp.Value.GetAsync(JwksUri, ct).ConfigureAwait(false);
        string jwks = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (false, "", "", "keys unavailable");
        if (!VerifyRs256(parts[0] + "." + parts[1], FromBase64Url(parts[2]), jwks, kid)) return (false, "", "", "signature");
        return CheckIdTokenClaims(Encoding.UTF8.GetString(FromBase64Url(parts[1])), clientId, nonce, DateTime.UtcNow);
    }

    internal static bool VerifyRs256(string signedPart, byte[] signature, string jwksJson, string kid)
    {
        using var doc = JsonDocument.Parse(jwksJson);
        if (!doc.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) return false;
        foreach (var key in keys.EnumerateArray())
        {
            string Prop(string n) => key.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (Prop("kty") != "RSA") continue;
            if (kid.Length > 0 && Prop("kid") != kid) continue;
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = FromBase64Url(Prop("n")), Exponent = FromBase64Url(Prop("e")) });
            if (rsa.VerifyData(Encoding.ASCII.GetBytes(signedPart), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return true;
        }
        return false;
    }

    internal static async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        Session? s;
        lock (Gate) s = LoadLocked();
        if (s is not { HasPlan: true }) throw SignedOutError();
        if (!forceRefresh && s.AccessToken.Length > 0 && s.ExpiresUtc - DateTime.UtcNow > RefreshAhead) return s.AccessToken;

        await RefreshLane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (Gate) s = LoadLocked();
            if (s is not { HasPlan: true }) throw SignedOutError();
            if (!forceRefresh && s.AccessToken.Length > 0 && s.ExpiresUtc - DateTime.UtcNow > RefreshAhead) return s.AccessToken;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = s.ClientId,
                ["refresh_token"] = s.RefreshToken,
                ["resource"] = Resource,
            };
            int status;
            string body;
            try
            {
                (status, body) = await PostFormAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Network, "Could not reach ChatGPT to renew your sign-in. Check your internet.", ex.GetType().Name);
            }
            var tokens = ParseTokenResponse(body);
            if (status == 200 && tokens.AccessToken.Length > 0)
            {
                lock (Gate)
                {
                    s.AccessToken = tokens.AccessToken;
                    if (tokens.RefreshToken.Length > 0) s.RefreshToken = tokens.RefreshToken;
                    if (tokens.IdToken.Length > 0) s.IdToken = tokens.IdToken;
                    if (tokens.Scope.Length > 0) s.Scope = tokens.Scope;
                    s.ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn));
                    SaveLocked(s);
                }
                return s.AccessToken;
            }
            if (TerminalRefreshErrors.Contains(tokens.Error, StringComparer.Ordinal) || status == 401)
            {
                RuntimeLog.Info("ChatGPT", $"{Tag}: the sign-in can no longer be renewed ({tokens.Error}); signing out.");
                ClearTokens(keepIdToken: false);
                throw SignedOutError();
            }
            throw new AiProviderException(AiVendor.OpenAI, status >= 500 ? AiProviderErrorKind.Overloaded : AiProviderErrorKind.BadRequest,
                "ChatGPT could not renew your sign-in right now. Try again in a moment.", $"refresh HTTP {status.ToString(CultureInfo.InvariantCulture)} {tokens.Error}");
        }
        finally
        {
            RefreshLane.Release();
        }
    }

    internal static AiProviderException SignedOutError()
        => new(AiVendor.OpenAI, AiProviderErrorKind.InvalidKey,
            "Your ChatGPT sign-in has ended. Sign in again: Settings › AI › Continue with ChatGPT.", "chatgpt signed out");

    private static void ClearTokens(bool keepIdToken)
    {
        lock (Gate)
        {
            if (LoadLocked() is not { } s) return;
            s.AccessToken = string.Empty;
            s.RefreshToken = string.Empty;
            s.Scope = string.Empty;
            s.ExpiresUtc = default;
            if (!keepIdToken) s.IdToken = string.Empty;
            SaveLocked(s);
        }
        Changed?.Invoke();
    }

    public static async Task<string> SignOutAsync(CancellationToken ct)
    {
        Session? s;
        lock (Gate) s = LoadLocked();
        if (s == null || s.RefreshToken.Length == 0)
        {
            ClearTokens(keepIdToken: false);
            return "Signed out of ChatGPT.";
        }
        bool revoked = false;
        try
        {
            var (status, _) = await PostFormAsync(RevokeEndpoint, new Dictionary<string, string>
            {
                ["token"] = s.RefreshToken,
                ["token_type_hint"] = "refresh_token",
                ["client_id"] = s.ClientId,
            }, ct).ConfigureAwait(false);
            revoked = status == 200;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            RuntimeLog.Swallowed(ex);
        }
        ClearTokens(keepIdToken: false);
        RuntimeLog.Info("ChatGPT", $"{Tag}: signed out (remote revoke {(revoked ? "confirmed" : "not confirmed")}).");
        return revoked
            ? "Signed out of ChatGPT. This app can no longer use your plan."
            : "Signed out here. ChatGPT could not be reached to confirm it, so you can also remove Free Video Studio in ChatGPT's settings.";
    }
}
