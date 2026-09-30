using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace AyDesktop.Services;

/// <summary>
/// 采集不会因重装系统而改变的硬件指纹：
///   主板 UUID、CPU ID、BIOS 序列号、主板序列号
/// 
/// 不采集：MachineGuid（重装会变）、卷序列号（格式化会变）、MAC（虚拟网卡会变）。
/// 
/// 只要 ≥ 2 项有效，即认为采集成功；结果用 SHA256 归并为一个 64 位十六进制串。
/// 同一个进程内缓存，避免反复查询 WMI。
/// </summary>
public static class HardwareId
{
    private static string? _cached;
    private static readonly object _lock = new();

    /// <summary>固定盐 —— 防止用公开硬件指纹直接建彩虹表</summary>
    private const string Salt = "AyDesktop::HW::v1::DoNotChange";

    public static string Get()
    {
        if (_cached != null) return _cached;
        lock (_lock)
        {
            if (_cached != null) return _cached;
            _cached = Compute();
            return _cached;
        }
    }

    private static string Compute()
    {
        var parts = new List<KeyValuePair<string, string>>();

        TryAdd(parts, "csproduct.uuid", QueryWmi(
            "SELECT UUID FROM Win32_ComputerSystemProduct", "UUID"));

        TryAdd(parts, "cpu.id", QueryWmi(
            "SELECT ProcessorId FROM Win32_Processor", "ProcessorId"));

        TryAdd(parts, "bios.sn", QueryWmi(
            "SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"));

        TryAdd(parts, "baseboard.sn", QueryWmi(
            "SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"));

        if (parts.Count < 2)
            throw new InvalidOperationException(
                $"无法采集足够的硬件指纹（仅 {parts.Count} 项有效）。");

        // 固定顺序：避免 WMI 返回顺序变化导致哈希不同
        parts.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        var raw = new StringBuilder();
        raw.Append(Salt).Append('|');
        foreach (var p in parts)
            raw.Append(p.Key).Append('=').Append(p.Value).Append('|');

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString()));
        return Convert.ToHexString(hash); // 64 字符十六进制
    }

    private static void TryAdd(List<KeyValuePair<string, string>> list,
                               string key, string? value)
    {
        if (IsValidId(value))
            list.Add(new(key, value!.Trim()));
    }

    private static string? QueryWmi(string query, string prop)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var v = obj[prop]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
        }
        catch { /* WMI 不可用时静默降级 */ }
        return null;
    }

    /// <summary>过滤常见的"假"序列号（OEM 未填 / 全 0 / 全 F / 模板值）</summary>
    private static bool IsValidId(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;

        var t = s.Trim();
        if (t.Length < 4) return false;

        var lower = t.ToLowerInvariant();

        if (lower.Contains("to be filled") ||
            lower.Contains("default string") ||
            lower.Contains("system serial") ||
            lower.Contains("not applicable") ||
            lower.Contains("not specified") ||
            lower.Contains("not available") ||
            lower.Contains("o.e.m") ||
            lower.Contains("oem") ||
            lower.Contains("unknown") ||
            lower.Contains("none"))
            return false;

        // 全 0 / 全 F / 全相同字符（如 "FFFFFFFF-FFFF-..." 或 "00000000"）
        bool allSame = true;
        char first = t[0];
        foreach (var c in t)
        {
            if (c != first && c != '-' && c != ' ' && c != ':') { allSame = false; break; }
        }
        if (allSame) return false;

        // 十六进制专用：全 0 / 全 F
        if (t.All(c => c == '0' || c == '-' || c == ' ')) return false;
        if (t.All(c => c == 'F' || c == 'f' || c == '-' || c == ' ')) return false;

        return true;
    }
}