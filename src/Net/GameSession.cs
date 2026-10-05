using System.Collections.Concurrent;
using System.Numerics;

namespace Net;

public enum SessionState
{
    Disconnected,
    Authenticating,
    RealmList,
    ConnectingWorld,
    CharacterSelect,
    EnteringWorld,
    InWorld,
}

/// <summary>Where the server put the player: on login, and again after every map change.</summary>
public sealed record WorldEntry(uint Map, Vector3 Position, float Orientation);

public sealed record ChatMessage(byte Type, uint Language, ulong Sender, string SenderName, string Channel, string Text)
{
    public const byte Say = 0x00, Party = 0x01, Guild = 0x03, Yell = 0x05, Whisper = 0x06, WhisperInform = 0x07, Emote = 0x08,
        TextEmote = 0x09, System = 0x0A, MonsterSay = 0x0B, MonsterYell = 0x0C, MonsterEmote = 0x0D, ChannelMessage = 0x0E,
        MonsterWhisper = 0x1A;

    public static ChatMessage Parse(byte[] body)
    {
        var r = new PacketReader(body);
        var type = r.U8();
        var language = r.U32();
        ulong sender = 0;
        var name = "";
        var channel = "";
        switch (type)
        {
            case MonsterWhisper or MonsterEmote:
                r.U32();
                name = r.CString();
                r.U64();
                break;
            case Say or Party or Yell:
                sender = r.U64();
                r.U64();
                break;
            case MonsterSay or MonsterYell:
                sender = r.U64();
                r.U32();
                name = r.CString();
                r.U64();
                break;
            case ChannelMessage:
                channel = r.CString();
                r.U32();
                sender = r.U64();
                break;
            default:
                sender = r.U64();
                break;
        }
        r.U32();
        var text = r.CString();
        return new ChatMessage(type, language, sender, name, channel, text);
    }
}

