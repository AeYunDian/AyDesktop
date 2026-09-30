using System.Text.Json;
using AyOAuthClientSDK;

namespace AyDesktop.Services;

public class AuthService : IDisposable
{
    private const string ClientId =
        "9f2c4306de094a3d47ba663a5007a4642c92cb802e60c0f50d3118a428328a63";

    private const string KeyToken = "account";
    private const string KeyOffline = "offline";

    private static readonly string ClientSecret = LoadClientSecret();

    private static string LoadClientSecret()
    {
        var b32 = Env.AYDESKTOP_CLIENT_SECRET_B32;

        if (string.IsNullOrWhiteSpace(b32))
            throw new InvalidOperationException(
                "未配置 AYDESKTOP_CLIENT_SECRET_B32。\n\n" +
                "请在项目根目录的 .env 文件中设置该项，然后重新编译。");

        try
        {
            return Base32.DecodeToString(b32.Trim());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "AYDESKTOP_CLIENT_SECRET_B32 不是合法的 Base32 字符串。", ex);
        }
    }

    private const int CallbackPort = 18923;
    private const string CallbackPath = "/oauth/callback";
    private const string RedirectUri = "http://127.0.0.1:18923/oauth/callback";

    private AyOAuthClient? _client;
    private OAuthTokenResult? _token;
    public event Action? AuthorizationReceived;
    private OAuthUserInfo? _user;
    private DateTime _expireAtUtc = DateTime.MinValue;

    public OAuthUserInfo? CurrentUser => _user;
    public bool IsLoggedIn => _token != null && DateTime.UtcNow < _expireAtUtc;

    // ★ 新增：离线模式
    public bool IsOfflineMode { get; private set; }

    private AyOAuthClient Client
    {
        get
        {
            if (_client != null) return _client;

            _client = new AyOAuthClient(new AyOAuthOptions
            {
                ClientId = ClientId,
                ClientSecret = ClientSecret,
                LocalServerPort = CallbackPort,
                CallbackPath = CallbackPath,
                RedirectUri = RedirectUri,
            });
            return _client;
        }
    }

    // ===== 会话 =====

    public bool TryRestoreSession()
    {
        var json = MachineKeyStore.Load(KeyToken);
        if (string.IsNullOrEmpty(json)) return false;

        try
        {
            var s = JsonSerializer.Deserialize<StoredSession>(json);
            if (s == null || string.IsNullOrEmpty(s.AccessToken)) return false;
            if (DateTime.UtcNow >= s.ExpireAtUtc && string.IsNullOrEmpty(s.RefreshToken))
                return false;

            _token = new OAuthTokenResult
            {
                AccessToken = s.AccessToken,
                RefreshToken = s.RefreshToken,
                ExpiresIn = s.ExpiresIn,
                TokenType = s.TokenType,
                Scope = s.Scope
            };
            _expireAtUtc = s.ExpireAtUtc;
            _user = new OAuthUserInfo
            {
                Sub = s.Sub,
                Username = s.Username ?? "",
                Email = s.Email ?? "",
                Avatar = s.Avatar
            };
            return true;
        }
        catch { return false; }
    }

    public async Task LoginAsync()
    {
        try
        {
            var code = await Client.GetAuthorizationCodeAsync();
            try { AuthorizationReceived?.Invoke(); } catch { }
            _token = await Client.ExchangeCodeAsync(code);
            if (string.IsNullOrEmpty(_token.AccessToken))
                throw new AyOAuthException("响应中缺少 access_token。");

            _expireAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, _token.ExpiresIn) - 30);
            _user = await Client.GetUserInfoAsync(_token.AccessToken);
            Persist();

            ExitOfflineMode();   // ★ 登录成功 → 退出离线模式
        }
        catch (AyOAuthException ex) when (ex.HttpStatus == 32)
        {
            throw new AyOAuthException(
                $"端口 {CallbackPort} 已被其他程序占用，请关闭冲突程序后重试。",
                ex.Error, ex.ErrorDescription, ex.HttpStatus, ex);
        }
    }

    public async Task<bool> EnsureValidAsync()
    {
        if (_token == null) return false;
        if (DateTime.UtcNow < _expireAtUtc) return true;
        if (string.IsNullOrEmpty(_token.RefreshToken)) return false;

        try
        {
            var refreshed = await Client.RefreshTokenAsync(_token.RefreshToken);

            if (string.IsNullOrEmpty(refreshed.RefreshToken))
                refreshed.RefreshToken = _token.RefreshToken;

            _token = refreshed;
            _expireAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, _token.ExpiresIn) - 30);
            Persist();
            return true;
        }
        catch { return false; }
    }
    /// <summary>启动时调用：secret 缺失会抛异常。</summary>
    public static void ValidateConfiguration() => _ = ClientSecret;
    public void Logout()
    {
        var t = _token;
        _token = null;
        _user = null;
        _expireAtUtc = DateTime.MinValue;
        MachineKeyStore.Clear(KeyToken);
        MachineKeyStore.Clear(KeyOffline);   // ★ 登出也清离线标志
        IsOfflineMode = false;                // ★

        if (t != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var revoke = !string.IsNullOrEmpty(t.RefreshToken)
                        ? t.RefreshToken! : t.AccessToken!;
                    await Client.RevokeTokenAsync(revoke);
                }
                catch { }
            });
        }
    }

    // ===== ★ 离线模式 API =====

    /// <summary>
    /// 从磁盘恢复离线状态到内存，返回是否处于离线模式。App 启动时调用一次。
    /// </summary>
    public bool RestoreOfflineState()
    {
        try
        {
            var v = MachineKeyStore.Load(KeyOffline) == "1";
            IsOfflineMode = v;
            return v;
        }
        catch { return false; }
    }

    /// <summary>进入离线模式并持久化。</summary>
    public void EnterOfflineMode()
    {
        IsOfflineMode = true;
        try { MachineKeyStore.Save(KeyOffline, "1"); } catch { }
    }

    /// <summary>退出离线模式并清除持久化标志。</summary>
    public void ExitOfflineMode()
    {
        IsOfflineMode = false;
        try { MachineKeyStore.Clear(KeyOffline); } catch { }
    }

    private void Persist()
    {
        if (_token == null) return;
        var s = new StoredSession
        {
            AccessToken = _token.AccessToken,
            RefreshToken = _token.RefreshToken,
            ExpiresIn = _token.ExpiresIn,
            TokenType = _token.TokenType,
            Scope = _token.Scope,
            ExpireAtUtc = _expireAtUtc,
            Sub = _user?.Sub ?? 0,
            Username = _user?.Username,
            Email = _user?.Email,
            Avatar = _user?.Avatar
        };
        MachineKeyStore.Save(KeyToken, JsonSerializer.Serialize(s));
    }

    public void Dispose() => _client?.Dispose();

    private class StoredSession
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
        public string? TokenType { get; set; }
        public string? Scope { get; set; }
        public DateTime ExpireAtUtc { get; set; }
        public int Sub { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? Avatar { get; set; }
    }
}