using System.Numerics;
using Client.World;
using Engine.World;
using Formats.Terrain;
using ImGuiNET;
using Net;
using Shader = Engine.Rendering.Shader;

namespace Client.Online;

/// <summary>
/// The server side of the world screen: keeps the player's movement in sync, draws the creatures, players and
/// game objects the server reports with their names and combat text, and turns world clicks into targeting.
/// The interface itself is FrameXML (<see cref="InGameUi"/>).
/// </summary>
public sealed class OnlineWorld : IDisposable
{
    private const int HeartbeatMs = 500;
    private const float NameDistance = 60f;
    private const float TabRange = 40f;
    private const long FloatingMs = 1600;
    /// <summary>Beyond this camera distance item models on units are skipped.</summary>
    private const float AttachmentDistance = 80f;

    private sealed record Floating(ulong Unit, string Text, uint Color, float Scale, long StartMs, float Drift);

    private readonly GameSession _session;
    private readonly ClientData _data;
    private readonly WorldGame _game;
    private readonly Gameplay _play;
    private readonly GameText _text;
    private readonly InGameUi _ui;
    private readonly GameApi _api;
    private readonly UnitAnimator _animator;
    private readonly CharacterModels _characters;
    private readonly List<Floating> _floating = [];
    private bool _moving;
    private bool _grounded = true;
    private float _sentFacing;
    private Vector3 _previous;
    private long _nextHeartbeat;
    private long _autoFightWait;
    private Matrix4x4 _viewProjection;
    private Vector2 _screen;

    public OnlineWorld(WorldGame game, GameSession session, ClientData data, Gameplay play, GameText text, InGameUi ui)
    {
        _game = game;
        _session = session;
        _data = data;
        _play = play;
        _text = text;
        _ui = ui;
        _api = ui.Api;
        _animator = new UnitAnimator(game.Graphics, game.Assets);
        _characters = new CharacterModels(data, game.Assets);
        _api.PositionOf = PositionOf;
        _api.TargetNearest = TargetNearest;
        _api.Interact = Interact;
        _api.ZoneText = () => game.Scene?.Map.Name ?? "";
        _api.Logout = session.Logout;
        _api.Quit = session.Logout;
        ui.WorldClicked += OnWorldClicked;
        session.Teleported += OnTeleported;
        session.EnteredWorld += OnNewWorld;
        session.LoggedOut += OnLoggedOut;
        session.Disconnected += OnDisconnected;
        play.Swing += OnSwing;
        play.SpellCast += OnSpellCast;
        play.Combat += OnCombat;
        session.ChatReceived += OnChat;
    }

    private void OnChat(ChatMessage message)
    {
        if (AutoFight)
            Console.WriteLine($"[chat] {message.SenderName}: {message.Text}");
    }

    /// <summary>The world screen should hand over to a glue screen ("charselect" or "disconnected").</summary>
    public event Action<string>? ExitRequested;

    public GameSession Session => _session;
    /// <summary>Server run speed relative to the controller's base speed.</summary>
    public float SpeedScale => _session.PlayerRunSpeed / CharacterController.RunSpeed;

    public void Dispose()
    {
        _ui.WorldClicked -= OnWorldClicked;
        _session.Teleported -= OnTeleported;
        _session.EnteredWorld -= OnNewWorld;
        _session.LoggedOut -= OnLoggedOut;
        _session.Disconnected -= OnDisconnected;
        _play.Swing -= OnSwing;
        _play.SpellCast -= OnSpellCast;
        _play.Combat -= OnCombat;
        _session.ChatReceived -= OnChat;
        _animator.Dispose();
    }

