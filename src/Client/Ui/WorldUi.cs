using System.Numerics;
using System.Text;
using Client.World;
using Engine.World;
using Formats.Dbc;
using Formats.Models;
using Formats.Terrain;
using ImGuiNET;

namespace Client.Ui;

/// <summary>Debug UI: world/map controls and an MPQ file browser with previews.</summary>
public sealed class WorldUi(WorldGame game, string? initialFilter = null)
{
    private static readonly string[] TextExtensions = [".lua", ".xml", ".toc", ".txt", ".wtf", ".html", ".htm"];
    /// <summary>Slider maxima that stand for "no limit".</summary>
    private const float MaxDoodadDistance = 3000f;
    private const int MaxLoadRadius = 8;

    private int _mapIndex = -1;
    private Vector3 _teleport;
    private string _filter = initialFilter ?? "";
    private bool _selectFirstMatch = initialFilter is not null;
    private string _appliedFilter = "\0";
    private List<string> _filtered = [];
    private string? _selected;
    private Preview? _preview;
    private float _fps;
    private readonly float[] _frameTimes = new float[240];
    private int _frameIndex;
    private bool _showFps = true;

    private sealed record Preview(string Name, long Size, string Archive, string Details, string? Text);

    public void Draw(float dt)
    {
        _fps = _fps * 0.95f + (dt > 0 ? 1f / dt : 0) * 0.05f;
        _frameTimes[_frameIndex] = dt * 1000f;
        _frameIndex = (_frameIndex + 1) % _frameTimes.Length;
        DrawWorldWindow();
        DrawFileBrowser();
        if (_showFps)
            DrawFpsOverlay();
        if (Touch)
            DrawTouchControls(dt);
    }

    private float Scale => game.Options.UiScale;
    private bool Touch => game.Options.TouchControls;

