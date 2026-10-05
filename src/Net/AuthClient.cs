using System.Net.Sockets;
using System.Text;

namespace Net;

/// <summary>Logon server result codes (the byte after the command in challenge/proof replies).</summary>
public enum AuthResult : byte
{
    Success = 0x00,
    Banned = 0x03,
    UnknownAccount = 0x04,
    IncorrectPassword = 0x05,
    AlreadyOnline = 0x06,
    NoTime = 0x07,
    DbBusy = 0x08,
    VersionInvalid = 0x09,
    VersionUpdate = 0x0A,
    Suspended = 0x0C,
    NoAccess = 0x0D,
    ParentControl = 0x0F,
}

[Flags]
public enum RealmFlags : byte
{
    None = 0,
    Invalid = 0x01,
    Offline = 0x02,
    SpecifyBuild = 0x04,
    NewPlayers = 0x20,
    Recommended = 0x40,
    Full = 0x80,
}

/// <summary>One entry of the logon server's realm list (pre-2.0 layout).</summary>
public sealed record RealmInfo(uint Icon, RealmFlags Flags, string Name, string Address, float Population, int Characters,
    int Category)
{
    public bool IsDown => (Flags & RealmFlags.Offline) != 0;
    public bool IsPvp => Icon is 1 or 8;
    public bool IsRp => Icon is 6 or 8;

    public (string Host, int Port) Endpoint
    {
        get
        {
            var colon = Address.LastIndexOf(':');
            return colon > 0 && int.TryParse(Address[(colon + 1)..], out var port) ? (Address[..colon], port) : (Address, 8085);
        }
    }
}

public sealed class AuthException(string token, string message) : Exception(message)
{
    /// <summary>GlueStrings key describing the failure (LOGIN_UNKNOWN_ACCOUNT, LOGIN_SERVER_DOWN, ...).</summary>
    public string Token { get; } = token;
}

/// <summary>
/// The logon (realmd) conversation: LOGON_CHALLENGE, LOGON_PROOF with SRP6, then REALM_LIST.
/// Identifies itself as the 1.12.1 (5875) Windows x86 client.
/// </summary>
public sealed class AuthClient : IAsyncDisposable
{
    public const int DefaultPort = 3724;
    public const ushort Build = 5875;
    private const byte CmdLogonChallenge = 0x00;
    private const byte CmdLogonProof = 0x01;
    private const byte CmdRealmList = 0x10;

    private readonly TcpClient _tcp = new() { NoDelay = true };
    private NetworkStream _stream = null!;

    public string Account { get; private set; } = "";
    public byte[] SessionKey { get; private set; } = [];

    public static async Task<AuthClient> ConnectAsync(string host, int port, CancellationToken cancel)
    {
        var client = new AuthClient();
        try
        {
            await client._tcp.ConnectAsync(host, port, cancel);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            await client.DisposeAsync();
            throw new AuthException("LOGIN_SERVER_DOWN", $"Cannot reach logon server {host}:{port}: {e.Message}");
        }
        client._stream = client._tcp.GetStream();
        return client;
    }

