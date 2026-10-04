using System.Text;

namespace FrameXml;

/// <summary>
/// A table-of-contents file. Mirrors FUN_006edb90 (file list) and FUN_0051c9b0 (AddOn metadata):
/// a UTF-8 BOM is skipped, lines starting with '#' are not files, "## Key: Value" lines are metadata,
/// and trailing spaces are trimmed from file entries.
/// </summary>
public sealed class TocFile
{
    public IReadOnlyList<string> Files { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public TocFile(IReadOnlyList<string> files, IReadOnlyDictionary<string, string> metadata)
    {
        Files = files;
        Metadata = metadata;
    }

    public static TocFile Parse(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        var files = new List<string>();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line[0] != '#')
            {
                var entry = line.TrimEnd(' ');
                if (entry.Length > 0)
                    files.Add(entry);
                continue;
            }
            if (line.Length < 2 || line[1] != '#')
                continue;
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var key = line[2..colon].Trim();
            if (key.Length > 0)
                metadata[key] = line[(colon + 1)..].Trim();
        }
        return new TocFile(files, metadata);
    }

    public string? this[string key] => Metadata.GetValueOrDefault(key);

    public int? InterfaceVersion => int.TryParse(this["Interface"], out var v) ? v : null;
    public bool LoadOnDemand => this["LoadOnDemand"] is "1";

    /// <summary>Required dependencies: "RequiredDeps", "Dependencies" or any key starting with "Dep".</summary>
    public IReadOnlyList<string> Dependencies => List(Metadata
        .Where(m => m.Key.StartsWith("RequiredDep", StringComparison.OrdinalIgnoreCase) || m.Key.StartsWith("Dep", StringComparison.OrdinalIgnoreCase))
        .Select(m => m.Value));

    public IReadOnlyList<string> OptionalDependencies => List(Metadata
        .Where(m => m.Key.StartsWith("OptionalDep", StringComparison.OrdinalIgnoreCase))
        .Select(m => m.Value));

    public IReadOnlyList<string> SavedVariables => List([this["SavedVariables"] ?? ""]);

    private static List<string> List(IEnumerable<string> values) => values
        .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToList();
}
