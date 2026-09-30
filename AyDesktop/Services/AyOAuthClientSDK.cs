#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

// SDK 支持 .NET Framework 4.6.1 ~ .NET 10，Windows / Linux / macOS
namespace AyOAuthClientSDK
{
    public class AyOAuthOptions
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string? RedirectUri { get; set; }

        public string AuthorizationEndpoint { get; set; } = "https://online.undz.cn/api/oauth/authorize";
        public string TokenEndpoint { get; set; } = "https://online.undz.cn/api/oauth/token";
        public string UserInfoEndpoint { get; set; } = "https://online.undz.cn/api/oauth/userinfo";
        public string RevokeEndpoint { get; set; } = "https://online.undz.cn/api/oauth/revoke";
        public string VerifyEndpoint { get; set; } = "https://online.undz.cn/api/oauth/verify";

        public string Scope { get; set; } = "openid profile email";
        public int LocalServerPort { get; set; } = 0;
        public string CallbackPath { get; set; } = "/oauth/callback";
        public TimeSpan AuthorizationTimeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    public class OAuthTokenResult
    {
        public string? AccessToken { get; set; }
        public string? TokenType { get; set; }
        public int ExpiresIn { get; set; }
        public string? RefreshToken { get; set; }
        public string? Scope { get; set; }
        public string? IdToken { get; set; }
    }

    public class OAuthUserInfo
    {
        public int Sub { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? Gender { get; set; }
        public string? Avatar { get; set; }
        public string? Description { get; set; }
    }

    public class AyOAuthException : Exception
    {
        public string? Error { get; }
        public string? ErrorDescription { get; }
        public int? HttpStatus { get; }

        public AyOAuthException(string message,
                                string? error = null,
                                string? errorDescription = null,
                                int? httpStatus = null,
                                Exception? inner = null)
            : base(message, inner)
        {
            Error = error;
            ErrorDescription = errorDescription;
            HttpStatus = httpStatus;
        }
    }

    /// <summary>
    /// Ay OAuth 2.0 客户端（标准授权码流程，无 PKCE）
    /// </summary>
    public class AyOAuthClient : IDisposable
    {
        private readonly AyOAuthOptions _options;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        public AyOAuthClient(AyOAuthOptions options) : this(options, null) { }

        public AyOAuthClient(AyOAuthOptions options, HttpClient? httpClient)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (string.IsNullOrWhiteSpace(_options.ClientId))
                throw new ArgumentException("ClientId is required.", nameof(options));
            if (string.IsNullOrWhiteSpace(_options.ClientSecret))
                throw new ArgumentException("ClientSecret is required.", nameof(options));

            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        // ====================================================================
        // 授权码流程
        // ====================================================================

        public async Task<string> GetAuthorizationCodeAsync(
    CancellationToken cancellationToken = default)
        {
            string state = GenerateState();
            string redirectUri = _options.RedirectUri ?? GetAutoRedirectUri();

            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirectParsed))
                throw new InvalidOperationException($"Invalid RedirectUri: {redirectUri}");

            // ★ Windows HttpListener 要求 prefix 必须以 "/" 结尾
            //   所以只注册根路径，然后在回调里手动校验 path 是否匹配
            string listenerPrefix =
                $"{redirectParsed.Scheme}://{redirectParsed.Host}:{redirectParsed.Port}/";
            string expectedPath = redirectParsed.AbsolutePath.TrimEnd('/');

            string authUrl = BuildAuthorizationUrl(redirectUri, state);

            using var listener = new HttpListener();
            listener.Prefixes.Add(listenerPrefix);

            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                throw new AyOAuthException(
                    $"无法启动本地回调监听 ({listenerPrefix})：{ex.Message}",
                    error: "listener_start_failed",
                    httpStatus: ex.ErrorCode, inner: ex);
            }
            catch (ArgumentException ex)
            {
                throw new AyOAuthException(
                    $"回调地址格式不合法 ({listenerPrefix})：{ex.Message}",
                    error: "invalid_prefix", inner: ex);
            }