    private void OnCombat(CombatEvent e)
    {
        var self = _session.PlayerGuid;
        if (AutoFight)
            Console.WriteLine($"[combat] {e.Kind} {e.Attacker:X}->{e.Victim:X} {e.Amount} {e.Outcome}");
        if (e.Attacker != self && e.Victim != self)
            return;
        var outcome = e.Outcome.Length > 0 ? _text[e.Outcome] : "";
        var drift = (Random.Shared.NextSingle() - 0.5f) * 40f;
        if (outcome.Length > 0)
            _floating.Add(new Floating(e.Victim, outcome, 0xFFFFFFFF, 1f, e.TimeMs, drift));
        else if (e.Kind is CombatKind.Heal)
            _floating.Add(new Floating(e.Victim, $"+{e.Amount}", 0xFF40FF40, e.Critical ? 1.5f : 1.1f, e.TimeMs, drift));
        else if (e.Kind is CombatKind.Energize)
            _floating.Add(new Floating(e.Victim, $"+{e.Amount}", 0xFFFFA060, 1f, e.TimeMs, drift));
        else if (e.Amount > 0)
        {
            var tint = e.Victim == self ? 0xFF3030FFu : e.Kind == CombatKind.Melee ? 0xFFFFFFFFu : 0xFF30E0FFu;
            var text = e.Victim == self ? $"-{e.Amount}" : e.Amount.ToString();
            _floating.Add(new Floating(e.Victim, text, tint, e.Critical ? 1.7f : 1.1f, e.TimeMs, drift));
        }
    }

    /// <summary>Render-space position of a unit; the player follows the local controller.</summary>
    private Vector3? PositionOf(ulong guid) =>
        guid == _session.PlayerGuid ? _game.Player.Position : _session.Objects.Get(guid) is { } obj ? WorldSpace.FromWorld(obj.Position) : null;

    private void OnSwing(ulong attacker, ulong victim)
    {
        var now = _session.NowMs;
        _animator.PlayOnce(attacker, SkinnedActor.Attack1H, now, SkinnedActor.AttackUnarmed);
        if (!_animator.IsPlayingOnce(victim) && (victim != _session.PlayerGuid || !_moving))
            _animator.PlayOnce(victim, UnitAnimator.CombatWound, now);
    }

    private void OnSpellCast(ulong caster, uint spell)
    {
        if (_text.Spell(spell) is { Passive: false, Hidden: false })
            _animator.PlayOnce(caster, SkinnedActor.SpellCastOmni, _session.NowMs, SkinnedActor.Spell);
    }

    public void Poll() => _session.Poll();

    /// <summary>Scripted test: walk to the nearest non-friendly creature, fight it, loot it and repeat.</summary>
    public bool AutoFight { get; set; }

    /// <summary>The camera yaw to run along this frame, or null to stand still.</summary>
    public float? AutoFightStep()
    {
        if (!AutoFight || _session.Objects.Get(_session.PlayerGuid) is not { IsDead: false } player)
            return null;
        var here = _game.Player.Position;
        var target = _session.Objects.Get(_play.Target);
        if (target is null || target.Type != ObjectType.Unit || (target.IsDead && !target.Lootable))
        {
            var next = _session.Objects.All
                .Where(o => o.Type == ObjectType.Unit && !o.IsDead && !o.TappedByOther && _api.ReactionOf(o) != Reaction.Friendly)
                .MinBy(o => Vector3.Distance(WorldSpace.FromWorld(o.Position), here));
            _play.Select(next?.Guid ?? 0);
            return null;
        }
        var offset = WorldSpace.FromWorld(target.Position) - here;
        offset.Y = 0;
        if (offset.Length() > 3.5f)
            return MathF.Atan2(-offset.X, -offset.Z);
        var now = _session.NowMs;
        if (now < _autoFightWait)
            return null;
        _autoFightWait = now + 700;
        if (target.IsDead)
        {
            if (_play.Loot is null)
                _play.OpenLoot(target.Guid);
            else
            {
                _play.TakeLootMoney();
                foreach (var slot in _play.Loot.Items)
                    _play.TakeLoot(slot.Index);
                _play.CloseLoot();
                _play.Select(0);
            }
            return null;
        }
        Face(target);
        if (!_play.AutoAttacking)
            _play.StartAttack(target.Guid);
        else if (player.Power >= 15 && _play.Cooldown(78).Seconds <= 0)
            _api.CastSpell(78);
        return null;
    }

