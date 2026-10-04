namespace FrameXml;

/// <summary>Severity levels used by the client's status objects when writing Logs\GlueXML.log / FrameXML.log.</summary>
public enum UiLogLevel { Info = 0, Warning = 1, Error = 2 }

public sealed record UiLogEntry(UiLogLevel Level, string Message);

public sealed class UiLog
{
    private readonly List<UiLogEntry> _entries = [];

    public IReadOnlyList<UiLogEntry> Entries => _entries;
    public IEnumerable<UiLogEntry> Problems => _entries.Where(e => e.Level != UiLogLevel.Info);
    /// <summary>Also echo warnings and errors to stderr as they happen.</summary>
    public bool Echo { get; set; }

    public void Add(UiLogLevel level, string message)
    {
        _entries.Add(new UiLogEntry(level, message));
        if (Echo && level != UiLogLevel.Info)
            Console.Error.WriteLine($"[{level}] {message}");
    }

    public void Info(string message) => Add(UiLogLevel.Info, message);
    public void Warning(string message) => Add(UiLogLevel.Warning, message);
    public void Error(string message) => Add(UiLogLevel.Error, message);

    public override string ToString() => string.Join(Environment.NewLine, _entries.Select(e => e.Level switch
    {
        UiLogLevel.Error => "ERROR: " + e.Message,
        UiLogLevel.Warning => "WARNING: " + e.Message,
        _ => e.Message,
    }));
}
