using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using AyDesktop.Models;

namespace AyDesktop.Services;

public class ConfigService
{
    private readonly string _configPath;
    private readonly string _desktopPath;

    public ConfigService()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dbDir = Path.Combine(baseDir, "database");
        Directory.CreateDirectory(dbDir);

        _configPath = Path.Combine(dbDir, "config.xml");
        _desktopPath = Path.Combine(dbDir, "desktop.xml");
    }

    // ===== 配置读写 =====

    public AppConfig LoadConfig()
    {
        if (!File.Exists(_configPath)) return new AppConfig();
        try
        {
            var serializer = new XmlSerializer(typeof(AppConfig));
            using var reader = new StreamReader(_configPath);
            return (AppConfig?)serializer.Deserialize(reader) ?? new AppConfig();
        }
        catch { return new AppConfig(); }
    }

    public List<DesktopItem> LoadDesktopItems()
    {
        if (!File.Exists(_desktopPath)) return new List<DesktopItem>();
        try
        {
            var serializer = new XmlSerializer(typeof(DesktopItemCollection));
            using var reader = new StreamReader(_desktopPath);
            var collection = (DesktopItemCollection?)serializer.Deserialize(reader);
            return collection?.Items ?? new List<DesktopItem>();
        }
        catch { return new List<DesktopItem>(); }
    }

    public void SaveConfig(AppConfig config)
    {
        var serializer = new XmlSerializer(typeof(AppConfig));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false) // 不写 BOM
        };
        using var writer = XmlWriter.Create(_configPath, settings);
        serializer.Serialize(writer, config);
    }

    public void SaveDesktopItems(List<DesktopItem> items)
    {
        var serializer = new XmlSerializer(typeof(DesktopItemCollection));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false) // ★ 与 SaveConfig 一致，无 BOM
        };
        using var writer = XmlWriter.Create(_desktopPath, settings);
        serializer.Serialize(writer, new DesktopItemCollection { Items = items });
    }
}