    /// <summary>Sends start/stop, jump/land, facing and heartbeat packets for the frame's player movement.</summary>
    public void SyncMovement(CharacterController player)
    {
        var position = player.Position;
        var moving = Vector2.Distance(new Vector2(position.X, position.Z), new Vector2(_previous.X, _previous.Z)) > 0.001f;
        _previous = position;
        var now = _session.NowMs;

        if (moving != _moving)
        {
            _moving = moving;
            Send(moving ? Opcode.MSG_MOVE_START_FORWARD : Opcode.MSG_MOVE_STOP, player);
        }
        if (player.IsGrounded != _grounded)
        {
            _grounded = player.IsGrounded;
            Send(_grounded ? Opcode.MSG_MOVE_FALL_LAND : Opcode.MSG_MOVE_JUMP, player);
        }
        if (!moving && MathF.Abs(Orientation(player.Facing) - _sentFacing) > 0.01f)
            Send(Opcode.MSG_MOVE_SET_FACING, player);
        if (moving && now >= _nextHeartbeat)
            Send(Opcode.MSG_MOVE_HEARTBEAT, player);
    }

    private void Send(Opcode opcode, CharacterController player)
    {
        var flags = _moving ? MoveFlags.Forward : MoveFlags.None;
        var orientation = Orientation(player.Facing);
        var movement = new MovementInfo { Position = WorldSpace.ToWorld(player.Position), Orientation = orientation };
        if (opcode == Opcode.MSG_MOVE_JUMP || !player.IsGrounded)
        {
            flags |= MoveFlags.Jumping;
            movement.JumpVelocity = -CharacterController.JumpSpeed;
            movement.JumpCos = MathF.Cos(orientation);
            movement.JumpSin = MathF.Sin(orientation);
            movement.JumpXySpeed = _moving ? _session.PlayerRunSpeed : 0;
        }
        movement.Flags = flags;
        _session.SendMovement(opcode, movement);
        _sentFacing = orientation;
        _nextHeartbeat = _session.NowMs + HeartbeatMs;
    }

    /// <summary>WoW orientation in [0, 2π); the controller's facing uses the same angle.</summary>
    private static float Orientation(float facing)
    {
        var o = facing % MathF.Tau;
        return o < 0 ? o + MathF.Tau : o;
    }

    private void OnTeleported(Vector3 position, float orientation)
    {
        _game.Player.Teleport(WorldSpace.FromWorld(position));
        _game.Player.SetFacing(orientation);
        _previous = _game.Player.Position;
        _sentFacing = orientation;
    }

    private void OnNewWorld(WorldEntry entry)
    {
        if (_game.Scene?.Map.Id == entry.Map)
        {
            OnTeleported(entry.Position, entry.Orientation);
            return;
        }
        if (_data.Map((int)entry.Map) is { } map)
        {
            _game.LoadMap(_game.Maps.FirstOrDefault(m => m.Id == map.Id) ?? map, WorldSpace.FromWorld(entry.Position));
            _game.Player.SetFacing(entry.Orientation);
        }
    }

    private void OnLoggedOut() => ExitRequested?.Invoke("charselect");

    private void OnDisconnected(string reason)
    {
        Console.WriteLine($"Disconnected: {reason}");
        _session.Disconnect();
        ExitRequested?.Invoke("disconnected");
    }

    /// <summary>Draws every object the server reports; the player's own model follows the local controller.</summary>
    public void Render(WorldScene scene, Shader modelShader, Action<Vector3, float> drawBox)
    {
        _animator.BeginFrame();
        var camera = _game.Camera.Position;
        foreach (var obj in _session.Objects.All)
        {
            var self = obj.Guid == _session.PlayerGuid;
            var position = self ? _game.Player.Position : WorldSpace.FromWorld(obj.Position);
            var facing = self ? _game.Player.Facing : obj.Orientation;
            if (!DrawObject(scene, modelShader, obj, position, facing, Vector3.Distance(camera, position)) && obj.IsUnit)
                drawBox(position, facing);
        }
    }

    /// <summary>Objects the server has reported so far, and whether the player's own object is among them.</summary>
    public (int Count, bool HasPlayer) Received => (_session.Objects.All.Count(), _session.Player is not null);

