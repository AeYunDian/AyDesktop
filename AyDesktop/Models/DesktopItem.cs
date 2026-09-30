using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Xml.Serialization;

namespace AyDesktop.Models;

[XmlType("Item")]
public class DesktopItem : INotifyPropertyChanged
{
    // ===== 组文件夹布局常量 =====
    public const double GroupCellWidth = 100;
    public const double GroupCellHeight = 88;
    public const double GroupPaddingH = 20;
    public const double GroupPaddingTop = 32;
    public const double GroupPaddingBottom = 16;
    public const int GroupColumnsFixed = 2;

    private string _name = "";
    private string _fullPath = "";
    private double _x;
    private double _y;
    private bool _isSelected;
    private bool _showExtension;
    private bool _neverShowExt;
    private bool _isGroupMode;

    public DesktopItem()
    {
        Children.CollectionChanged += (_, _) =>
        {
            OnP(nameof(GroupRows));
            OnP(nameof(GroupWidth));
            OnP(nameof(GroupHeight));
            OnP(nameof(ChildrenCount));
        };
    }

    [XmlAttribute("name")]
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return; _name = value;
            OnP(nameof(Name)); OnP(nameof(DisplayName)); OnP(nameof(Extension));
        }
    }

    [XmlAttribute("path")]
    public string FullPath
    {
        get => _fullPath;
        set { if (_fullPath == value) return; _fullPath = value; OnP(nameof(FullPath)); }
    }

    [XmlAttribute("isDir")] public bool IsDirectory { get; set; }
    [XmlAttribute("group")] public string GroupName { get; set; } = "";
    [XmlAttribute("order")] public int Order { get; set; }

    [XmlAttribute("isSystem")] public bool IsSystem { get; set; }
    [XmlAttribute("clsid")] public string Clsid { get; set; } = "";
    [XmlAttribute("isFolder")] public bool IsFolder { get; set; }

    // ★ 文件夹样式覆盖："", "Group", "Fullscreen"
    [XmlAttribute("folderMode")]
    public string FolderModeOverride { get; set; } = "";

    [XmlIgnore]
    public FolderMode? FolderModeOverrideValue
    {
        get => FolderModeOverride switch
        {
            "Group" => FolderMode.Group,
            "Fullscreen" => FolderMode.Fullscreen,
            _ => null,
        };
        set => FolderModeOverride = value switch
        {
            FolderMode.Group => "Group",
            FolderMode.Fullscreen => "Fullscreen",
            _ => "",
        };
    }

    [XmlArray("Children")]
    [XmlArrayItem("Item")]
    public ObservableCollection<DesktopItem> Children { get; set; } = new();

    [XmlIgnore]
    public bool IsSystemIcon => IsSystem || !string.IsNullOrEmpty(Clsid);

    /// <summary>运行时有效组模式（由 VM 根据 override 或默认值设置）</summary>
    [XmlIgnore]
    public bool IsGroupMode
    {
        get => _isGroupMode;
        set { if (_isGroupMode == value) return; _isGroupMode = value; OnP(nameof(IsGroupMode)); }
    }

    [XmlAttribute("x")]
    public double X { get => _x; set { if (Math.Abs(_x - value) > 0.01) { _x = value; OnP(nameof(X)); } } }

    [XmlAttribute("y")]
    public double Y { get => _y; set { if (Math.Abs(_y - value) > 0.01) { _y = value; OnP(nameof(Y)); } } }

    [XmlIgnore]
    public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnP(nameof(IsSelected)); } } }

    [XmlIgnore]
    public bool ShowExtension
    {
        get => _showExtension;
        set
        {
            if (_showExtension == value) return; _showExtension = value;
            OnP(nameof(ShowExtension)); OnP(nameof(DisplayName));
        }
    }

    [XmlIgnore]
    public bool IsNeverShowExt
    {
        get => _neverShowExt;
        set
        {
            if (_neverShowExt == value) return; _neverShowExt = value;
            OnP(nameof(IsNeverShowExt)); OnP(nameof(DisplayName));
        }
    }

    [XmlIgnore] public ImageSource? Icon { get; set; }

    [XmlIgnore]
    public string DisplayName
    {
        get
        {
            if (IsFolder || IsSystemIcon) return Name;
            if (IsNeverShowExt || !ShowExtension)
            {
                var n = Path.GetFileNameWithoutExtension(Name);
                return string.IsNullOrEmpty(n) ? Name : n;
            }
            return Name;
        }
    }

    [XmlIgnore] public string Extension => Path.GetExtension(Name).ToLowerInvariant();

    // ===== 组文件夹布局 =====

    [XmlIgnore] public int ChildrenCount => Children.Count;

    [XmlIgnore] public int GroupColumns => GroupColumnsFixed;

    [XmlIgnore]
    public int GroupRows => Math.Max(2, (int)Math.Ceiling(Children.Count / (double)GroupColumns));

    [XmlIgnore]
    public double GroupWidth => GroupColumns * GroupCellWidth + GroupPaddingH;

    [XmlIgnore]
    public double GroupHeight => GroupPaddingTop + GroupRows * GroupCellHeight + GroupPaddingBottom;

    [XmlIgnore]
    public double LayoutWidth => IsFolder && IsGroupMode ? GroupWidth : 96;

    [XmlIgnore]
    public double LayoutHeight => IsFolder && IsGroupMode ? GroupHeight : 110;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnP(string n) => PropertyChanged?.Invoke(this, new(n));
}

[XmlRoot("AyDesktopItems")]
public class DesktopItemCollection
{
    [XmlElement("Item")]
    public List<DesktopItem> Items { get; set; } = new();
}