using L4D2ModManager.Core.Models;

namespace L4D2ModManager.Core.Services.Mods;

/// <summary>Mod 数据库（JSON 持久化）。</summary>
public sealed class ModRepository
{
    private readonly List<ModItem> _items = new();
    private readonly object _gate = new();

    public ModRepository(string? databasePath = null)
    {
        DatabasePath = databasePath ?? AppPaths.DatabaseFile;
    }

    public string DatabasePath { get; }

    /// <summary>是否有未保存的改动。</summary>
    public bool IsDirty { get; private set; }

    public DateTime LastSavedUtc { get; private set; }

    /// <summary>当前全部 Mod（快照，拷贝列表但元素为引用）。</summary>
    public IReadOnlyList<ModItem> Mods
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    public void Load()
    {
        var document = JsonStore.Load<ModDatabaseDocument>(DatabasePath);
        lock (_gate)
        {
            _items.Clear();
            if (document?.Mods != null)
            {
                foreach (var item in document.Mods)
                {
                    if (string.IsNullOrWhiteSpace(item.FilePath)) continue;
                    // 修正 Key（老版本或手工编辑可能缺失）
                    item.Key = string.IsNullOrWhiteSpace(item.Key)
                        ? ModItem.MakeKey(item.FilePath, item.WorkshopId)
                        : item.Key;
                    item.FileIndex ??= new List<string>();
                    _items.Add(item);
                }
            }
        }
        IsDirty = false;
        Log.Info($"已加载 Mod 数据库：{_items.Count} 条记录（{DatabasePath}）");
    }

    public bool Save(bool force = false)
    {
        if (!IsDirty && !force) return true;

        ModDatabaseDocument document;
        lock (_gate)
        {
            document = new ModDatabaseDocument
            {
                GeneratedUtc = DateTime.UtcNow,
                Mods = _items.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            };
        }

        var ok = JsonStore.Save(DatabasePath, document);
        if (ok)
        {
            IsDirty = false;
            LastSavedUtc = DateTime.UtcNow;
        }
        return ok;
    }

    public ModItem? FindByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        lock (_gate)
            return _items.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public ModItem? FindByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = ModItem.ToLogicalPath(path);
        lock (_gate)
        {
            return _items.FirstOrDefault(m =>
                string.Equals(m.FilePath, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.FilePath, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public ModItem? FindByWorkshopId(string workshopId)
    {
        if (string.IsNullOrWhiteSpace(workshopId)) return null;
        lock (_gate)
            return _items.FirstOrDefault(m => string.Equals(m.WorkshopId, workshopId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按 Key 插入或更新。</summary>
    public void Upsert(ModItem item)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(m =>
                string.Equals(m.Key, item.Key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                // 保留原有 Key（可能来自历史路径）
                item.Key = string.IsNullOrWhiteSpace(item.Key) ? _items[index].Key : item.Key;
                _items[index] = item;
            }
            else
            {
                _items.Add(item);
            }
            IsDirty = true;
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            _items.RemoveAt(index);
            IsDirty = true;
            return true;
        }
    }

    /// <summary>移除磁盘上已不存在的记录（用于手动清理）。</summary>
    public int RemoveMissing()
    {
        int removed = 0;
        lock (_gate)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (File.Exists(_items[i].ActualPath)) continue;
                _items.RemoveAt(i);
                removed++;
            }
            if (removed > 0) IsDirty = true;
        }
        return removed;
    }

    public void MarkDirty()
    {
        lock (_gate) IsDirty = true;
    }
}