    /// <summary>
    /// For the loading screen: requests everything the objects within <paramref name="radius"/> of
    /// <paramref name="focus"/> need to be drawn (model, skeleton, skins, gear) and counts how many have it all.
    /// </summary>
    public (int Ready, int Total) Preload(Vector3 focus, float radius)
    {
        var (ready, total) = (0, 0);
        foreach (var obj in _session.Objects.All)
        {
            var position = obj.Guid == _session.PlayerGuid ? focus : WorldSpace.FromWorld(obj.Position);
            if (Vector3.Distance(position, focus) > radius || LookOf(obj) is not { } look)
                continue;
            total++;
            if (IsLoaded(look))
                ready++;
        }
        return (ready, total);
    }

    private bool IsLoaded(UnitLook look)
    {
        var assets = _game.Assets;
        var loaded = Model(look.Model) & assets.TryGetSkeleton(look.Model, out _);
        if (look.Character is { } character)
        {
            _characters.Body(character);
            loaded &= _characters.IsBodySettled(character) & Texture(_characters.HairTexture(character)) &
                      Texture(_characters.CapeTexture(character));
        }
        else
            foreach (var skin in look.Display.Skins)
                loaded &= Texture(skin);
        if (look.Items is not null)
            foreach (var item in _characters.Attachments(look.Character ?? new Appearance(0, 0, 0, 0, 0, 0, 0, null, look.Items), false))
                loaded &= Model(item.Model) & Texture(item.Texture);
        return loaded;

        bool Model(string name)
        {
            if (assets.Model(name) is { } model)
                foreach (var batch in model.Data.Batches)
                    if (batch.Texture is { } texture)
                        assets.Texture(texture);
            return assets.IsModelSettled(name) &&
                   (assets.Model(name) is not { } gpu || gpu.Data.Batches.All(b => b.Texture is not { } t || assets.IsTextureSettled(t)));
        }

        bool Texture(string? name)
        {
            if (name is null)
                return true;
            assets.Texture(name);
            return assets.IsTextureSettled(name);
        }
    }

    /// <summary>What to draw for an object; <paramref name="Items"/> are the display ids of its 19 equipment slots.</summary>
    private sealed record UnitLook(string Model, float Scale, DisplayInfo Display, Appearance? Character, int[]? Items = null);

    private UnitLook? LookOf(WorldObject obj)
    {
        if (obj.Type == ObjectType.GameObject)
            return _data.GameObjectModel((int)obj.DisplayId) is { } model ? new UnitLook(model, obj.Scale, new DisplayInfo(model, 1, [], null), null) : null;
        if (!obj.IsUnit || _data.CreatureDisplay((int)obj.DisplayId) is not { } display)
            return null;
        var look = display.Extra;
        if (look is null && obj.Type == ObjectType.Player)
        {
            var bytes = obj[UpdateFields.PlayerBytes];
            var items = new int[19];
            for (var slot = 0; slot < items.Length; slot++)
            {
                var entry = obj[UpdateFields.PlayerVisibleItem1 + slot * UpdateFields.PlayerVisibleItemStride];
                items[slot] = entry == 0 ? 0 : (int)(_play.Item(entry)?.DisplayId ?? 0);
            }
            look = new Appearance(obj.Race, obj.Gender, (int)(bytes & 0xFF), (int)(bytes >> 8 & 0xFF), (int)(bytes >> 16 & 0xFF),
                (int)(bytes >> 24), (int)(obj[UpdateFields.PlayerBytes2] & 0xFF), null, items);        }
        else if (obj.Type == ObjectType.Unit)
        {
            var items = (int[])(look?.Items?.Clone() ?? new int[19]);
            for (var hand = 0; hand < 3; hand++)
                if (obj[UpdateFields.UnitVirtualItemDisplay + hand] is var id and not 0)
                    items[CharacterModels.MainHand + hand] = (int)id;
            if (look is not null)
                look = look with { Items = items };
            else if (items.Any(i => i != 0))
                return new UnitLook(display.Model, display.Scale * obj.Scale, display, null, items);
        }
        return new UnitLook(display.Model, display.Scale * obj.Scale, display, look, look?.Items);
    }