    /// <summary>Authenticates; on success <see cref="SessionKey"/> holds K for the world server.</summary>
    public async Task LoginAsync(string account, string password, string locale, CancellationToken cancel)
    {
        Account = account.ToUpperInvariant();
        var name = Encoding.UTF8.GetBytes(Account);
        var body = new PacketWriter()
            .Bytes("WoW\0"u8).U8(1).U8(12).U8(1).U16(Build)
            .Bytes(Reversed("x86")).Bytes(Reversed("Win")).Bytes(Encoding.ASCII.GetBytes(new string(locale.PadRight(4)[..4].Reverse().ToArray())))
            .U32(unchecked((uint)(int)-TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes))
            .U32(0x0100007F)
            .U8((byte)name.Length).Bytes(name)
            .ToArray();
        await WriteAsync(new PacketWriter().U8(CmdLogonChallenge).U8(3).U16((ushort)body.Length).Bytes(body).ToArray(), cancel);

        var head = await ReadAsync(3, cancel);
        if (head[0] != CmdLogonChallenge)
            throw new AuthException("LOGIN_INVALID_CHALLENGE_MESSAGE", $"Unexpected reply 0x{head[0]:X2} to logon challenge.");
        if (head[2] != (byte)AuthResult.Success)
            throw Failure((AuthResult)head[2]);

        var serverB = await ReadAsync(32, cancel);
        var gLength = (await ReadAsync(1, cancel))[0];
        var g = await ReadAsync(gLength, cancel);
        var nLength = (await ReadAsync(1, cancel))[0];
        var n = await ReadAsync(nLength, cancel);
        var salt = await ReadAsync(32, cancel);
        await ReadAsync(16, cancel);
        var securityFlags = (await ReadAsync(1, cancel))[0];
        if (securityFlags != 0)
            throw new AuthException("LOGIN_UNKNOWN_ACCOUNT_PIN", "The account requires a PIN, which this client does not support.");

        Srp6 srp;
        try
        {
            srp = Srp6.Compute(Account, password, serverB, g, n, salt);
        }
        catch (InvalidDataException e)
        {
            throw new AuthException("LOGIN_SRP_ERROR", e.Message);
        }

        var proof = new PacketWriter().U8(CmdLogonProof).Bytes(srp.PublicA).Bytes(srp.ClientProof)
            .Bytes(new byte[20]).U8(0).U8(0).ToArray();
        await WriteAsync(proof, cancel);

        var reply = await ReadAsync(2, cancel);
        if (reply[0] != CmdLogonProof)
            throw new AuthException("LOGIN_INVALID_PROOF_MESSAGE", $"Unexpected reply 0x{reply[0]:X2} to logon proof.");
        if (reply[1] != (byte)AuthResult.Success)
            throw Failure((AuthResult)reply[1]);
        var rest = await ReadAsync(24, cancel);
        if (!rest.AsSpan(0, 20).SequenceEqual(srp.ExpectedServerProof))
            throw new AuthException("LOGIN_BAD_SERVER_PROOF", "The logon server's proof does not match.");
        SessionKey = srp.SessionKey;
    }

    public async Task<IReadOnlyList<RealmInfo>> RealmListAsync(CancellationToken cancel)
    {
        await WriteAsync(new PacketWriter().U8(CmdRealmList).U32(0).ToArray(), cancel);
        var head = await ReadAsync(3, cancel);
        if (head[0] != CmdRealmList)
            throw new AuthException("REALM_LIST_INVALID", $"Unexpected reply 0x{head[0]:X2} to realm list.");
        var body = await ReadAsync(head[1] | head[2] << 8, cancel);
        return ParseRealmList(body);
    }

    public static IReadOnlyList<RealmInfo> ParseRealmList(byte[] body)
    {
        var r = new PacketReader(body);
        r.U32();
        var count = r.U8();
        var realms = new List<RealmInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var icon = r.U32();
            var flags = (RealmFlags)r.U8();
            var name = r.CString();
            var address = r.CString();
            var population = r.F32();
            var characters = r.U8();
            var category = r.U8();
            r.U8();
            realms.Add(new RealmInfo(icon, flags, name, address, population, characters, category));
        }
        return realms;
    }

    private static AuthException Failure(AuthResult result) => result switch
    {
        AuthResult.Banned => new("LOGIN_BANNED", "Account banned."),
        AuthResult.UnknownAccount or AuthResult.IncorrectPassword => new("LOGIN_UNKNOWN_ACCOUNT", "Unknown account or wrong password."),
        AuthResult.AlreadyOnline => new("LOGIN_ALREADYONLINE", "Account already online."),
        AuthResult.NoTime => new("LOGIN_NOTIME", "No game time left."),
        AuthResult.DbBusy => new("LOGIN_DBBUSY", "Logon database busy."),
        AuthResult.VersionInvalid or AuthResult.VersionUpdate => new("LOGIN_BADVERSION", "The server does not accept client build 5875."),
        AuthResult.Suspended => new("LOGIN_SUSPENDED", "Account suspended."),
        AuthResult.ParentControl => new("LOGIN_PARENTALCONTROL", "Blocked by parental controls."),
        _ => new("LOGIN_FAILED", $"Logon failed ({result})."),
    };

    private static byte[] Reversed(string fourCc)
    {
        var bytes = Encoding.ASCII.GetBytes(fourCc.PadRight(4, '\0')[..4]);
        Array.Reverse(bytes);
        // Three-letter codes are sent as "68x\0": reversed text followed by the terminator.
        return fourCc.Length == 3 ? [.. bytes[1..], 0] : bytes;
    }

    private async Task WriteAsync(byte[] data, CancellationToken cancel) => await _stream.WriteAsync(data, cancel);

    private async Task<byte[]> ReadAsync(int count, CancellationToken cancel)
    {
        var buffer = new byte[count];
        try
        {
            await _stream.ReadExactlyAsync(buffer, cancel);
        }
        catch (EndOfStreamException)
        {
            throw new AuthException("LOGIN_FAILED", "The logon server closed the connection.");
        }
        return buffer;
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
