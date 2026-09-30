using System.Text.Json;
using System.Text.Json.Serialization;

namespace AyDesktop.Services;

public enum StartupAction
{
    None,
    DeleteDesktopConfig,
    DeleteAppConfig,
    ResetAll,
    Logout,
}

public class StartupCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>参数名（启动命令行开关）</summary>
    public const string Switch = "-commandline";

    /// <summary>把动作编码成命令行参数值（Base32(JSON)）。</summary>
    public static string Encode(StartupAction action)
    {
        var json = JsonSerializer.Serialize(
            new StartupCommand { Action = action.ToString() });
        return Base32.EncodeString(json);
    }

    /// <summary>从 Base32(JSON) 解析动作；失败返回 None。</summary>
    public static StartupAction Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return StartupAction.None;
        try
        {
            var json = Base32.DecodeToString(value.Trim());
            var cmd = JsonSerializer.Deserialize<StartupCommand>(json);
            if (cmd == null || string.IsNullOrEmpty(cmd.Action)) return StartupAction.None;
            return Enum.TryParse<StartupAction>(cmd.Action, ignoreCase: true, out var a)
                ? a : StartupAction.None;
        }
        catch { return StartupAction.None; }
    }

    /// <summary>从命令行参数数组里抽取 -commandline 后面的值。</summary>
    public static string? ExtractArg(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}