    private bool DrawObject(WorldScene scene, Shader modelShader, WorldObject obj, Vector3 position, float facing, float distance)
    {
        if (LookOf(obj) is not { } look)
            return false;
        var transform = Matrix4x4.CreateScale(look.Scale) * Matrix4x4.CreateRotationY(facing + MathF.PI / 2) * Matrix4x4.CreateTranslation(position);
        if (obj.Type == ObjectType.GameObject)
            return scene.DrawActor(modelShader, look.Model, transform);

        var actor = _animator.Pose(obj.Guid, look.Model, BaseAnimation(obj), obj.IsDead, distance, _session.NowMs);
        bool drawn;
        if (look.Character is not { } character)
            drawn = scene.DrawActor(modelShader, look.Model, transform, type => type is >= 11 and <= 13 ? look.Display.Skins[type - 11] : null, posed: actor?.Mesh);
        else
        {
            var body = _characters.Body(character);
            var hair = _characters.HairTexture(character);
            var cape = _characters.CapeTexture(character);
            drawn = scene.DrawActor(modelShader, look.Model, transform, type => type switch
            {
                1 => body,
                2 => cape is null ? null : _game.Assets.Texture(cape),
                6 => hair is null ? null : _game.Assets.Texture(hair),
                _ => null,
            }, _characters.Geosets(character), actor?.Mesh);
        }
        if (drawn && look.Items is not null && distance < AttachmentDistance && _game.Assets.TryGetModelData(look.Model, out var data) && data is not null)
        {
            var sheathed = obj.SheathState == 0 && !obj.IsDead;
            var gear = look.Character ?? new Appearance(0, 0, 0, 0, 0, 0, 0, null, look.Items);
            foreach (var item in _characters.Attachments(gear, sheathed))
            {
                if (CharacterModels.AttachmentTransform(data, item.Attachment, actor, transform) is not { } at)
                    continue;
                scene.DrawActor(modelShader, item.Model, at, item.Texture is null ? null : _ => item.Texture);
            }
        }
        return drawn;
    }

    /// <summary>The looping animation a unit shows when nothing one-off is playing.</summary>
    private int BaseAnimation(WorldObject obj)
    {
        if (obj.IsDead)
            return SkinnedActor.Dead;
        switch (obj.StandState)
        {
            case 1 or 2 or 4 or 5 or 6: return UnitAnimator.SitGround;
            case 3: return UnitAnimator.Sleep;
            case 8: return UnitAnimator.KneelLoop;
        }
        var self = obj.Guid == _session.PlayerGuid;
        var flags = obj.Movement.Flags;
        if (self ? !_game.Player.IsGrounded : (flags & (MoveFlags.Jumping | MoveFlags.FallingFar)) != 0)
            return SkinnedActor.Fall;
        var speed = self ? (_moving ? _session.PlayerRunSpeed : 0) : obj.Speed;
        if (!self && (flags & MoveFlags.Swimming) != 0)
            return speed > 0.1f ? SkinnedActor.Swim : SkinnedActor.SwimIdle;
        if (speed > 0.1f)
            return !self && (flags & MoveFlags.Backward) != 0 ? UnitAnimator.Walkbackwards : speed < 4f ? SkinnedActor.Walk : SkinnedActor.Run;
        if (_play.Casts.TryGetValue(obj.Guid, out var cast))
            return cast.Channel ? UnitAnimator.ChannelCastOmni : SkinnedActor.ReadySpellOmni;
        if (_play.Attacking.ContainsKey(obj.Guid))
            return SkinnedActor.Ready1H;
        return SkinnedActor.Stand;
    }

