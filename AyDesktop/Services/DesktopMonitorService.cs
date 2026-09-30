using System.IO;
namespace AyDesktop.Services;

public class DesktopMonitorService : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _monitoredFolders = new();

    public event Action<string, WatcherChangeTypes>? FileChanged;

    public void AddFolder(string folderPath)
    {
        if (_monitoredFolders.Contains(folderPath)) return;

        var watcher = new FileSystemWatcher(folderPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        watcher.Created += (s, e) => OnChanged(e.FullPath, WatcherChangeTypes.Created);
        watcher.Deleted += (s, e) => OnChanged(e.FullPath, WatcherChangeTypes.Deleted);
        watcher.Renamed += (s, e) => OnRenamed(e.OldFullPath, e.FullPath);
        watcher.Changed += (s, e) => OnChanged(e.FullPath, WatcherChangeTypes.Changed);

        _watchers[folderPath] = watcher;
        _monitoredFolders.Add(folderPath);
    }

    public void RemoveFolder(string folderPath)
    {
        if (_watchers.TryGetValue(folderPath, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watchers.Remove(folderPath);
            _monitoredFolders.Remove(folderPath);
        }
    }

    private void OnChanged(string path, WatcherChangeTypes changeType)
    {
        // 在非 UI 线程触发，UI 层需通过 Dispatcher 更新
        FileChanged?.Invoke(path, changeType);
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        FileChanged?.Invoke(oldPath, WatcherChangeTypes.Deleted);
        FileChanged?.Invoke(newPath, WatcherChangeTypes.Created);
    }

    public void Dispose()
    {
        foreach (var w in _watchers.Values)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        _monitoredFolders.Clear();
    }
}