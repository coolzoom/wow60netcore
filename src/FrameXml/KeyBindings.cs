using System.Xml.Linq;
using MoonSharp.Interpreter;

namespace FrameXml;

/// <summary>
/// The &lt;Binding&gt; commands of Bindings.xml and the keys bound to them. A key is the client's key name with
/// modifier prefixes ("1", "SHIFT-2", "CTRL-TAB"); the command's Lua body runs with the global keystate "down",
/// and "up" too when the binding has runOnUp.
/// </summary>
public sealed class KeyBindings
{
    private sealed record Command(string Name, string? Header, bool RunOnUp, DynValue? Body);

    private readonly List<Command> _commands = [];
    private readonly Dictionary<string, Command> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default keys of a fresh 1.12 install (the client has no bindings file until the player changes one).</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["1"] = "ACTIONBUTTON1", ["2"] = "ACTIONBUTTON2", ["3"] = "ACTIONBUTTON3", ["4"] = "ACTIONBUTTON4", ["5"] = "ACTIONBUTTON5",
        ["6"] = "ACTIONBUTTON6", ["7"] = "ACTIONBUTTON7", ["8"] = "ACTIONBUTTON8", ["9"] = "ACTIONBUTTON9", ["0"] = "ACTIONBUTTON10",
        ["-"] = "ACTIONBUTTON11", ["="] = "ACTIONBUTTON12",
        ["SHIFT-1"] = "ACTIONPAGE1", ["SHIFT-2"] = "ACTIONPAGE2", ["SHIFT-3"] = "ACTIONPAGE3", ["SHIFT-4"] = "ACTIONPAGE4",
        ["SHIFT-5"] = "ACTIONPAGE5", ["SHIFT-6"] = "ACTIONPAGE6", ["SHIFT-UP"] = "PREVIOUSACTIONPAGE", ["SHIFT-DOWN"] = "NEXTACTIONPAGE",
        ["CTRL-1"] = "SHAPESHIFTBUTTON1", ["CTRL-2"] = "SHAPESHIFTBUTTON2", ["CTRL-3"] = "SHAPESHIFTBUTTON3", ["CTRL-4"] = "SHAPESHIFTBUTTON4",
        ["ENTER"] = "OPENCHAT", ["/"] = "OPENCHATSLASH", ["PAGEUP"] = "CHATPAGEUP", ["PAGEDOWN"] = "CHATPAGEDOWN",
        ["SHIFT-PAGEDOWN"] = "CHATBOTTOM", ["R"] = "REPLY",
        ["TAB"] = "TARGETNEARESTENEMY", ["SHIFT-TAB"] = "TARGETPREVIOUSENEMY", ["CTRL-TAB"] = "TARGETNEARESTFRIEND",
        ["F1"] = "TARGETSELF", ["F2"] = "TARGETPARTYMEMBER1", ["F3"] = "TARGETPARTYMEMBER2", ["F4"] = "TARGETPARTYMEMBER3",
        ["F5"] = "TARGETPARTYMEMBER4", ["F"] = "ASSISTTARGET", ["T"] = "ATTACKTARGET", ["V"] = "NAMEPLATES",
        ["C"] = "TOGGLECHARACTER0", ["K"] = "TOGGLECHARACTER1", ["U"] = "TOGGLECHARACTER2", ["H"] = "TOGGLECHARACTER4",
        ["B"] = "OPENALLBAGS", ["SHIFT-B"] = "TOGGLEBACKPACK", ["F12"] = "TOGGLEBACKPACK", ["F11"] = "TOGGLEBAG1", ["F10"] = "TOGGLEBAG2",
        ["F9"] = "TOGGLEBAG3", ["F8"] = "TOGGLEBAG4", ["P"] = "TOGGLESPELLBOOK", ["SHIFT-P"] = "TOGGLEPETBOOK", ["N"] = "TOGGLETALENTS",
        ["L"] = "TOGGLEQUESTLOG", ["ESCAPE"] = "TOGGLEGAMEMENU", ["M"] = "TOGGLEWORLDMAP", ["O"] = "TOGGLESOCIAL",
        ["SHIFT-M"] = "TOGGLEBATTLEFIELDMINIMAP", ["CTRL-R"] = "TOGGLEFPS", ["PRINTSCREEN"] = "SCREENSHOT", ["ALT-Z"] = "TOGGLEUI",
        ["Z"] = "TOGGLESHEATH", ["X"] = "SITORSTAND",
    };

    public int Count => _commands.Count;

    /// <summary>Compiles every &lt;Binding&gt; element; called once the FrameXML functions they call are defined.</summary>
    public void Load(UiScreen ui, string xml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException e)
        {
            ui.Log.Error($"Bindings.xml: {e.Message}");
            return;
        }
        foreach (var node in root.Elements().Where(e => e.Name.LocalName == "Binding"))
        {
            var name = node.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
                continue;
            var body = string.IsNullOrWhiteSpace(node.Value) ? null : ui.Lua.Compile(node.Value, $"BINDING_{name}");
            var command = new Command(name, node.Attribute("header")?.Value,
                string.Equals(node.Attribute("runOnUp")?.Value, "true", StringComparison.OrdinalIgnoreCase), body);
            _commands.Add(command);
            _byName[name] = command;
        }
        foreach (var (key, command) in Defaults)
            if (_byName.ContainsKey(command))
                _keys[key] = command;
    }

    public string? Action(string key) => _keys.GetValueOrDefault(key);

    public IEnumerable<string> KeysFor(string command) => _keys.Where(k => k.Value.Equals(command, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key);

    public void Set(string key, string? command)
    {
        if (string.IsNullOrEmpty(command))
            _keys.Remove(key);
        else
            _keys[key] = command;
    }

    /// <summary>GetBinding(index): command name, header, then its keys.</summary>
    public (string Name, string? Header, string[] Keys)? Get(int index) =>
        index >= 1 && index <= _commands.Count ? (_commands[index - 1].Name, _commands[index - 1].Header, KeysFor(_commands[index - 1].Name).ToArray()) : null;

    /// <summary>Runs the command bound to <paramref name="key"/>; false when the key is unbound.</summary>
    public bool Press(UiScreen ui, string key, bool down)
    {
        if (Action(key) is not { } name)
            return false;
        Run(ui, name, down);
        return true;
    }

    public void Run(UiScreen ui, string name, bool down)
    {
        if (!_byName.TryGetValue(name, out var command) || command.Body is null || (!down && !command.RunOnUp))
            return;
        ui.Lua.Globals["keystate"] = down ? "down" : "up";
        ui.Lua.TryCall(command.Body, out _);
    }
}
