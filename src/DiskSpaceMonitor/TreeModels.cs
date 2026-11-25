using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskSpaceMonitor;

/// <summary>
/// One node of the agent's pruned disk tree. Kinds: "d" directory, "f" file,
/// "o" folded small items of a directory, "u" space the scan could not see.
/// Short JSON names keep wsl-tree.json small.
/// </summary>
public sealed class DiskTreeNode
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("s")] public long Size { get; set; }
    [JsonPropertyName("k")] public string Kind { get; set; } = "d";
    [JsonPropertyName("e")] public string? Extension { get; set; }
    [JsonPropertyName("fc")] public long FileCount { get; set; }
    [JsonPropertyName("c")] public List<DiskTreeNode> Children { get; set; } = new();
    [JsonIgnore] public DiskTreeNode? Parent { get; private set; }
    [JsonIgnore] public bool IsDirectory => Kind == "d";

    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        "o" => $"<мелкие объекты: {FileCount:N0}>",
        "u" => "<не учтено: нет доступа, метаданные ФС>",
        _ => Name
    };

    [JsonIgnore]
    public string TypeName => Kind switch
    {
        "d" => "Каталог",
        "o" => "Мелкие объекты",
        "u" => "Не учтено",
        _ => Extension ?? ""
    };

    [JsonIgnore]
    public string FullPath
    {
        get
        {
            var names = new List<string>();
            for (var node = this; node is not null; node = node.Parent) names.Add(node.Parent is null ? node.Name : node.DisplayName);
            names.Reverse();
            var root = names[0].TrimEnd('/');
            return names.Count == 1 ? names[0] : root + "/" + string.Join('/', names.Skip(1));
        }
    }

    /// <summary>Children are sorted by size, so the first-child chain is the biggest leaf.</summary>
    public DiskTreeNode DominantLeaf()
    {
        var node = this;
        while (node.Children.Count > 0) node = node.Children[0];
        return node;
    }

    public IEnumerable<DiskTreeNode> PathFromRoot()
    {
        var chain = new List<DiskTreeNode>();
        for (var node = this; node is not null; node = node.Parent) chain.Add(node);
        chain.Reverse();
        return chain;
    }

    /// <summary>Iterative: deep trees (node_modules) must not overflow the stack.</summary>
    internal void LinkParents()
    {
        var stack = new Stack<DiskTreeNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children) { child.Parent = node; stack.Push(child); }
        }
    }
}

public sealed class ExtensionStat
{
    public string Ext { get; set; } = "";
    public long Size { get; set; }
    public long Count { get; set; }
}

public sealed class DiskTreeSnapshot
{
    // Each tree level is an object plus a "c" array: the default depth of 64 would
    // reject ordinary nested paths such as node_modules chains.
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, MaxDepth = 1024 };

    public int SchemaVersion { get; set; }
    public string? RequestId { get; set; }
    public string Root { get; set; } = "/";
    public DateTimeOffset? Timestamp { get; set; }
    public double DurationSeconds { get; set; }
    public long ScannedBytes { get; set; }
    public long ScannedFiles { get; set; }
    public long UnaccountedBytes { get; set; }
    public int ErrorCount { get; set; }
    public List<string> ErrorSamples { get; set; } = new();
    public List<ExtensionStat> Extensions { get; set; } = new();
    public DiskTreeNode? Tree { get; set; }

    public static DiskTreeSnapshot Parse(string json)
    {
        var result = JsonSerializer.Deserialize<DiskTreeSnapshot>(json, Options) ?? throw new InvalidDataException("Пустой файл дерева");
        if (result.SchemaVersion != 1 || result.Tree is null) throw new InvalidDataException("Неподдерживаемая схема дерева диска");
        result.Tree.LinkParents();
        return result;
    }
}

/// <summary>Progress of the agent's tree scan, embedded in wsl-metrics.json.</summary>
public sealed class TreeScanStatus
{
    public string State { get; set; } = "idle";
    public string? RequestId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long Files { get; set; }
    public long Bytes { get; set; }
    public string? Error { get; set; }
}

public static class Units
{
    public static string Bytes(double bytes) => bytes >= 1L << 30 ? $"{bytes / (1L << 30):F2} GiB" :
        bytes >= 1L << 20 ? $"{bytes / (1L << 20):F1} MiB" : bytes >= 1024 ? $"{bytes / 1024:F0} KiB" : $"{bytes:F0} Б";
}