    /// <summary>
    /// What the engine itself draws over the world, under the interface: names above nearby units and floating combat text.
    /// Also updates the mouseover unit.
    /// </summary>
    public void DrawWorldText(Matrix4x4 viewProjection, Vector2 screen, Vector3 camera, Vector2 mouse)
    {
        _viewProjection = viewProjection;
        _screen = screen;
        _ui.SetMouseover(_ui.OwnsMouse ? 0 : Pick(viewProjection, screen, mouse, units: true)?.Guid ?? 0);
        FaceTargetWhileFighting();

        var draw = ImGui.GetBackgroundDrawList();
        var font = _ui.WorldFont;
        var fontSize = 14f * screen.Y / 768f;
        var playerLevel = (int)(_session.Player?.Level ?? 1);
        foreach (var obj in _session.Objects.All)
        {
            if (!obj.IsUnit || obj.Name is not { Length: > 0 } name)
                continue;
            var target = obj.Guid == _play.Target;
            var position = PositionOf(obj.Guid)!.Value + new Vector3(0, 2.6f * MathF.Max(1, LookOf(obj)?.Scale ?? 1), 0);
            if (Vector3.Distance(position, camera) > NameDistance && !target)
                continue;
            var clip = Vector4.Transform(new Vector4(position, 1f), viewProjection);
            if (clip.W <= 0.1f)
                continue;
            var ndc = new Vector2(clip.X, clip.Y) / clip.W;
            var head = new Vector2((ndc.X + 1) * 0.5f * screen.X, (1 - ndc.Y) * 0.5f * screen.Y);
            var reaction = _api.ReactionOf(obj);
            if (!obj.IsDead && obj.Guid != _session.PlayerGuid && (target || reaction != Reaction.Friendly))
            {
                DrawNameplate(draw, font, fontSize, obj, name, head, target, reaction, playerLevel, screen.Y / 768f);
                continue;
            }

            var size = font.CalcTextSizeA(fontSize, float.MaxValue, 0, name);
            var at = head - new Vector2(size.X / 2, 0);
            var color = obj.TappedByOther || obj.IsDead ? 0xFFA0A0A0u : obj.Type == ObjectType.Player ? 0xFFFFC080u : reaction switch
            {
                Reaction.Hostile => 0xFF4040FFu,
                Reaction.Neutral => 0xFF30FFFFu,
                _ => 0xFF40FF40u,
            };
            if (target)
                draw.AddRectFilled(at - new Vector2(4, 2), at + size + new Vector2(4, 2), 0x80000000, 3);
            draw.AddText(font, fontSize, at + Vector2.One, 0xFF000000, name);
            draw.AddText(font, fontSize, at, color, name);
        }

        var now = _session.NowMs;
        _floating.RemoveAll(f => now - f.StartMs > FloatingMs);
        foreach (var f in _floating)
        {
            if (PositionOf(f.Unit) is not { } position)
                continue;
            var t = (now - f.StartMs) / (float)FloatingMs;
            var clip = Vector4.Transform(new Vector4(position + new Vector3(0, 2.4f, 0), 1), viewProjection);
            if (clip.W <= 0.1f)
                continue;
            var ndc = new Vector2(clip.X, clip.Y) / clip.W;
            var size = fontSize * 1.5f * f.Scale * (t < 0.1f ? 1 + (0.1f - t) * 4 : 1);
            var extent = font.CalcTextSizeA(size, float.MaxValue, 0, f.Text);
            var at = new Vector2((ndc.X + 1) * 0.5f * screen.X - extent.X / 2 + f.Drift, (1 - ndc.Y) * 0.5f * screen.Y - t * 60f);
            var alpha = (uint)(Math.Clamp(1.4f - t * 1.4f, 0, 1) * 255) << 24;
            draw.AddText(font, size, at + new Vector2(1.5f), alpha, f.Text);
            draw.AddText(font, size, at, (f.Color & 0x00FFFFFF) | alpha, f.Text);
        }
    }

    private const string NameplateBorder = "Interface\\Tooltips\\Nameplate-Border.blp";
    private const string NameplateGlow = "Interface\\Tooltips\\Nameplate-Glow.blp";
    private const string NameplateBar = "Interface\\TargetingFrame\\UI-TargetingFrame-BarFill.blp";
    private const string NameplateSkull = "Interface\\TargetingFrame\\UI-TargetingFrame-Skull.blp";
    /// <summary>
    /// Layout of the 128x32 border texture, in its pixels: the health bar's hollow, the level box on its right, and
    /// the empty top half where the name goes.
    /// </summary>
    private static readonly Vector2 PlateSize = new(128, 32), PlateBarMin = new(5, 20), PlateBarMax = new(105, 27),
        PlateLevel = new(116, 23.5f);
    private const float PlateTop = 16;