            OpenBrowser(authUrl);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(_options.AuthorizationTimeout);

            var delayTask = Task.Delay(Timeout.Infinite, linkedCts.Token);

            try
            {
                // 循环等待，直到拿到 path 匹配的请求（可能被 favicon 等干扰）
                while (true)
                {
                    var contextTask = listener.GetContextAsync();
                    var completed = await Task.WhenAny(contextTask, delayTask).ConfigureAwait(false);

                    if (completed != contextTask)
                    {
                        _ = contextTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);

                        if (cancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(cancellationToken);

                        throw new TimeoutException(
                            $"授权超时：用户在 {_options.AuthorizationTimeout.TotalMinutes:0} 分钟内未完成登录。");
                    }

                    HttpListenerContext context;
                    try { context = await contextTask.ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        throw new AyOAuthException("等待回调时出错。", inner: ex);
                    }

                    // 校验 path
                    string requestPath = (context.Request.Url?.AbsolutePath ?? "").TrimEnd('/');
                    if (!string.Equals(requestPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // 不是我们期望的回调（可能是 /favicon.ico），回 404 继续等
                        try
                        {
                            context.Response.StatusCode = 404;
                            context.Response.Close();
                        }
                        catch { }
                        continue;
                    }

                    return await HandleCallbackAsync(context, state, cancellationToken)
                           .ConfigureAwait(false);
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        private async Task<string> HandleCallbackAsync(
            HttpListenerContext context, string expectedState, CancellationToken ct)
        {
            var request = context.Request;
            var response = context.Response;

            var query = ParseQueryString(request.Url?.Query ?? string.Empty);
            string? code = query.TryGetValue("code", out var c) ? c : null;
            string? receivedState = query.TryGetValue("state", out var s) ? s : null;

            // state 时序安全比较
            if (string.IsNullOrEmpty(receivedState) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(receivedState),
                    Encoding.ASCII.GetBytes(expectedState)))
            {
                await SendErrorResponseAsync(response,
                    "state 校验失败（可能的 CSRF 攻击）", ct).ConfigureAwait(false);
                throw new AyOAuthException("state 校验失败。", error: "invalid_state");
            }

            if (string.IsNullOrEmpty(code))
            {
                string? error = query.TryGetValue("error", out var e) ? e : null;
                string? errorDesc = query.TryGetValue("error_description", out var ed) ? ed : null;
                await SendErrorResponseAsync(response,
                    $"授权失败：{error} - {errorDesc}", ct).ConfigureAwait(false);
                throw new AyOAuthException($"授权失败：{error}", error, errorDesc);
            }

            await SendSuccessResponseAsync(response, ct).ConfigureAwait(false);
            return code!;
        }

        // ====================================================================
        // Token 交换 / 刷新
        // ====================================================================

        public async Task<OAuthTokenResult> ExchangeCodeAsync(
            string code, string? redirectUri = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("code is required.", nameof(code));

            redirectUri ??= _options.RedirectUri ?? GetAutoRedirectUri();

            var parameters = new List<KeyValuePair<string, string>>
            {
                new("grant_type",    "authorization_code"),
                new("code",          code),
                new("client_id",     _options.ClientId),
                new("client_secret", _options.ClientSecret),
                new("redirect_uri",  redirectUri),
            };

            using var content = new FormUrlEncodedContent(parameters);
            using var response = await _httpClient
                .PostAsync(_options.TokenEndpoint, content, ct).ConfigureAwait(false);

            return await ParseTokenResponseAsync(response, "Token exchange", ct)
                .ConfigureAwait(false);
        }

        public async Task<OAuthTokenResult> RefreshTokenAsync(
            string refreshToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new ArgumentException("refreshToken is required.", nameof(refreshToken));

            var parameters = new List<KeyValuePair<string, string>>
            {
                new("grant_type",    "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id",     _options.ClientId),
                new("client_secret", _options.ClientSecret),
            };

            using var content = new FormUrlEncodedContent(parameters);
            using var response = await _httpClient
                .PostAsync(_options.TokenEndpoint, content, ct).ConfigureAwait(false);

            return await ParseTokenResponseAsync(response, "Refresh token", ct)
                .ConfigureAwait(false);
        }

        private async Task<OAuthTokenResult> ParseTokenResponseAsync(
            HttpResponseMessage response, string op, CancellationToken ct)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var err = TryParseJsonObject(body);
                string? error = GetString(err, "error") ?? "unknown";
                string? errorDesc = GetString(err, "error_description");
                throw new AyOAuthException($"{op} 失败：{error}",
                    error, errorDesc, (int)response.StatusCode);
            }

            var json = JsonParser.ParseObject(body);
            return new OAuthTokenResult
            {
                AccessToken = GetString(json, "access_token"),
                TokenType = GetString(json, "token_type"),
                ExpiresIn = GetInt32(json, "expires_in"),
                RefreshToken = GetString(json, "refresh_token"),
                Scope = GetString(json, "scope"),
                IdToken = GetString(json, "id_token")
            };
        }

