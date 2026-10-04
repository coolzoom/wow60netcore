namespace FrameXml;

/// <summary>Client-style paths: backslash separated, case-insensitive, relative entries resolved against the including file.</summary>
public static class UiPath
{
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('/', '\\').Split('\\'))
        {
            // The loader collapses "dir\..\" pairs before opening (FrameXML.cpp, FUN_006ede10).
            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
                parts.RemoveAt(parts.Count - 1);
            else if (part.Length > 0 && part != ".")
                parts.Add(part);
        }
        return string.Join('\\', parts);
    }

    /// <summary>Directory prefix including the trailing backslash, or "" for a bare file name.</summary>
    public static string Directory(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash < 0 ? "" : path[..(slash + 1)];
    }

    public static string Combine(string directoryOf, string relative) => Normalize(Directory(directoryOf) + relative);

    public static bool HasExtension(string path, string extension) =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
