namespace Formats.Mpq;

public enum MpqLookup { Missing, Found, Deleted }

/// <summary>Layered view over a client's Data directory; higher-priority archives shadow lower ones.</summary>
public sealed class MpqFileSystem : IDisposable
{
    // Load order used by the 1.12 client: patches first, then the base archives.
    private static readonly string[] PatchOrder = ["patch-3.MPQ", "patch-2.MPQ", "patch.MPQ"];
    private static readonly string[] BaseOrder =
    [
        "dbc.MPQ", "interface.MPQ", "model.MPQ", "texture.MPQ", "terrain.MPQ", "wmo.MPQ",
        "misc.MPQ", "fonts.MPQ", "sound.MPQ", "speech.MPQ", "speech2.MPQ", "base.MPQ", "backup.MPQ",
    ];

    private readonly List<MpqArchive> _archives;
    private IReadOnlyList<string>? _allFiles;

    public IReadOnlyList<MpqArchive> Archives => _archives;
    public string DataDirectory { get; }

    public MpqFileSystem(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        var present = Directory.GetFiles(dataDirectory, "*.mpq", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
            .ToDictionary(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

        var ordered = PatchOrder.Concat(BaseOrder).Where(present.ContainsKey).ToList();
        ordered.AddRange(present.Keys.Except(ordered, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        _archives = ordered.Select(name => new MpqArchive(present[name])).ToList();
    }

    /// <summary>Walks up from <paramref name="start"/> looking for a directory containing Data/*.MPQ.</summary>
    public static string? FindDataDirectory(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var data = System.IO.Path.Combine(dir.FullName, "Data");
            if (Directory.Exists(data) && Directory.EnumerateFiles(data, "*.MPQ").Any())
                return data;
        }
        return null;
    }

    public bool Exists(string name) => FindArchive(name) is not null;

    public byte[]? TryRead(string name) => FindArchive(name)?.TryReadFile(name);

    public byte[] Read(string name) => TryRead(name) ?? throw new FileNotFoundException($"'{name}' not found in MPQ archives.", name);

    /// <summary>Union of every archive's (listfile), minus entries that no longer resolve.</summary>
    public IReadOnlyList<string> AllFiles => _allFiles ??= _archives
        .SelectMany(a => a.ReadListFile())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(Exists)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public MpqArchive? FindArchive(string name)
    {
        foreach (var archive in _archives)
        {
            switch (archive.Lookup(name))
            {
                case MpqLookup.Found: return archive;
                case MpqLookup.Deleted: return null;
            }
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var archive in _archives)
            archive.Dispose();
    }
}