    /// <summary>Hold-to-move buttons (bottom left) and zoom (bottom right); dragging elsewhere turns the camera.</summary>
    private void DrawTouchControls(float dt)
    {
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        var viewport = ImGui.GetMainViewport();
        var button = new Vector2(ImGui.GetFontSize() * 3.2f);
        bool Hold(string label)
        {
            ImGui.Button(label, button);
            return ImGui.IsItemActive();
        }

        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(20 * Scale, viewport.WorkSize.Y - 20 * Scale), ImGuiCond.Always, new Vector2(0, 1));
        ImGui.SetNextWindowBgAlpha(0.25f);
        var move = Vector2.Zero;
        var vertical = 0f;
        var jump = false;
        if (ImGui.Begin("##move", flags))
        {
            ImGui.Dummy(button);
            ImGui.SameLine();
            if (Hold("前")) move.Y += 1;
            ImGui.SameLine();
            if (Hold(game.Flying ? "升" : "跳"))
            {
                if (game.Flying) vertical += 1;
                else jump = true;
            }

            if (Hold("左")) move.X -= 1;
            ImGui.SameLine();
            if (ImGui.Button(game.Flying ? "步行" : "飞行", button))
                game.Flying = !game.Flying;
            ImGui.SameLine();
            if (Hold("右")) move.X += 1;

            ImGui.Dummy(button);
            ImGui.SameLine();
            if (Hold("后")) move.Y -= 1;
            ImGui.SameLine();
            if (game.Flying && Hold("降")) vertical -= 1;
        }
        ImGui.End();
        game.TouchMove = move;
        game.TouchVertical = vertical;
        game.TouchJump = jump;

        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize - new Vector2(20 * Scale), ImGuiCond.Always, new Vector2(1, 1));
        ImGui.SetNextWindowBgAlpha(0.25f);
        if (ImGui.Begin("##zoom", flags))
        {
            if (Hold("拉近")) game.Zoom(-25f * dt);
            if (Hold("拉远")) game.Zoom(25f * dt);
        }
        ImGui.End();
    }

    private void DrawFpsOverlay()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(viewport.WorkSize.X - 10, 10), ImGuiCond.Always, new Vector2(1, 0));
        ImGui.SetNextWindowBgAlpha(0.6f);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        if (!ImGui.Begin("##fps", flags))
        {
            ImGui.End();
            return;
        }

        var samples = _frameTimes.Where(t => t > 0).ToArray();
        var average = samples.Length > 0 ? samples.Average() : 0f;
        var worst = samples.Length > 0 ? samples.Max() : 0f;
        var color = _fps >= 55 ? new Vector4(0.4f, 1f, 0.4f, 1f) : _fps >= 30 ? new Vector4(1f, 0.85f, 0.3f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f);

        ImGui.SetWindowFontScale(1.6f);
        ImGui.TextColored(color, $"{_fps:F0} FPS");
        ImGui.SetWindowFontScale(1f);
        ImGui.Text($"帧时间 平均 {average:F1} ms  最慢 {worst:F1} ms");
        ImGui.PlotLines("##frametimes", ref _frameTimes[0], _frameTimes.Length, _frameIndex, null, 0f, MathF.Max(50f, worst), new Vector2(220, 40) * Scale);
        var vsync = game.VSync;
        if (ImGui.Checkbox("垂直同步", ref vsync))
            game.VSync = vsync;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启时帧率被锁定在显示器刷新率, 测性能时请关闭");
        ImGui.End();
    }

    private void DrawWorldWindow()
    {
        ImGui.SetNextWindowPos(new Vector2(10, 10) * Scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(330, 0) * Scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowCollapsed(Touch, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("世界"))
        {
            ImGui.End();
            return;
        }

        var scene = game.Scene!;
        var maps = game.Maps;
        if (_mapIndex < 0)
            _mapIndex = maps.ToList().FindIndex(m => m.Id == scene.Map.Id);

        var names = maps.Select(m => $"{m.Name} ({m.Directory})").ToArray();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##map", ref _mapIndex, names, names.Length) && _mapIndex >= 0)
            game.LoadMap(maps[_mapIndex]);

        var world = WorldSpace.ToWorld(game.Player.Position);
        var tile = WorldSpace.TileAt(game.Player.Position.X, game.Player.Position.Z);
        ImGui.Text($"坐标 X {world.X:F1}  Y {world.Y:F1}  Z {world.Z:F1}");
        ImGui.Text($"地块 {tile.X}_{tile.Y}   FPS {_fps:F0}");
        ImGui.SameLine();
        ImGui.Checkbox("FPS 面板", ref _showFps);

        ImGui.SetNextItemWidth(240 * Scale);
        ImGui.InputFloat3("##tp", ref _teleport, "%.1f");
        ImGui.SameLine();
        if (ImGui.Button("传送"))
            game.TeleportWorld(_teleport.X, _teleport.Y, _teleport.Z == 0 ? null : _teleport.Z);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("世界坐标 X Y Z (与游戏内 .gps 相同), Z 为 0 时落到最高处");

        ImGui.Separator();
        var settings = game.Settings;
        bool doodads = settings.Doodads, wmos = settings.MapObjects, distant = settings.DistantTerrain;
        bool flying = game.Flying, fog = game.FogEnabled;
        var distance = float.IsPositiveInfinity(settings.DoodadDistance) ? MaxDoodadDistance : settings.DoodadDistance;
        var radius = settings.LoadRadius == RenderSettings.AllTiles ? MaxLoadRadius : settings.LoadRadius;
        ImGui.Checkbox("飞行 (空格上升 / Shift 下降)", ref flying);
        var speed = game.SpeedMultiplier;
        ImGui.SetNextItemWidth(ImGui.CalcItemWidth() - 50 * Scale);
        if (ImGui.SliderFloat("移动速度", ref speed, 1f, 100f, "%.0f 倍", ImGuiSliderFlags.Logarithmic | ImGuiSliderFlags.AlwaysClamp))
            game.SpeedMultiplier = speed;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"跑步 {CharacterController.RunSpeed * speed:F0} 码/秒, 飞行 {CharacterController.FlySpeed * speed:F0} 码/秒\nCtrl+点击可直接输入数值");
        ImGui.SameLine();
        if (ImGui.SmallButton("重置"))
            game.SpeedMultiplier = 1f;
        ImGui.Checkbox("显示物件 (M2)", ref doodads);
        ImGui.SameLine();
        ImGui.Checkbox("显示建筑 (WMO)", ref wmos);
        ImGui.Checkbox("雾效", ref fog);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("关闭后视距扩展到整张地图");
        ImGui.SameLine();
        ImGui.Checkbox("远景地形 (WDL + 小地图)", ref distant);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("用低精度高度图和小地图贴图画出整张地图, 未细节加载的地块显示为远景");

        ImGui.SliderFloat("物件距离", ref distance, 50f, MaxDoodadDistance, distance >= MaxDoodadDistance ? "无限" : "%.0f");
        ImGui.SliderInt("加载半径", ref radius, 0, MaxLoadRadius, radius >= MaxLoadRadius ? "全部" : "%d");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("细节地块半径 (地块约 533 码)。\"全部\" 会加载整张地图的所有地块和物件,\n东部王国约 700 块, 需要数 GB 显存和较长时间, 仅用于压力测试。");
        game.Flying = flying;
        game.FogEnabled = fog;
        game.Settings = new RenderSettings(doodads, wmos,
            distance >= MaxDoodadDistance ? float.PositiveInfinity : distance,
            radius >= MaxLoadRadius ? RenderSettings.AllTiles : radius,
            distant);

        var stats = scene.Stats;
        ImGui.Separator();
        ImGui.Text($"地块 {stats.Tiles}/{scene.Wdt.Tiles().Count()} (加载中 {stats.PendingTiles})  远景 {stats.DistantTiles}  物件 {stats.Doodads}  建筑 {stats.MapObjects}");
        ImGui.Text($"绘制调用 {stats.DrawCalls}  纹理 {game.Assets.TextureCount}  模型 {game.Assets.ModelCount}  排队 {game.Assets.PendingCount}");
        ImGui.Text($"显存估算 {(stats.TileBytes + game.Assets.Bytes) / (1024.0 * 1024.0):F0} MB  (地形 {stats.TileBytes / (1024.0 * 1024.0):F0} MB, 贴图/模型 {game.Assets.Bytes / (1024.0 * 1024.0):F0} MB)");
        ImGui.Text($"托管内存 {GC.GetTotalMemory(false) / (1024.0 * 1024.0):F0} MB");
        ImGui.TextDisabled("WASD 移动, 鼠标拖动旋转, 滚轮缩放, Q/E 转向");
        ImGui.End();
    }

    private void DrawFileBrowser()
    {
        ImGui.SetNextWindowPos(new Vector2(10, 330) * Scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(520, 420) * Scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowCollapsed(Touch, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MPQ 文件"))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "过滤 (例如 .m2 tree、Interface\\Glues)", ref _filter, 256);
        if (_filter != _appliedFilter)
        {
            _appliedFilter = _filter;
            var terms = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _filtered = game.Files.AllFiles
                .Where(f => terms.All(t => f.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (_selectFirstMatch && _filtered.Count > 0)
            {
                Select(_filtered[0]);
                _selectFirstMatch = false;
            }
        }
        ImGui.Text($"{_filtered.Count} 个文件");

        ImGui.BeginChild("list", new Vector2(ImGui.GetContentRegionAvail().X * 0.5f, 0), ImGuiChildFlags.Border);
        unsafe
        {
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(_filtered.Count);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var name = _filtered[i];
                    if (ImGui.Selectable(name, name == _selected))
                        Select(name);
                }
            }
            clipper.End();
            clipper.Destroy();
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("preview", Vector2.Zero, ImGuiChildFlags.Border);
        DrawPreview();
        ImGui.EndChild();
        ImGui.End();
    }

    private void Select(string name)
    {
        _selected = name;
        var data = game.Files.TryRead(name);
        var archive = Path.GetFileName(game.Files.FindArchive(name)?.Path ?? "?");
        if (data is null)
        {
            _preview = new Preview(name, 0, archive, "无法读取", null);
            return;
        }

        string details;
        string? text = null;
        try
        {
            details = Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".m2" => DescribeM2(new M2Model(data)),
                ".wmo" => DescribeWmo(name, data),
                ".adt" => DescribeAdt(data),
                ".dbc" => DescribeDbc(new DbcFile(data), out text),
                ".blp" => "",
                _ => "",
            };
        }
        catch (Exception e)
        {
            details = $"解析失败: {e.Message}";
        }

        if (TextExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            text = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 16 * 1024));
        _preview = new Preview(name, data.Length, archive, details, text);
    }

    private void DrawPreview()
    {
        if (_preview is not { } preview)
        {
            ImGui.TextDisabled("选择左侧文件查看内容");
            return;
        }

        ImGui.TextWrapped(preview.Name);
        ImGui.Text($"{preview.Size:N0} 字节  来自 {preview.Archive}");
        if (preview.Details.Length > 0)
            ImGui.TextWrapped(preview.Details);

        var extension = Path.GetExtension(preview.Name).ToLowerInvariant();
        if (extension == ".blp")
        {
            if (game.Assets.Texture(preview.Name) is { } texture)
            {
                ImGui.Text($"{texture.Width} x {texture.Height}");
                var width = Math.Min(ImGui.GetContentRegionAvail().X, Math.Max(texture.Width, 64));
                ImGui.Image((IntPtr)texture.Handle, new Vector2(width, width * texture.Height / texture.Width));
            }
            else
            {
                ImGui.TextDisabled("解码中...");
            }
        }

        if ((extension == ".m2" || (extension == ".wmo" && !IsWmoGroup(preview.Name))) && ImGui.Button("放到角色前方"))
            game.PlaceModelInFront(preview.Name);

        if (preview.Text is { } text)
            ImGui.TextUnformatted(text);
    }

    private static bool IsWmoGroup(string name) =>
        name.Length > 8 && name[^8] == '_' && name[^7..^4].All(char.IsDigit);

    private static string DescribeM2(M2Model model) =>
        $"M2 模型 \"{model.Name}\"\n顶点 {model.Positions.Length}  三角形 {model.Indices.Length / 3}  批次 {model.Batches.Count}\n" +
        string.Join("\n", model.Batches.Select(b => b.Texture ?? "(运行时替换纹理)").Distinct());

    private string DescribeWmo(string name, byte[] data)
    {
        if (IsWmoGroup(name))
            return "WMO 组文件 (随根文件加载)";
        var wmo = new WmoModel(data, i => game.Files.TryRead(WmoModel.GroupFileName(name, i)));
        return $"WMO 建筑\n组 {wmo.Groups.Count}  材质 {wmo.Materials.Count}  三角形 {wmo.Groups.Sum(g => g.Indices.Length / 3)}";
    }

    private static string DescribeAdt(byte[] data)
    {
        var adt = new AdtFile(data, 0, 0);
        return $"ADT 地形块\n区块 {adt.Chunks.Count}  纹理 {adt.Textures.Count}  物件 {adt.Doodads.Count}  建筑 {adt.MapObjects.Count}\n" +
               string.Join("\n", adt.Textures);
    }

    private static string DescribeDbc(DbcFile dbc, out string? text)
    {
        var rows = new StringBuilder();
        for (var r = 0; r < Math.Min(dbc.RecordCount, 50); r++)
        {
            var fields = Enumerable.Range(0, dbc.FieldCount).Select(f =>
            {
                var value = dbc.GetInt(r, f);
                var s = value > 0 ? dbc.GetString(r, f) : "";
                return s.Length > 0 ? $"\"{s}\"" : value.ToString();
            });
            rows.AppendLine(string.Join(" | ", fields));
        }
        text = rows.ToString();
        return $"DBC 表  记录 {dbc.RecordCount}  字段 {dbc.FieldCount}  记录大小 {dbc.RecordSize}";
    }
}
