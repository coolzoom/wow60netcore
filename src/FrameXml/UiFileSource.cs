using Formats.Mpq;

namespace FrameXml;

public interface IUiFileSource
{
    byte[]? Read(string path);
    bool Exists(string path);

    /// <summary>Immediate subdirectory names of <paramref name="directory"/> (used to enumerate AddOns).</summary>
    IEnumerable<string> Directories(string directory);
}

/// <summary>
/// MPQ archives with optional loose files from the game directory layered on top. The retail client renames
/// loose Interface\GlueXML and Interface\FrameXML folders to *.old at startup (AddOns.cpp, FUN_0051fb50), so
/// loose overrides only take effect on clients where that check is patched out.
/// </summary>
public sealed class MpqUiFileSource(MpqFileSystem files, string? looseRoot = null) : IUiFileSource
{
    public byte[]? Read(string path)
    {
        if (looseRoot is not null && FindLoose(path) is { } loose)
            return File.ReadAllBytes(loose);
        return files.TryRead(path);
    }

    public bool Exists(string path) => looseRoot is not null && FindLoose(path) is not null || files.Exists(UiPath.Normalize(path));

    public IEnumerable<string> Directories(string directory)
    {
        var prefix = UiPath.Normalize(directory) + "\\";
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.AllFiles)
        {
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var slash = file.IndexOf('\\', prefix.Length);
            if (slash > prefix.Length)
                names.Add(file[prefix.Length..slash]);
        }
        if (looseRoot is not null && FindLoose(directory) is { } dir && System.IO.Directory.Exists(dir))
            foreach (var sub in System.IO.Directory.EnumerateDirectories(dir))
                names.Add(Path.GetFileName(sub));
        return names;
    }

    /// <summary>Resolves each path segment case-insensitively so Windows-style names work on case-sensitive disks.</summary>
    private string? FindLoose(string path)
    {
        var current = looseRoot!;
        foreach (var segment in UiPath.Normalize(path).Split('\\'))
        {
            var exact = Path.Combine(current, segment);
            if (File.Exists(exact) || System.IO.Directory.Exists(exact))
            {
                current = exact;
                continue;
            }
            if (!System.IO.Directory.Exists(current))
                return null;
            var match = System.IO.Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(e => Path.GetFileName(e).Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return null;
            current = match;
        }
        return File.Exists(current) || System.IO.Directory.Exists(current) ? current : null;
    }
}

/// <summary>In-memory files for tests and tools.</summary>
public sealed class MemoryUiFileSource : IUiFileSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public MemoryUiFileSource Add(string path, string text)
    {
        _files[UiPath.Normalize(path)] = System.Text.Encoding.UTF8.GetBytes(text);
        return this;
    }

    public byte[]? Read(string path) => _files.GetValueOrDefault(UiPath.Normalize(path));

    public bool Exists(string path) => _files.ContainsKey(UiPath.Normalize(path));

    public IEnumerable<string> Directories(string directory)
    {
        var prefix = UiPath.Normalize(directory) + "\\";
        return _files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', prefix.Length) > prefix.Length)
            .Select(k => k[prefix.Length..k.IndexOf('\\', prefix.Length)])
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
