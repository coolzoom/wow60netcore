using System.Numerics;
using System.Xml.Linq;
using MoonSharp.Interpreter;

namespace FrameXml.Objects;

public sealed class Backdrop
{
    public string? BgFile { get; set; }
    public string? EdgeFile { get; set; }
    public bool Tile { get; set; }
    public float TileSize { get; set; }
    public float EdgeSize { get; set; }
    /// <summary>Left, right, top, bottom.</summary>
    public Vector4 Insets { get; set; }
    public Color4 Color { get; set; } = Color4.White;
    public Color4 BorderColor { get; set; } = Color4.White;
}

/// <summary>CSimpleFrame: scripts, events, children and layered regions.</summary>
public class Frame(UiScreen ui) : Region(ui)
{
    /// <summary>Script handlers every frame accepts (FUN_0076a0d0 plus OnEvent from the script object base).</summary>
    private static readonly string[] BaseScripts =
    [
        "OnEvent", "OnLoad", "OnSizeChanged", "OnUpdate", "OnShow", "OnHide", "OnEnter", "OnLeave", "OnMouseDown",
        "OnMouseUp", "OnMouseWheel", "OnDragStart", "OnDragStop", "OnReceiveDrag", "OnChar", "OnKeyDown", "OnKeyUp",
    ];

    private readonly Dictionary<string, DynValue> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private FrameStrata _strata = FrameStrata.Parent;
    private int? _level;
    private bool _showFired;

