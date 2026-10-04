namespace Formats.Dbc;

/// <summary>Client database table (WDBC): fixed-size records of 32-bit fields plus a string block.</summary>
public sealed class DbcFile
{
    private const uint Magic = 0x43424457; // "WDBC"

    private readonly byte[] _data;
    private readonly int _stringBlock;

    public int RecordCount { get; }
    public int FieldCount { get; }
    public int RecordSize { get; }
    public int StringBlockSize { get; }

    public DbcFile(byte[] data)
    {
        if (data.Length < 20 || data.U32(0) != Magic)
            throw new InvalidDataException("Not a WDBC file.");
        _data = data;
        RecordCount = data.I32(4);
        FieldCount = data.I32(8);
        RecordSize = data.I32(12);
        StringBlockSize = data.I32(16);
        _stringBlock = 20 + RecordCount * RecordSize;
    }

    public int GetInt(int record, int field) => _data.I32(FieldOffset(record, field));

    public float GetFloat(int record, int field) => _data.F32(FieldOffset(record, field));

    public string GetString(int record, int field)
    {
        var offset = GetInt(record, field);
        return offset <= 0 || offset >= StringBlockSize ? "" : _data.CString(_stringBlock + offset);
    }

    /// <summary>First non-empty string among a run of localized fields.</summary>
    public string GetLocalizedString(int record, int firstField, int localeCount = 8)
    {
        for (var i = 0; i < localeCount; i++)
        {
            var value = GetString(record, firstField + i);
            if (value.Length > 0)
                return value;
        }
        return "";
    }

    private int FieldOffset(int record, int field)
    {
        if ((uint)record >= RecordCount || (uint)field >= FieldCount)
            throw new ArgumentOutOfRangeException(nameof(record), $"Record {record} field {field} is out of range.");
        return 20 + record * RecordSize + field * 4;
    }
}

public sealed record MapEntry(int Id, string Directory, string Name);

public static class MapDbc
{
    // Map.dbc in 1.12: 0 = ID, 1 = Directory, 4..11 = localized name (enUS..esMX).
    public static IReadOnlyList<MapEntry> Read(byte[] data)
    {
        var dbc = new DbcFile(data);
        return Enumerable.Range(0, dbc.RecordCount)
            .Select(r => new MapEntry(dbc.GetInt(r, 0), dbc.GetString(r, 1), dbc.GetLocalizedString(r, 4)))
            .ToList();
    }
}
