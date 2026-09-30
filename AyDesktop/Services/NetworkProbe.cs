using System.Net.Http;

namespace AyDesktop.Services;

/// <summary>
/// 网络/服务可达性探测。
/// 不用 Ping（ICMP 常被屏蔽），改用 HTTP HEAD 请求。
/// </summary>
public static class NetworkProbe
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    /// <summary>
    /// 探测 Ay 服务是否可达。
    /// 判据：能拿到 HTTP 响应即视为可达（包括 401/404/405 等业务错误）。
    /// 网络不可达、DNS 失败、超时 → false。
    /// </summary>
    public static async Task<bool> IsServerReachableAsync(
        CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Head,
                "https://online.undz.cn/api/oauth/verify");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}