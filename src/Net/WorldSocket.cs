using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Net;

public sealed record WorldPacket(Opcode Opcode, byte[] Body)
{
    public PacketReader Reader() => new(Body);
}

/// <summary>
/// TCP connection to the world server. Server headers are a big-endian u16 size and a u16 opcode, client
/// headers a big-endian u16 size and a u32 opcode; both are encrypted with <see cref="HeaderCrypt"/> once the
/// session is authenticated. Packets are received on a background task and queued for the game thread.
/// </summary>
public sealed class WorldSocket : IDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly ConcurrentQueue<WorldPacket> _incoming = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private NetworkStream _stream = null!;
    private HeaderCrypt? _crypt;
    private volatile string? _closedReason;

    /// <summary>Set when the connection ends; null while it is open.</summary>
    public string? ClosedReason => _closedReason;
    public bool IsOpen => _closedReason is null;

    public static async Task<WorldSocket> ConnectAsync(string host, int port, CancellationToken cancel)
    {
        var socket = new WorldSocket();
        try
        {
            await socket._tcp.ConnectAsync(host, port, cancel);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        socket._stream = socket._tcp.GetStream();
        return socket;
    }

    /// <summary>
    /// The handshake: SMSG_AUTH_CHALLENGE, CMSG_AUTH_SESSION with SHA1(account | 0 | clientSeed | serverSeed | K),
    /// then header encryption with K. Returns the server's SMSG_AUTH_RESPONSE code.
    /// </summary>
    public async Task<ResponseCode> AuthenticateAsync(string account, byte[] sessionKey, CancellationToken cancel)
    {
        var challenge = await ReceiveAsync(cancel);
        if (challenge.Opcode != Opcode.SMSG_AUTH_CHALLENGE)
            throw new IOException($"Expected SMSG_AUTH_CHALLENGE, got {challenge.Opcode}.");
        var serverSeed = challenge.Reader().U32();
        var clientSeed = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));

        var name = account.ToUpperInvariant();
        var digest = SHA1.HashData(new PacketWriter().Bytes(Encoding.UTF8.GetBytes(name)).U32(0).U32(clientSeed).U32(serverSeed)
            .Bytes(sessionKey).ToArray());
        var session = new PacketWriter().U32(AuthClient.Build).U32(0).CString(name).U32(clientSeed).Bytes(digest)
            .U32(0);
        await SendAsync(Opcode.CMSG_AUTH_SESSION, session.ToArray(), cancel);
        _crypt = new HeaderCrypt(sessionKey);

        var response = await ReceiveAsync(cancel);
        if (response.Opcode != Opcode.SMSG_AUTH_RESPONSE)
            throw new IOException($"Expected SMSG_AUTH_RESPONSE, got {response.Opcode}.");
        return (ResponseCode)response.Body[0];
    }

    /// <summary>Starts the background receive loop; afterwards read packets with <see cref="TryDequeue"/>.</summary>
    public void StartReceiving() => _ = Task.Run(ReceiveLoop);

    public bool TryDequeue(out WorldPacket packet) => _incoming.TryDequeue(out packet!);

    public void Send(Opcode opcode, byte[] body)
    {
        if (!IsOpen)
            return;
        _ = SendAndObserve(opcode, body);
    }

    private async Task SendAndObserve(Opcode opcode, byte[] body)
    {
        try
        {
            await SendAsync(opcode, body, _closing.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Close($"send failed: {e.Message}");
        }
    }

    public async Task SendAsync(Opcode opcode, byte[] body, CancellationToken cancel)
    {
        var packet = new byte[6 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)(body.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), (uint)opcode);
        body.CopyTo(packet, 6);
        await _sendLock.WaitAsync(cancel);
        try
        {
            _crypt?.Encrypt(packet.AsSpan(0, 6));
            await _stream.WriteAsync(packet, cancel);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<WorldPacket> ReceiveAsync(CancellationToken cancel)
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header, cancel);
        _crypt?.Decrypt(header);
        var size = BinaryPrimitives.ReadUInt16BigEndian(header);
        var opcode = (Opcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        if (size < 2)
            throw new IOException($"Invalid packet size {size}.");
        var body = new byte[size - 2];
        await _stream.ReadExactlyAsync(body, cancel);
        return new WorldPacket(opcode, body);
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (!_closing.IsCancellationRequested)
                _incoming.Enqueue(await ReceiveAsync(_closing.Token));
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException or EndOfStreamException)
        {
            Close(e is EndOfStreamException ? "server closed the connection" : e.Message);
        }
    }

    public void Close(string reason)
    {
        _closedReason ??= reason;
        _closing.Cancel();
        _tcp.Close();
    }

    public void Dispose()
    {
        Close("disposed");
        _tcp.Dispose();
        _closing.Dispose();
        _sendLock.Dispose();
    }
}