    /// <summary>
    /// The client's nameplate over a unit's head: health bar colored by reaction (grey once another player tapped
    /// it), the border with the level box (a skull ten or more levels above the player), the name above, and the
    /// glow around the current target. <paramref name="scale"/> is the UI scale (screen height / 768).
    /// </summary>
    private void DrawNameplate(ImDrawListPtr draw, ImFontPtr font, float fontSize, WorldObject obj, string name, Vector2 head,
        bool target, Reaction reaction, int playerLevel, float scale)
    {
        var assets = _game.Assets;
        var min = new Vector2(head.X - (PlateBarMin.X + PlateBarMax.X) / 2 * scale, head.Y - PlateSize.Y * scale);
        Vector2 At(Vector2 texel) => min + texel * scale;

        if (target && assets.Texture("@add:" + NameplateGlow, () => assets.Image(NameplateGlow) is { } glow
                ? new Formats.Blp.RgbaImage(glow.Width, glow.Height, Ui.GlueRenderer.AdditiveToAlpha(glow.Pixels)) : null) is { } highlight)
            draw.AddImage((IntPtr)highlight.Handle, min, At(PlateSize), Vector2.Zero, Vector2.One, 0xFF00D0FF);

        var barMin = At(PlateBarMin);
        var barMax = At(PlateBarMax);
        draw.AddRectFilled(barMin, barMax, 0x80000000);
        var health = obj.MaxHealth == 0 ? 0 : Math.Clamp(obj.Health / (float)obj.MaxHealth, 0, 1);
        var color = obj.TappedByOther ? 0xFF808080u : reaction switch
        {
            Reaction.Hostile => 0xFF0000FFu,
            Reaction.Neutral => 0xFF00FFFFu,
            _ => 0xFF00FF00u,
        };
        if (health > 0 && assets.Texture(NameplateBar) is { } bar)
            draw.AddImage((IntPtr)bar.Handle, barMin, new Vector2(barMin.X + (barMax.X - barMin.X) * health, barMax.Y),
                Vector2.Zero, new Vector2(health, 1), color);
        if (assets.Texture(NameplateBorder) is { } border)
            draw.AddImage((IntPtr)border.Handle, min, At(PlateSize));

        var level = (int)obj.Level;
        var levelAt = At(PlateLevel);
        if (level >= playerLevel + 10 && assets.Texture(NameplateSkull) is { } skull)
            draw.AddImage((IntPtr)skull.Handle, levelAt - new Vector2(7 * scale), levelAt + new Vector2(7 * scale));
        else
        {
            var text = level.ToString();
            var levelSize = fontSize * 0.8f;
            var extent = font.CalcTextSizeA(levelSize, float.MaxValue, 0, text);
            var at = levelAt - extent / 2;
            draw.AddText(font, levelSize, at + Vector2.One, 0xFF000000, text);
            draw.AddText(font, levelSize, at, LevelColor(level, playerLevel), text);
        }

        var nameSize = font.CalcTextSizeA(fontSize, float.MaxValue, 0, name);
        var nameAt = new Vector2(head.X - nameSize.X / 2, min.Y + PlateTop * scale - nameSize.Y);
        draw.AddText(font, fontSize, nameAt + Vector2.One, 0xFF000000, name);
        draw.AddText(font, fontSize, nameAt, 0xFFFFFFFF, name);
    }

    /// <summary>The 1.12 difficulty color of a level relative to the player's (red, orange, yellow, green, grey; ImGui ABGR).</summary>
    private static uint LevelColor(int level, int playerLevel)
    {
        var diff = level - playerLevel;
        var grey = playerLevel <= 5 ? 0 : playerLevel <= 39 ? playerLevel - 5 - playerLevel / 10 : playerLevel - 1 - playerLevel / 5;
        return diff >= 5 ? 0xFF1A1AFFu
            : diff >= 3 ? 0xFF4080FFu
            : diff >= -2 ? 0xFF00D1FFu
            : level > grey ? 0xFF40BF40u
            : 0xFF808080u;
    }

    /// <summary>Left click selects; right click attacks, loots, talks or uses a game object.</summary>
    private void OnWorldClicked(Silk.NET.Input.MouseButton button, Vector2 mouse)
    {
        if (button == Silk.NET.Input.MouseButton.Left)
        {
            if (Pick(_viewProjection, _screen, mouse, units: true) is { } selected)
                _play.Select(selected.Guid);
        }
        else if (button == Silk.NET.Input.MouseButton.Right && Pick(_viewProjection, _screen, mouse, units: false) is { } clicked)
            Interact(clicked);
    }

