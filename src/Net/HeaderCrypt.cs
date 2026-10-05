namespace Net;

/// <summary>
/// The 1.x world-socket header cipher: each header byte is XORed with the session key and chained with the
/// previous ciphertext byte. Packet bodies are sent in the clear.
/// </summary>
public sealed class HeaderCrypt(byte[] key)
{
    private int _sendIndex, _recvIndex;
    private byte _sendPrevious, _recvPrevious;

    public void Encrypt(Span<byte> header)
    {
        for (var t = 0; t < header.Length; t++)
        {
            _sendIndex %= key.Length;
            var x = (byte)((header[t] ^ key[_sendIndex++]) + _sendPrevious);
            header[t] = _sendPrevious = x;
        }
    }

    public void Decrypt(Span<byte> header)
    {
        for (var t = 0; t < header.Length; t++)
        {
            _recvIndex %= key.Length;
            var encrypted = header[t];
            header[t] = (byte)((encrypted - _recvPrevious) ^ key[_recvIndex++]);
            _recvPrevious = encrypted;
        }
    }
}