        // ====================================================================
        // 用户信息 / 验证 / 撤销
        // ====================================================================

        public async Task<OAuthUserInfo> GetUserInfoAsync(
            string accessToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("accessToken is required.", nameof(accessToken));

            using var req = new HttpRequestMessage(HttpMethod.Get, _options.UserInfoEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var err = TryParseJsonObject(body);
                throw new AyOAuthException(
                    $"UserInfo 请求失败：{GetString(err, "error") ?? "unknown"}",
                    GetString(err, "error"),
                    GetString(err, "error_description"),
                    (int)response.StatusCode);
            }

            var json = JsonParser.ParseObject(body);
            return new OAuthUserInfo
            {
                Sub = GetInt32(json, "sub"),
                Username = GetString(json, "username"),
                Email = GetString(json, "email"),
                Gender = GetString(json, "gender"),
                Avatar = GetString(json, "avatar"),
                Description = GetString(json, "description")
            };
        }

        public async Task<bool> VerifyTokenAsync(
            string accessToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return false;

            using var req = new HttpRequestMessage(HttpMethod.Get, _options.VerifyEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return GetBool(TryParseJsonObject(body), "valid");
        }

        public async Task RevokeTokenAsync(
            string token, string? tokenTypeHint = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("token is required.", nameof(token));

            var parameters = new List<KeyValuePair<string, string>>
            {
                new("token",         token),
                new("client_id",     _options.ClientId),
                new("client_secret", _options.ClientSecret),
            };
            if (!string.IsNullOrEmpty(tokenTypeHint))
                parameters.Add(new("token_type_hint", tokenTypeHint!));

            using var content = new FormUrlEncodedContent(parameters);
            using var response = await _httpClient
                .PostAsync(_options.RevokeEndpoint, content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var err = TryParseJsonObject(body);
                throw new AyOAuthException(
                    $"撤销令牌失败（HTTP {(int)response.StatusCode}）",
                    GetString(err, "error"),
                    GetString(err, "error_description"),
                    (int)response.StatusCode);
            }
        }

        // ====================================================================
        // 辅助
        // ====================================================================

        private string BuildAuthorizationUrl(string redirectUri, string state)
        {
            var builder = new UriBuilder(_options.AuthorizationEndpoint);
            var query = ParseQueryString(builder.Query);

            query["client_id"] = _options.ClientId;
            query["response_type"] = "code";
            query["redirect_uri"] = redirectUri;
            query["scope"] = _options.Scope;
            query["state"] = state;

            builder.Query = BuildQueryString(query);
            return builder.Uri.ToString();
        }

        private string GetAutoRedirectUri()
        {
            int port = _options.LocalServerPort;
            if (port == 0) port = GetAvailablePort();

            string path = _options.CallbackPath;
            if (string.IsNullOrEmpty(path)) path = "/oauth/callback";
            if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;

            return $"http://127.0.0.1:{port}{path}";
        }

        private static int GetAvailablePort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork,
                                          SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        private static string GenerateState()
        {
            Span<byte> bytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return;
            }
            catch { }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "rundll32",
                        Arguments = $"url.dll,FileProtocolHandler \"{url}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", new[] { url });
                else
                    Process.Start("xdg-open", new[] { url });
            }
            catch { }
        }

        private static async Task SendSuccessResponseAsync(
            HttpListenerResponse response, CancellationToken ct)
        {
            const string html =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<title>授权成功</title></head><body>" +
                "<div style=\"font-family:system-ui;text-align:center;margin-top:80px\">" +
                "<h2>授权成功</h2><p>您现在可以关闭这个窗口。</p>" +
                "</div></body></html>";
            await WriteHtmlResponseAsync(response, html, 200, ct).ConfigureAwait(false);
        }

        private static async Task SendErrorResponseAsync(
            HttpListenerResponse response, string message, CancellationToken ct)
        {
            string safe = WebUtility.HtmlEncode(message);
            string html =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<title>授权失败</title></head><body>" +
                "<div style=\"font-family:system-ui;text-align:center;margin-top:80px\">" +
                $"<h2>授权失败</h2><p>{safe}</p>" +
                "</div></body></html>";
            await WriteHtmlResponseAsync(response, html, 400, ct).ConfigureAwait(false);
        }

        private static async Task WriteHtmlResponseAsync(
            HttpListenerResponse response, string html, int status, CancellationToken ct)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            try
            {
                response.StatusCode = status;
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.Headers["Cache-Control"] = "no-store";
                await response.OutputStream
                    .WriteAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            }
            catch { }
            finally { try { response.Close(); } catch { } }
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            if (query[0] == '?') query = query.Substring(1);

            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key, value;
                if (eq < 0) { key = WebUtility.UrlDecode(pair); value = ""; }
                else
                {
                    key = WebUtility.UrlDecode(pair.Substring(0, eq));
                    value = WebUtility.UrlDecode(pair.Substring(eq + 1));
                }
                if (!string.IsNullOrEmpty(key)) dict[key] = value;
            }
            return dict;
        }

        private static string BuildQueryString(Dictionary<string, string> dict)
        {
            var sb = new StringBuilder();
            foreach (var kv in dict)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(WebUtility.UrlEncode(kv.Key));
                sb.Append('=');
                sb.Append(WebUtility.UrlEncode(kv.Value));
            }
            return sb.ToString();
        }

        private static Dictionary<string, object?> TryParseJsonObject(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new();
            try { return JsonParser.ParseObject(body!); }
            catch { return new(); }
        }

        private static string? GetString(Dictionary<string, object?> d, string key) =>
            d.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

        private static int GetInt32(Dictionary<string, object?> d, string key, int fallback = 0)
        {
            if (!d.TryGetValue(key, out var v) || v is null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool GetBool(Dictionary<string, object?> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || v is null) return false;
            if (v is bool b) return b;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient.Dispose();
        }
    }

    // ========================================================================
    // 轻量级 JSON 解析器
    // ========================================================================

    internal static class JsonParser
    {
        public static Dictionary<string, object?> ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            int index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '{') return new();
            index++;
            return ParseObjectBody(json, ref index);
        }

        private static Dictionary<string, object?> ParseObjectBody(string json, ref int index)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            SkipWhitespace(json, ref index);

            if (index < json.Length && json[index] == '}') { index++; return dict; }

            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"')
                    throw new FormatException($"JSON: expected '\"' at position {index}");

                string key = ParseString(json, ref index);

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':')
                    throw new FormatException($"JSON: expected ':' at position {index}");
                index++;

                SkipWhitespace(json, ref index);
                dict[key] = ParseValue(json, ref index);

                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    throw new FormatException("JSON: unexpected end of object");

                char ch = json[index];
                if (ch == '}') { index++; return dict; }
                if (ch == ',') { index++; continue; }
                throw new FormatException($"JSON: expected ',' or '}}' at position {index}");
            }
        }

        private static object? ParseValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length)
                throw new FormatException("JSON: unexpected end of input");

            char ch = json[index];
            if (ch == '"') return ParseString(json, ref index);
            if (ch == '{') { index++; return ParseObjectBody(json, ref index); }

            if (ch == '[')
            {
                index++;
                var list = new List<object?>();
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ']') { index++; return list; }

                while (true)
                {
                    SkipWhitespace(json, ref index);
                    list.Add(ParseValue(json, ref index));
                    SkipWhitespace(json, ref index);
                    if (index >= json.Length)
                        throw new FormatException("JSON: unexpected end of array");
                    if (json[index] == ']') { index++; return list; }
                    if (json[index] == ',') { index++; continue; }
                    throw new FormatException($"JSON: expected ',' or ']' at position {index}");
                }
            }

            if (MatchLiteral(json, index, "true")) { index += 4; return true; }
            if (MatchLiteral(json, index, "false")) { index += 5; return false; }
            if (MatchLiteral(json, index, "null")) { index += 4; return null; }

            if (ch == '-' || (ch >= '0' && ch <= '9'))
                return ParseNumber(json, ref index);

            throw new FormatException($"JSON: unexpected character '{ch}' at position {index}");
        }

        private static object ParseNumber(string json, ref int index)
        {
            int start = index;
            if (json[index] == '-') index++;

            int digitStart = index;
            while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
            if (index == digitStart)
                throw new FormatException($"JSON: invalid number at position {start}");

            bool isFloat = false;

            if (index < json.Length && json[index] == '.')
            {
                isFloat = true; index++;
                int fracStart = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index == fracStart)
                    throw new FormatException($"JSON: invalid fraction at position {index}");
            }

            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                isFloat = true; index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                int expStart = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index == expStart)
                    throw new FormatException($"JSON: invalid exponent at position {index}");
            }

            string numStr = json.Substring(start, index - start);
            if (isFloat)
                return double.Parse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture);

            if (long.TryParse(numStr, NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out long l))
                return l;

            return double.Parse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string ParseString(string json, ref int index)
        {
            if (index >= json.Length || json[index] != '"')
                throw new FormatException($"JSON: expected '\"' at position {index}");

            index++;
            var sb = new StringBuilder();

            while (index < json.Length)
            {
                char ch = json[index];

                if (ch == '"') { index++; return sb.ToString(); }

                if (ch == '\\')
                {
                    index++;
                    if (index >= json.Length)
                        throw new FormatException("JSON: unterminated escape sequence");

                    char esc = json[index];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;

                        case 'u':
                            {
                                if (index + 4 >= json.Length)
                                    throw new FormatException(
                                        "JSON: incomplete \\u escape at position " + index);

                                string hex = json.Substring(index + 1, 4);
                                int code;
                                try { code = Convert.ToInt32(hex, 16); }
                                catch
                                {
                                    throw new FormatException(
                                        $"JSON: invalid \\u escape '{hex}' at position {index}");
                                }

                                // 处理 UTF-16 代理对
                                if (code >= 0xD800 && code <= 0xDBFF &&
                                    index + 4 + 6 < json.Length &&
                                    json[index + 5] == '\\' && json[index + 6] == 'u')
                                {
                                    string hex2 = json.Substring(index + 7, 4);
                                    int low;
                                    try { low = Convert.ToInt32(hex2, 16); }
                                    catch { low = 0; }

                                    if (low >= 0xDC00 && low <= 0xDFFF)
                                    {
                                        int combined = 0x10000
                                            + ((code - 0xD800) << 10)
                                            + (low - 0xDC00);
                                        sb.Append(char.ConvertFromUtf32(combined));
                                        index += 11; // uXXXX\uXXXX 共 11 字符
                                        continue;
                                    }
                                }

                                sb.Append((char)code);
                                index += 5; // u + 4 hex
                                continue;
                            }

                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(ch);
                }

                index++;
            }

            throw new FormatException("JSON: unterminated string");
        }

        private static bool MatchLiteral(string json, int index, string literal)
        {
            if (index + literal.Length > json.Length) return false;
            for (int i = 0; i < literal.Length; i++)
                if (json[index + i] != literal[i]) return false;
            return true;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length)
            {
                char c = json[index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') index++;
                else break;
            }
        }
    }
}