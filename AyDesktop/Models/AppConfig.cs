using System.Xml.Serialization;

namespace AyDesktop.Models;

public enum TriState { Follow = 0, On = 1, Off = 2 }

/// <summary>文件夹打开方式</summary>
public enum FolderMode
{
    Fullscreen = 0,   // 全屏窗口打开
    Group = 1,        // 桌面分组框
}

[XmlRoot("AyDesktopConfig")]
public class AppConfig
{
    [XmlElement("ShowBackground")] public bool ShowBackground { get; set; } = true;
    [XmlElement("BackgroundColor")] public string BackgroundColor { get; set; } = "#CC1E1E1E";
    [XmlElement("BackgroundOpacity")] public double BackgroundOpacity { get; set; } = 0.85;
    [XmlElement("IconSize")] public int IconSize { get; set; } = 48;
    [XmlElement("AutoArrange")] public bool AutoArrange { get; set; } = true;
    [XmlElement("SnapToGrid")] public bool SnapToGrid { get; set; } = true;
    [XmlElement("GridSize")] public int GridSize { get; set; } = 96;
    [XmlElement("FloatWindowOpacity")] public double FloatWindowOpacity { get; set; } = 0.85;
    [XmlElement("FloatTopmost")] public bool FloatTopmost { get; set; } = true;
    [XmlElement("FloatX")] public double? FloatX { get; set; }
    [XmlElement("FloatY")] public double? FloatY { get; set; }
    [XmlElement("EnableAnimations")] public bool EnableAnimations { get; set; } = true;
    [XmlElement("RunAtStartup")] public bool RunAtStartup { get; set; } = false;
    [XmlElement("ShowFileName")] public bool ShowFileName { get; set; } = true;
    [XmlElement("ShowFileExtensions")] public TriState ShowFileExtensions { get; set; } = TriState.Follow;
    [XmlElement("ShowHiddenFiles")] public TriState ShowHiddenFiles { get; set; } = TriState.Follow;
    [XmlElement("ShowSystemIcons")] public TriState ShowSystemIcons { get; set; } = TriState.Follow;

    /// <summary>文件夹打开方式</summary>
    [XmlElement("FolderMode")] public FolderMode FolderMode { get; set; } = FolderMode.Group;

    /// <summary>拖拽图标叠放时自动创建文件夹</summary>
    [XmlElement("AutoCreateFolder")] public bool AutoCreateFolder { get; set; } = true;

    /// <summary>★ 全屏文件夹以模态框形式打开</summary>
    [XmlElement("FolderWindowModal")] public bool FolderWindowModal { get; set; } = true;

    [XmlArray("HiddenShellMenus")]
    [XmlArrayItem("Id")]
    public List<string> HiddenShellMenus { get; set; } = new();

    [XmlArray("MonitoredFolders")]
    [XmlArrayItem("Folder")]
    public List<string> MonitoredFolders { get; set; } = new();
}