    private void Interact(WorldObject obj)
    {
        if (obj.Type == ObjectType.GameObject)
        {
            _play.UseGameObject(obj.Guid);
            return;
        }
        if (obj.Guid == _session.PlayerGuid)
            return;
        _play.Select(obj.Guid);
        if (obj.IsDead)
        {
            if (obj.Lootable)
                _play.OpenLoot(obj.Guid);
            return;
        }
        if (obj.Type == ObjectType.Unit && _api.ReactionOf(obj) == Reaction.Friendly && obj.NpcFlags != 0)
            _play.Interact(obj.Guid);
        else if (_api.ReactionOf(obj) != Reaction.Friendly)
        {
            Face(obj);
            _play.StartAttack(obj.Guid);
        }
    }

    /// <summary>The nearest unit (or, for right clicks, game object) under the mouse cursor.</summary>
    private WorldObject? Pick(Matrix4x4 viewProjection, Vector2 screen, Vector2 mouse, bool units)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        var ndc = new Vector2(mouse.X / screen.X * 2 - 1, 1 - mouse.Y / screen.Y * 2);
        var near = Vector4.Transform(new Vector4(ndc, 0, 1), inverse);
        var far = Vector4.Transform(new Vector4(ndc, 1, 1), inverse);
        var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var direction = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - origin);

        WorldObject? best = null;
        var bestT = float.MaxValue;
        foreach (var obj in _session.Objects.All)
        {
            if (!(obj.IsUnit || (!units && obj.Type == ObjectType.GameObject)) || PositionOf(obj.Guid) is not { } position)
                continue;
            var look = LookOf(obj);
            var center = position + Vector3.UnitY;
            var radius = 1f;
            if (look is not null && _game.Assets.TryGetModelData(look.Model, out var data) && data is not null)
            {
                center = position + new Vector3(0, data.Center.Y * look.Scale, 0);
                radius = Math.Clamp(data.Radius * look.Scale * 0.7f, 0.6f, 12f);
            }
            var toCenter = center - origin;
            var along = Vector3.Dot(toCenter, direction);
            if (along < 0 || along > 200)
                continue;
            var miss = (toCenter - direction * along).LengthSquared();
            if (miss > radius * radius || along >= bestT)
                continue;
            best = obj;
            bestT = along;
        }
        return best;
    }

    /// <summary>Cycles through living units in front of the camera, nearest first (TargetNearestEnemy / TargetNearestFriend).</summary>
    private void TargetNearest(bool enemy, bool reverse)
    {
        var here = _game.Player.Position;
        var forward = _game.Camera.Forward;
        var candidates = _session.Objects.All
            .Where(o => o.IsUnit && !o.IsDead && o.Guid != _session.PlayerGuid && (_api.ReactionOf(o) != Reaction.Friendly) == enemy)
            .Select(o => (Unit: o, Offset: WorldSpace.FromWorld(o.Position) - here))
            .Where(c => c.Offset.Length() < TabRange && Vector3.Dot(Vector3.Normalize(c.Offset with { Y = 0 }), forward) > 0.2f)
            .OrderBy(c => c.Offset.Length())
            .Select(c => c.Unit)
            .ToList();
        if (candidates.Count == 0)
            return;
        var index = candidates.FindIndex(u => u.Guid == _play.Target);
        var next = reverse ? (index <= 0 ? candidates.Count - 1 : index - 1) : (index + 1) % candidates.Count;
        _play.Select(candidates[next].Guid);
    }

    private void Face(WorldObject target)
    {
        var here = WorldSpace.ToWorld(_game.Player.Position);
        var delta = target.Position - here;
        if (delta.X * delta.X + delta.Y * delta.Y > 0.01f)
            _game.Player.SetFacing(Orientation(MathF.Atan2(delta.Y, delta.X)));
    }

    /// <summary>Keeps the player turned toward the melee target while standing still, as the server requires.</summary>
    private void FaceTargetWhileFighting()
    {
        if (_play.AutoAttacking && !_moving && _session.Objects.Get(_play.Target) is { IsDead: false } target)
            Face(target);
    }
}
