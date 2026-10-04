namespace FrameXml;

public sealed record AddOnInfo(string Name, string TocPath, TocFile Toc)
{
    public string Title => Toc["Title"] ?? Name;
    public string? Notes => Toc["Notes"];
    public bool LoadOnDemand => Toc.LoadOnDemand;
    public bool Secure => Name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase);
    public bool Enabled { get; set; } = true;
    public bool Loaded { get; set; }
}

/// <summary>AddOn discovery and loading (AddOns.cpp: FUN_0051c9b0, FUN_0051f240, FUN_0051f600).</summary>
public static class AddOns
{
    public const string Directory = @"Interface\AddOns";
    public const int InterfaceVersion = 11200;

    /// <summary>Every Interface\AddOns\Name\Name.toc.</summary>
    public static List<AddOnInfo> Discover(IUiFileSource files)
    {
        var result = new List<AddOnInfo>();
        foreach (var name in files.Directories(Directory))
        {
            var toc = $@"{Directory}\{name}\{name}.toc";
            if (files.Read(toc) is { } data)
                result.Add(new AddOnInfo(name, toc, TocFile.Parse(data)));
        }
        return result;
    }

    /// <summary>Loads required dependencies first; out-of-date AddOns (wrong "## Interface:") are skipped.</summary>
    public static bool Load(UiScreen ui, AddOnInfo addOn, List<AddOnInfo>? all = null, bool loadOutOfDate = false)
    {
        if (addOn.Loaded)
            return true;
        if (!loadOutOfDate && !addOn.Secure && addOn.Toc.InterfaceVersion != InterfaceVersion)
        {
            ui.Log.Warning($"AddOn {addOn.Name} is out of date (interface {addOn.Toc.InterfaceVersion})");
            return false;
        }
        addOn.Loaded = true;
        all ??= Discover(ui.Files);
        foreach (var dependency in addOn.Toc.Dependencies)
        {
            var required = all.FirstOrDefault(a => a.Name.Equals(dependency, StringComparison.OrdinalIgnoreCase));
            if (required is null || !required.Enabled || !Load(ui, required, all, loadOutOfDate))
            {
                ui.Log.Warning($"AddOn {addOn.Name} is missing dependency {dependency}");
                addOn.Loaded = false;
                return false;
            }
        }
        foreach (var optional in addOn.Toc.OptionalDependencies)
            if (all.FirstOrDefault(a => a.Name.Equals(optional, StringComparison.OrdinalIgnoreCase)) is { Enabled: true } dependency)
                Load(ui, dependency, all, loadOutOfDate);

        ui.Loader.LoadToc(addOn.TocPath);
        ui.FireEvent("ADDON_LOADED", addOn.Name);
        return true;
    }
}