    public List<Frame> Children { get; } = [];
    public List<LayeredRegion> Regions { get; } = [];
    public HashSet<string> Events { get; } = new(StringComparer.OrdinalIgnoreCase);
    public float Alpha { get; set; } = 1f;
    public int Id { get; set; }
    public bool Toplevel { get; set; }
    public bool Movable { get; set; }
    public bool Resizable { get; set; }
    public bool MouseEnabled { get; set; }
    public bool KeyboardEnabled { get; set; }
    public bool MouseWheelEnabled { get; set; }
    public bool ClampedToScreen { get; set; }
    public Backdrop? Backdrop { get; set; }
    /// <summary>Left, right, top, bottom; positive values shrink the clickable area.</summary>
    public Vector4 HitRectInsets { get; set; }
    /// <summary>Mouse buttons that start OnDragStart (RegisterForDrag), e.g. "LeftButton".</summary>
    public HashSet<string> DragButtons { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Creation order, used to break ties between frames of the same strata and level.</summary>
    public int Serial { get; internal set; }

    public override string ObjectType => "Frame";
    public override bool IsVisible => Shown && (Parent?.IsVisible ?? true);
    public override float EffectiveAlpha => Alpha * (Parent?.EffectiveAlpha ?? 1f);

    public FrameStrata Strata
    {
        get => _strata == FrameStrata.Parent ? Parent?.Strata ?? FrameStrata.Medium : _strata;
        set => _strata = value;
    }

    public int Level
    {
        get => _level ?? (Parent is null ? 0 : Parent.Level + 1);
        set => _level = value;
    }

    protected virtual IEnumerable<string> ScriptNames => BaseScripts;

    public bool SupportsScript(string name) => ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public DynValue? GetScript(string name) => _scripts.GetValueOrDefault(name);

    public void SetScript(string name, DynValue? handler)
    {
        if (handler is null || handler.IsNil())
            _scripts.Remove(name);
        else
            _scripts[name] = handler;
    }

    public bool HasHandler(string name) => _scripts.ContainsKey(name);

    public void RunScript(string name, params DynValue[] args)
    {
        if (_scripts.TryGetValue(name, out var handler))
            Ui.RunScript(this, name, handler, args);
    }

    public void SetParent(Frame? parent)
    {
        if (Parent == parent)
            return;
        Parent?.Children.Remove(this);
        Parent = parent;
        parent?.Children.Add(this);
        Ui.InvalidateLayout();
    }

    public void Show()
    {
        if (Shown)
            return;
        Shown = true;
        if (Parent?.IsVisible ?? true)
            BecameVisible();
    }

    public void Hide()
    {
        if (!Shown)
            return;
        var wasVisible = IsVisible;
        Shown = false;
        if (wasVisible)
            BecameHidden();
    }

    /// <summary>
    /// Visible children are shown before the frame's own OnShow runs: AccountLogin_OnShow focuses the account box
    /// only after both auto-focus edit boxes have taken focus.
    /// </summary>
    private void BecameVisible()
    {
        _showFired = true;
        foreach (var child in Children.ToList())
            if (child.Shown)
                child.BecameVisible();
        OnVisible();
        RunScript("OnShow");
    }

    private void BecameHidden()
    {
        foreach (var child in Children.ToList())
            if (child.Shown)
                child.BecameHidden();
        OnHidden();
        RunScript("OnHide");
    }

    protected virtual void OnVisible() { }
    protected virtual void OnHidden() { }

    public void RegisterEvent(string name) => Ui.RegisterEvent(this, name);
    public void UnregisterEvent(string name) => Ui.UnregisterEvent(this, name);

    /// <summary>Whether a layered region should draw now (buttons swap state textures).</summary>
    public virtual bool IsRegionActive(LayeredRegion region) => region.Shown;

    public T AddRegion<T>(T region, DrawLayer layer) where T : LayeredRegion
    {
        region.Parent = this;
        region.Layer = layer;
        Regions.Add(region);
        return region;
    }

    /// <summary>CSimpleFrame::LoadXML (FUN_00769820). Inherited templates are applied first.</summary>
    internal virtual void LoadXml(XElement node, UiLoader loader)
    {
        if (node.Attr("inherits") is { Length: > 0 } inherits)
        {
            if (Ui.FindVirtual(inherits) is { } template)
                LoadXml(template, loader);
            else
                loader.Log.Warning($"Couldn't find inherited node: {inherits}");
        }

        LoadLayout(node, loader);
        if (node.Attr("hidden") is { Length: > 0 } hidden)
            Shown = !UiEnums.Bool(hidden);
        if (node.Attr("toplevel") is { Length: > 0 } toplevel) Toplevel = UiEnums.Bool(toplevel);
        if (node.Attr("movable") is { Length: > 0 } movable) Movable = UiEnums.Bool(movable);
        if (node.Attr("resizable") is { Length: > 0 } resizable) Resizable = UiEnums.Bool(resizable);
        if (node.Attr("frameStrata") is { Length: > 0 } strataName)
        {
            if (UiEnums.TryStrata(strataName, out var strata))
                Strata = strata;
            else
                loader.Log.Warning($"Frame {Name ?? "<unnamed>"}: Unknown frame strata: {strataName}");
        }
        if (node.Attr("frameLevel") is { Length: > 0 } levelText)
        {
            if (int.TryParse(levelText, out var level) && level > 0)
                Level = level;
            else
                loader.Log.Warning($"Frame {Name ?? "<unnamed>"}: Unknown frame level: {levelText}");
        }
        if (node.Attr("alpha") is { Length: > 0 } alpha) Alpha = Math.Clamp(UiEnums.Float(alpha, 1f), 0f, 1f);
        if (node.Attr("id") is { Length: > 0 } id && int.TryParse(id, out var idValue)) Id = idValue;
        if (UiEnums.Bool(node.Attr("enableMouse"))) MouseEnabled = true;
        if (UiEnums.Bool(node.Attr("enableKeyboard"))) KeyboardEnabled = true;
        if (node.Attr("clampedToScreen") is { Length: > 0 } clamped) ClampedToScreen = UiEnums.Bool(clamped);

        foreach (var child in node.Elements())
        {
            switch (child.Tag().ToUpperInvariant())
            {
                case "BACKDROP":
                    Backdrop = loader.LoadBackdrop(child);
                    break;
                case "HITRECTINSETS":
                    if (loader.Inset(child) is { } insets)
                        HitRectInsets = insets;
                    break;
                case "LAYERS":
                    LoadLayers(child, loader);
                    break;
                case "SCRIPTS":
                    LoadScripts(child, loader);
                    break;
            }
        }
    }

    private void LoadLayers(XElement layers, UiLoader loader)
    {
        foreach (var layerNode in layers.Elements())
        {
            if (!layerNode.Is("Layer"))
            {
                loader.Log.Warning($"Frame {Name ?? "<unnamed>"}: Unknown child node in {layers.Tag()} element: {layerNode.Tag()}");
                continue;
            }
            var layer = DrawLayer.Artwork;
            if (layerNode.Attr("level") is { Length: > 0 } level && !UiEnums.TryLayer(level, out layer))
                layer = DrawLayer.Artwork;
            foreach (var regionNode in layerNode.Elements())
            {
                if (regionNode.Is("Texture"))
                    loader.CreateTexture(regionNode, this, layer);
                else if (regionNode.Is("FontString"))
                    loader.CreateFontString(regionNode, this, layer);
                else
                    loader.Log.Warning($"Frame {Name ?? "<unnamed>"}: Unknown child node in {layerNode.Tag()} element: {regionNode.Tag()}");
            }
        }
    }

    /// <summary>Compiles each handler body as a chunk named "Frame:Script" (FUN_007025c0) and enables input it needs (FUN_00769ef0).</summary>
    private void LoadScripts(XElement scripts, UiLoader loader)
    {
        foreach (var script in scripts.Elements())
        {
            var name = script.Tag();
            if (!SupportsScript(name))
            {
                loader.Log.Warning($"Frame {Name ?? "<unnamed>"}: Unknown script element {name}");
                continue;
            }
            var body = script.Value;
            if (string.IsNullOrWhiteSpace(body))
                SetScript(name, null);
            else
                SetScript(name, Ui.Lua.Compile(body, $"{Name ?? "<unnamed>"}:{name}"));

            switch (name.ToUpperInvariant())
            {
                case "ONCHAR":
                case "ONKEYDOWN":
                case "ONKEYUP":
                    KeyboardEnabled = true;
                    break;
                case "ONENTER":
                case "ONLEAVE":
                case "ONMOUSEDOWN":
                case "ONMOUSEUP":
                case "ONDRAGSTART":
                    MouseEnabled = true;
                    break;
                case "ONMOUSEWHEEL":
                    MouseWheelEnabled = true;
                    break;
            }
        }
    }

    /// <summary>
    /// FUN_0076a2f0: create &lt;Frames&gt; children (inherited ones first), run OnLoad, then OnShow if the frame
    /// is visible and OnLoad didn't already trigger it.
    /// </summary>
    internal virtual void PostLoadXml(XElement node, UiLoader loader)
    {
        _showFired = false;
        CreateChildFrames(node, loader);
        RunScript("OnLoad");
        if (IsVisible && !_showFired)
        {
            OnVisible();
            RunScript("OnShow");
        }
    }

    private void CreateChildFrames(XElement node, UiLoader loader)
    {
        if (node.Attr("inherits") is { Length: > 0 } inherits && Ui.FindVirtual(inherits) is { } template)
            CreateChildFrames(template, loader);
        if (node.Child("Frames") is { } frames)
            foreach (var child in frames.Elements())
                loader.CreateFrame(child, this);
    }
}
