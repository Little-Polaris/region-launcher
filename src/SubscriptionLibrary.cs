using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

sealed record SavedSubscription(Guid Id, string Name, string Url, bool ViaProxy, int Selected, JsonArray Nodes)
{
    public override string ToString() => Name;
    public SubscriptionResult ReadNodes() => Subscription.Parse(Nodes.ToJsonString(), cached: true);
}

sealed class SubscriptionLibrary(string folder)
{
    const int MaxLibraryBytes = 32 * 1024 * 1024;
    const int MaxSubscriptions = 32;
    string Pathname => Path.Combine(folder, "subscriptions.json");
    public List<SavedSubscription> Items { get; private set; } = [];
    public Guid? LastUsed { get; private set; }

    public void Load()
    {
        if (File.Exists(Pathname))
        {
            if (new FileInfo(Pathname).Length > MaxLibraryBytes) throw new IOException("订阅缓存超过 32 MB。");
            var state = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(Pathname));
            if (state?.Items == null || state.Items.Count > MaxSubscriptions ||
                state.Items.Any(x => x == null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name) || x.Url == null || x.Nodes == null) ||
                state.Items.Select(x => x.Id).Distinct().Count() != state.Items.Count)
                throw new IOException("订阅集合格式无效。");
            Items = state.Items; LastUsed = state.LastUsed;
            return;
        }

        string legacy = Path.Combine(folder, "subscription.json");
        if (!File.Exists(legacy)) return;
        if (new FileInfo(legacy).Length > 12 * 1024 * 1024) throw new IOException("旧订阅缓存超过大小限制。");
        var data = JsonNode.Parse(File.ReadAllText(legacy))!.AsObject();
        string url = data["url"]?.GetValue<string>() ?? "";
        var item = new SavedSubscription(Guid.NewGuid(), url.Length == 0 ? "本地导入" : "原有订阅", url,
            data["viaProxy"]?.GetValue<bool>() ?? true, data["selected"]?.GetValue<int>() ?? -1,
            data["nodes"]?.AsArray() ?? throw new IOException("旧订阅没有节点列表。"));
        Items = [item]; LastUsed = item.Id;
    }

    public SavedSubscription Save(Guid? id, string name, string url, bool viaProxy, int selected, List<SubscriptionNode> nodes)
    {
        var existing = Items.FirstOrDefault(x => x.Id == id) ??
            Items.FirstOrDefault(x => x.Url.Equals(url, StringComparison.Ordinal));
        if (Items.Any(x => x.Id != existing?.Id && x.Url.Equals(url, StringComparison.Ordinal)))
            throw new IOException("这条链接已保存在另一份订阅中。");
        name = name.Trim();
        if (name.Length == 0) name = existing?.Name ?? (url.Length == 0 ? "本地导入" : $"订阅 {Items.Count + 1}");
        if (name.Length > 60) throw new IOException("订阅名称不能超过 60 个字符。");
        var entry = new SavedSubscription(existing?.Id ?? Guid.NewGuid(), name, url, viaProxy, selected,
            new JsonArray(nodes.Select(n => (JsonNode?)n.Node.DeepClone()).ToArray()));
        // Apply the same per-subscription limit used when opening the cache.
        if (Encoding.UTF8.GetByteCount(entry.Nodes.ToJsonString()) > 12 * 1024 * 1024)
            throw new IOException("单份订阅缓存不能超过 12 MB。");
        var updated = Items.ToList();
        int index = updated.FindIndex(x => x.Id == entry.Id);
        if (index < 0) updated.Add(entry); else updated[index] = entry;
        Commit(updated, entry.Id);
        return entry;
    }

    public void Remove(Guid id)
    {
        var remaining = Items.Where(x => x.Id != id).ToList();
        Commit(remaining, LastUsed == id ? remaining.FirstOrDefault()?.Id : LastUsed);
    }

    void Commit(List<SavedSubscription> items, Guid? last)
    {
        if (items.Count > MaxSubscriptions) throw new IOException("最多保存 32 份订阅。");
        var json = JsonSerializer.Serialize(new LibraryFile(items, last));
        if (Encoding.UTF8.GetByteCount(json) > MaxLibraryBytes) throw new IOException("订阅缓存总量不能超过 32 MB。");
        Directory.CreateDirectory(folder);
        string temp = Pathname + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, json); File.Move(temp, Pathname, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Items = items; LastUsed = last;
    }

    sealed record LibraryFile(List<SavedSubscription> Items, Guid? LastUsed);
}