/// <summary>
/// One player's connection: logon server (SRP6, realm list), then a world server (characters, entering the world,
/// movement and object updates). Network work runs on background tasks; results and packets are delivered on the
/// thread that calls <see cref="Poll"/>, so every event fires on the game thread.
/// </summary>
public sealed class GameSession : IDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<Action> _completions = new();
    private readonly Dictionary<Opcode, Action<WorldPacket>> _handlers = [];
    private readonly Dictionary<ulong, string> _playerNames = [];
    private readonly Dictionary<uint, string> _creatureNames = [];
    private readonly HashSet<ulong> _nameQueries = [];
    private readonly HashSet<uint> _creatureQueries = [];
    private readonly long _startTicks = Environment.TickCount64;
    private CancellationTokenSource _operation = new();
    private AuthClient? _auth;
    private WorldSocket? _world;
    private DateTime _nextPing;
    private uint _pingSequence;
    private MovementInfo _playerMovement;

    public GameSession()
    {
        _handlers[Opcode.SMSG_CHAR_ENUM] = p =>
        {
            Characters = CharacterInfo.ParseEnum(p.Body);
            State = SessionState.CharacterSelect;
            CharactersReceived?.Invoke();
        };
        _handlers[Opcode.SMSG_CHAR_CREATE] = p => CharacterCreated?.Invoke((ResponseCode)p.Body[0]);
        _handlers[Opcode.SMSG_CHAR_DELETE] = p => CharacterDeleted?.Invoke((ResponseCode)p.Body[0]);
        _handlers[Opcode.SMSG_CHARACTER_LOGIN_FAILED] = p =>
        {
            State = SessionState.CharacterSelect;
            Failed?.Invoke(((ResponseCode)p.Body[0]).ToString());
        };
        _handlers[Opcode.SMSG_LOGIN_VERIFY_WORLD] = p => EnterMap(p.Reader());
        _handlers[Opcode.SMSG_NEW_WORLD] = p =>
        {
            Objects.Clear();
            EnterMap(p.Reader());
            Send(Opcode.MSG_MOVE_WORLDPORT_ACK, []);
        };
        _handlers[Opcode.SMSG_UPDATE_OBJECT] = p => ApplyUpdate(p.Body);
        _handlers[Opcode.SMSG_COMPRESSED_UPDATE_OBJECT] = p => ApplyUpdate(ObjectStore.Inflate(p.Body));
        _handlers[Opcode.SMSG_DESTROY_OBJECT] = p => Objects.Remove(p.Reader().U64());
        _handlers[Opcode.SMSG_MONSTER_MOVE] = p =>
        {
            var move = MonsterMove.Parse(p.Body);
            Objects.Get(move.Guid)?.SetPath(move, NowMs);
        };
        _handlers[Opcode.SMSG_COMPRESSED_MOVES] = p => HandleCompressedMoves(ObjectStore.Inflate(p.Body));
        _handlers[Opcode.MSG_MOVE_TELEPORT_ACK] = HandleTeleport;
        foreach (var force in new[] { Opcode.SMSG_FORCE_RUN_SPEED_CHANGE, Opcode.SMSG_FORCE_RUN_BACK_SPEED_CHANGE, Opcode.SMSG_FORCE_SWIM_SPEED_CHANGE,
                     Opcode.SMSG_FORCE_WALK_SPEED_CHANGE, Opcode.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE, Opcode.SMSG_FORCE_TURN_RATE_CHANGE })
            _handlers[force] = p => HandleForcedSpeed(p, force + 1);
        _handlers[Opcode.SMSG_FORCE_MOVE_ROOT] = p => AcknowledgeRoot(p, true);
        _handlers[Opcode.SMSG_FORCE_MOVE_UNROOT] = p => AcknowledgeRoot(p, false);
        _handlers[Opcode.SMSG_NAME_QUERY_RESPONSE] = p =>
        {
            var r = p.Reader();
            var guid = r.U64();
            var name = r.CString();
            _playerNames[guid] = name;
            if (Objects.Get(guid) is { } obj)
                obj.Name = name;
        };
        _handlers[Opcode.SMSG_CREATURE_QUERY_RESPONSE] = p =>
        {
            var r = p.Reader();
            var entry = r.U32();
            if ((entry & 0x80000000) != 0)
                return;
            var name = r.CString();
            _creatureNames[entry] = name;
            foreach (var obj in Objects.All.Where(o => o.Type == ObjectType.Unit && o.Entry == entry))
                obj.Name = name;
        };
        _handlers[Opcode.SMSG_MESSAGECHAT] = p =>
        {
            var message = ChatMessage.Parse(p.Body);
            if (message.SenderName == "" && message.Sender != 0)
                message = message with { SenderName = _playerNames.GetValueOrDefault(message.Sender) ?? Objects.Get(message.Sender)?.Name ?? "" };
            ChatReceived?.Invoke(message);
        };
        _handlers[Opcode.SMSG_NOTIFICATION] = p => SystemMessage(p.Reader().CString());
        _handlers[Opcode.SMSG_SERVER_MESSAGE] = p =>
        {
            var r = p.Reader();
            r.U32();
            SystemMessage(r.CString());
        };
        _handlers[Opcode.SMSG_LOGOUT_COMPLETE] = _ =>
        {
            Objects.Clear();
            State = SessionState.CharacterSelect;
            LoggedOut?.Invoke();
        };

        Objects.Created += obj =>
        {
            if (obj.Type == ObjectType.Player)
                QueryName(obj);
            else if (obj.Type == ObjectType.Unit)
                QueryCreature(obj);
        };
    }

    public SessionState State { get; private set; }
    public string Account { get; private set; } = "";
    public IReadOnlyList<RealmInfo> Realms { get; private set; } = [];
    public RealmInfo? Realm { get; private set; }
    public IReadOnlyList<CharacterInfo> Characters { get; private set; } = [];
    public ulong PlayerGuid { get; private set; }
    public WorldEntry? Location { get; private set; }
    public ObjectStore Objects { get; } = new();
    public bool IsWorldConnected => _world is { IsOpen: true };
    public long NowMs => Environment.TickCount64 - _startTicks;
    public WorldObject? Player => Objects.Get(PlayerGuid);
    public float PlayerRunSpeed { get; private set; } = 7f;
    /// <summary>The server holds the player in place (logging out, stunned, ...).</summary>
    public bool Rooted { get; private set; }

    /// <summary>Progress messages as GlueStrings keys (LOGIN_STATE_CONNECTING, CHAR_LIST_RETRIEVING, ...).</summary>
    public event Action<string>? Progress;
    /// <summary>The current operation failed; the argument is a GlueStrings key or a ResponseCode name.</summary>
    public event Action<string>? Failed;
    public event Action? RealmsReceived;
    public event Action? CharactersReceived;
    public event Action<ResponseCode>? CharacterCreated;
    public event Action<ResponseCode>? CharacterDeleted;
    public event Action<WorldEntry>? EnteredWorld;
    /// <summary>The server moved the player within the map (GM teleport, knockback, ...).</summary>
    public event Action<Vector3, float>? Teleported;
    public event Action<ChatMessage>? ChatReceived;
    public event Action? LoggedOut;
    /// <summary>The world connection dropped; the argument describes why.</summary>
    public event Action<string>? Disconnected;

    /// <summary>Authenticates with the logon server and fetches the realm list.</summary>
    public void Login(string host, int port, string account, string password, string locale)
    {
        Disconnect();
        Account = account.ToUpperInvariant();
        State = SessionState.Authenticating;
        Run(async cancel =>
        {
            Report("LOGIN_STATE_CONNECTING");
            var auth = await AuthClient.ConnectAsync(host, port, cancel);
            Post(() => _auth = auth);
            Report("LOGIN_STATE_AUTHENTICATING");
            await auth.LoginAsync(account, password, locale, cancel);
            Report("REALM_LIST_IN_PROGRESS");
            var realms = await auth.RealmListAsync(cancel);
            Post(() =>
            {
                Realms = realms;
                State = SessionState.RealmList;
                RealmsReceived?.Invoke();
            });
        });
    }

    /// <summary>Refreshes the realm list over the still-open logon connection.</summary>
    public void RequestRealmList()
    {
        if (_auth is not { } auth)
        {
            RealmsReceived?.Invoke();
            return;
        }
        Run(async cancel =>
        {
            var realms = await auth.RealmListAsync(cancel);
            Post(() =>
            {
                Realms = realms;
                RealmsReceived?.Invoke();
            });
        }, background: true);
    }

    /// <summary>Connects to a realm's world server and asks for the character list.</summary>
    public void ConnectRealm(RealmInfo realm)
    {
        if (_auth is not { } auth)
            return;
        CloseWorld();
        Realm = realm;
        State = SessionState.ConnectingWorld;
        var (host, port) = realm.Endpoint;
        Run(async cancel =>
        {
            Report("LOGIN_STATE_CONNECTING");
            WorldSocket world;
            try
            {
                world = await WorldSocket.ConnectAsync(host, port, cancel);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
            {
                throw new AuthException("REALM_LIST_REALM_NOT_FOUND", $"Cannot reach world server {host}:{port}: {e.Message}");
            }
            Report("LOGIN_STATE_HANDSHAKING");
            var result = await world.AuthenticateAsync(auth.Account, auth.SessionKey, cancel);
            if (result != ResponseCode.AUTH_OK)
            {
                world.Dispose();
                throw new AuthException(result.ToString(), $"World server refused the session: {result}.");
            }
            world.StartReceiving();
            Post(() =>
            {
                _world = world;
                _nextPing = DateTime.UtcNow + PingInterval;
                RequestCharacters();
            });
        });
    }

    public void RequestCharacters()
    {
        Progress?.Invoke("CHAR_LIST_RETRIEVING");
        Send(Opcode.CMSG_CHAR_ENUM, []);
    }

    public void CreateCharacter(CharacterCreateInfo info)
    {
        Progress?.Invoke("CHAR_CREATE_IN_PROGRESS");
        Send(Opcode.CMSG_CHAR_CREATE, info.Serialize());
    }

    public void DeleteCharacter(ulong guid)
    {
        Progress?.Invoke("CHAR_DELETE_IN_PROGRESS");
        Send(Opcode.CMSG_CHAR_DELETE, new PacketWriter().U64(guid).ToArray());
    }

    public void EnterWorld(ulong guid)
    {
        PlayerGuid = guid;
        Rooted = false;
        State = SessionState.EnteringWorld;
        Objects.Clear();
        Progress?.Invoke("CHAR_LOGIN_IN_PROGRESS");
        Send(Opcode.CMSG_PLAYER_LOGIN, new PacketWriter().U64(guid).ToArray());
    }

    public void Logout() => Send(Opcode.CMSG_LOGOUT_REQUEST, []);

    /// <summary>Sends a movement opcode for the player (position in WoW world coordinates).</summary>
    public void SendMovement(Opcode opcode, MovementInfo movement)
    {
        movement.Time = (uint)NowMs;
        if (Rooted)
            movement.Flags = (movement.Flags & ~MoveFlags.Moving) | MoveFlags.Root;
        _playerMovement = movement;
        var w = new PacketWriter();
        movement.Write(w);
        Send(opcode, w.ToArray());
    }

    public void SendChat(byte type, string text, string? target = null)
    {
        var w = new PacketWriter().U32(type).U32(PlayerLanguage);
        if (type is ChatMessage.Whisper or ChatMessage.ChannelMessage)
            w.CString(target ?? "");
        Send(Opcode.CMSG_MESSAGECHAT, w.CString(text).ToArray());
    }

    public string? NameOf(ulong guid) => _playerNames.GetValueOrDefault(guid);

    /// <summary>Cancels the pending login/connect step.</summary>
    public void Cancel()
    {
        _operation.Cancel();
        _operation = new CancellationTokenSource();
        if (State is SessionState.Authenticating)
            Disconnect();
        else if (State is SessionState.ConnectingWorld)
        {
            CloseWorld();
            State = SessionState.RealmList;
        }
    }

    public void Disconnect()
    {
        _operation.Cancel();
        _operation = new CancellationTokenSource();
        CloseWorld();
        if (_auth is { } auth)
            _ = auth.DisposeAsync().AsTask();
        _auth = null;
        State = SessionState.Disconnected;
    }

    /// <summary>Runs queued completions and dispatches received packets. Call once per frame.</summary>
    public void Poll()
    {
        while (_completions.TryDequeue(out var completion))
            completion();

        if (_world is not { } world)
            return;
        while (world.TryDequeue(out var packet))
            Dispatch(packet);

        foreach (var obj in Objects.All)
            obj.Advance(NowMs);

        if (!world.IsOpen)
        {
            var reason = world.ClosedReason ?? "connection closed";
            CloseWorld();
            State = _auth is null ? SessionState.Disconnected : SessionState.RealmList;
            Disconnected?.Invoke(reason);
            return;
        }
        if (DateTime.UtcNow >= _nextPing)
        {
            _nextPing = DateTime.UtcNow + PingInterval;
            Send(Opcode.CMSG_PING, new PacketWriter().U32(++_pingSequence).U32(0).ToArray());
        }
    }

    private void Dispatch(WorldPacket packet)
    {
        if (!_handlers.TryGetValue(packet.Opcode, out var handler))
        {
            if (packet.Opcode is not (>= Opcode.MSG_MOVE_START_FORWARD and <= Opcode.MSG_MOVE_SET_FACING or Opcode.MSG_MOVE_HEARTBEAT))
                return;
            handler = HandleOtherMovement;
        }
        try
        {
            handler(packet);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"Bad {packet.Opcode} ({packet.Body.Length} bytes): {e.Message}");
        }
    }

    private void EnterMap(PacketReader r)
    {
        var map = r.U32();
        var position = r.Vector3();
        var entry = new WorldEntry(map, position, r.F32());
        Location = entry;
        _playerMovement = new MovementInfo { Position = entry.Position, Orientation = entry.Orientation };
        // The server ignores movement until the client confirms which unit it moves.
        Send(Opcode.CMSG_SET_ACTIVE_MOVER, new PacketWriter().U64(PlayerGuid).ToArray());
        State = SessionState.InWorld;
        EnteredWorld?.Invoke(entry);
    }

    private void ApplyUpdate(byte[] body)
    {
        Objects.ApplyUpdate(body, NowMs);
        if (Player is { } player)
            PlayerRunSpeed = player.RunSpeed;
    }

    private void HandleOtherMovement(WorldPacket packet)
    {
        var r = packet.Reader();
        var guid = r.PackedGuid();
        if (guid == PlayerGuid || Objects.Get(guid) is not { } obj)
            return;
        obj.SetMovement(MovementInfo.Read(r), NowMs);
    }

    private void HandleCompressedMoves(byte[] data)
    {
        var r = new PacketReader(data);
        while (r.Remaining > 3)
        {
            var size = r.U8();
            var opcode = (Opcode)r.U16();
            var body = r.Bytes(size - 2).ToArray();
            Dispatch(new WorldPacket(opcode, body));
        }
    }

    private void HandleTeleport(WorldPacket packet)
    {
        var r = packet.Reader();
        var guid = r.PackedGuid();
        var counter = r.U32();
        var movement = MovementInfo.Read(r);
        if (guid != PlayerGuid)
            return;
        _playerMovement = movement;
        Send(Opcode.MSG_MOVE_TELEPORT_ACK, new PacketWriter().U64(guid).U32(counter).U32((uint)NowMs).ToArray());
        Teleported?.Invoke(movement.Position, movement.Orientation);
    }

    private void HandleForcedSpeed(WorldPacket packet, Opcode ack)
    {
        var r = packet.Reader();
        var guid = r.PackedGuid();
        var counter = r.U32();
        var speed = r.F32();
        if (guid != PlayerGuid)
            return;
        if (packet.Opcode == Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)
            PlayerRunSpeed = speed;
        var w = new PacketWriter().U64(guid).U32(counter);
        (_playerMovement with { Time = (uint)NowMs }).Write(w);
        Send(ack, w.F32(speed).ToArray());
    }

    private void AcknowledgeRoot(WorldPacket packet, bool rooted)
    {
        var r = packet.Reader();
        var guid = r.PackedGuid();
        var counter = r.U32();
        if (guid != PlayerGuid)
            return;
        Rooted = rooted;
        _playerMovement.Flags = rooted ? (_playerMovement.Flags & ~MoveFlags.Moving) | MoveFlags.Root : _playerMovement.Flags & ~MoveFlags.Root;
        var ack = rooted ? Opcode.CMSG_FORCE_MOVE_ROOT_ACK : Opcode.CMSG_FORCE_MOVE_UNROOT_ACK;
        var w = new PacketWriter().U64(guid).U32(counter);
        (_playerMovement with { Time = (uint)NowMs }).Write(w);
        Send(ack, w.ToArray());
    }

    private void QueryName(WorldObject obj)
    {
        if (_playerNames.TryGetValue(obj.Guid, out var name))
            obj.Name = name;
        else if (_nameQueries.Add(obj.Guid))
            Send(Opcode.CMSG_NAME_QUERY, new PacketWriter().U64(obj.Guid).ToArray());
    }

    private void QueryCreature(WorldObject obj)
    {
        if (_creatureNames.TryGetValue(obj.Entry, out var name))
            obj.Name = name;
        else if (obj.Entry != 0 && _creatureQueries.Add(obj.Entry))
            Send(Opcode.CMSG_CREATURE_QUERY, new PacketWriter().U32(obj.Entry).U64(obj.Guid).ToArray());
    }

    /// <summary>Common for the Alliance races, Orcish for the Horde.</summary>
    private uint PlayerLanguage => Characters.FirstOrDefault(c => c.Guid == PlayerGuid)?.Race is 2 or 5 or 6 or 8 ? 1u : 7u;

    public void SystemMessage(string text) => ChatReceived?.Invoke(new ChatMessage(ChatMessage.System, 0, 0, "", "", text));

    /// <summary>Handles an opcode the session itself does not (combat, spells, items, ...).</summary>
    public void On(Opcode opcode, Action<WorldPacket> handler) => _handlers[opcode] = handler;

    public void Send(Opcode opcode, byte[] body) => _world?.Send(opcode, body);

    private void Report(string token) => Post(() => Progress?.Invoke(token));

    private void Post(Action action) => _completions.Enqueue(action);

    private void Run(Func<CancellationToken, Task> work, bool background = false)
    {
        var cancel = _operation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await work(cancel);
            }
            catch (OperationCanceledException)
            {
            }
            catch (AuthException e)
            {
                if (!background)
                    Post(() => Fail(e.Token, e.Message));
            }
            catch (Exception e)
            {
                if (!background)
                    Post(() => Fail("LOGIN_FAILED", e.Message));
            }
        });
    }

    private void Fail(string token, string message)
    {
        Console.Error.WriteLine($"Network: {message}");
        if (State is SessionState.Authenticating)
            Disconnect();
        else if (State is SessionState.ConnectingWorld)
            State = SessionState.RealmList;
        Failed?.Invoke(token);
    }

    private void CloseWorld()
    {
        _world?.Dispose();
        _world = null;
        Objects.Clear();
    }

    public void Dispose() => Disconnect();
}
