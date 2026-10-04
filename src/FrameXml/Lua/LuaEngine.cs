using MoonSharp.Interpreter;

namespace FrameXml.Lua;

/// <summary>
/// The FrameScript Lua state. Code goes through <see cref="Lua50Compat"/> and the globals Lua 5.0's compat.lua
/// provides (the client runs an embedded compat.lua in FUN_007039e0) are recreated on top of MoonSharp's 5.2 library.
/// </summary>
public sealed class LuaEngine
{
    private const string Prelude = """
        function __lua50_iter(f, s, c)
            if type(f) == "table" then return next, f, nil end
            return f, s, c
        end

        function table.getn(t)
            if type(t.n) == "number" then return t.n end
            return #t
        end
        function table.setn(t, n) t.n = n end
        function table.foreach(t, f)
            for k, v in pairs(t) do local r = f(k, v) if r ~= nil then return r end end
        end
        function table.foreachi(t, f)
            for i = 1, table.getn(t) do local r = f(i, t[i]) if r ~= nil then return r end end
        end
        string.gfind = string.gmatch
        math.mod = math.fmod
        math.log10 = math.log10 or function(x) return math.log(x, 10) end
        unpack = unpack or table.unpack
        loadstring = loadstring or function(s, name) return load(s, name) end

        foreach = table.foreach
        foreachi = table.foreachi
        getn = table.getn
        setn = table.setn
        tinsert = table.insert
        tremove = table.remove
        sort = table.sort

        abs = math.abs
        acos = function(x) return math.deg(math.acos(x)) end
        asin = function(x) return math.deg(math.asin(x)) end
        atan = function(x) return math.deg(math.atan(x)) end
        atan2 = function(x, y) return math.deg(math.atan2(x, y)) end
        ceil = math.ceil
        cos = function(x) return math.cos(math.rad(x)) end
        deg = math.deg
        exp = math.exp
        floor = math.floor
        frexp = math.frexp
        ldexp = math.ldexp
        log = math.log
        log10 = math.log10
        max = math.max
        min = math.min
        mod = math.fmod
        PI = math.pi
        rad = math.rad
        random = math.random
        sin = function(x) return math.sin(math.rad(x)) end
        sqrt = math.sqrt
        tan = function(x) return math.tan(math.rad(x)) end

        strbyte = string.byte
        strchar = string.char
        strfind = string.find
        format = string.format
        gsub = string.gsub
        strlen = string.len
        strlower = string.lower
        strrep = string.rep
        strsub = string.sub
        strupper = string.upper

        date = os.date
        time = os.time

        getglobal = function(n) return _G[n] end
        setglobal = function(n, v) _G[n] = v end
        gcinfo = function() return math.floor(collectgarbage("count")), 0 end
        """;

    private readonly UiLog _log;
    private DynValue? _errorHandler;
    private bool _inErrorHandler;

    public Script Script { get; }
    public Table Globals => Script.Globals;
    public int ErrorCount { get; private set; }

    public LuaEngine(UiLog log)
    {
        _log = log;
        Script = new Script(CoreModules.Preset_Complete);
        Script.DoString(Prelude, null, "compat.lua");
        Globals["seterrorhandler"] = DynValue.NewCallback((_, args) =>
        {
            _errorHandler = args.Count > 0 && args[0].Type == DataType.Function ? args[0] : null;
            return DynValue.Nil;
        });
        Globals["geterrorhandler"] = DynValue.NewCallback((_, _) => _errorHandler ?? DynValue.Nil);
    }

    public DynValue this[string name]
    {
        get => Globals.Get(name);
        set => Globals.Set(name, value);
    }

    /// <summary>Compiles a chunk without running it; returns null (and reports) on syntax errors.</summary>
    public DynValue? Compile(string code, string chunkName)
    {
        try
        {
            return Script.LoadString(Lua50Compat.Transform(code), null, chunkName);
        }
        catch (InterpreterException e)
        {
            ReportError(e.DecoratedMessage ?? e.Message);
            return null;
        }
    }

    /// <summary>Compiles and runs a chunk (FUN_00704ae0). Returns false if it failed to compile or raised an error.</summary>
    public bool Execute(string code, string chunkName) =>
        Compile(code, chunkName) is { } chunk && TryCall(chunk, out _);

    public DynValue Call(DynValue function, params DynValue[] args) => TryCall(function, out var result, args) ? result : DynValue.Nil;

    public bool TryCall(DynValue function, out DynValue result, params DynValue[] args)
    {
        try
        {
            result = Script.Call(function, args);
            return true;
        }
        catch (InterpreterException e)
        {
            ReportError(e.DecoratedMessage ?? e.Message);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or FormatException)
        {
            ReportError($"internal error: {e.Message}");
        }
        result = DynValue.Nil;
        return false;
    }

    /// <summary>Errors go to the script's handler from seterrorhandler, otherwise to the UI log.</summary>
    public void ReportError(string message)
    {
        ErrorCount++;
        _log.Error(message);
        if (_errorHandler is null || _inErrorHandler)
            return;
        _inErrorHandler = true;
        try
        {
            Script.Call(_errorHandler, DynValue.NewString(message));
        }
        catch (InterpreterException e)
        {
            _log.Error("error in error handler: " + (e.DecoratedMessage ?? e.Message));
        }
        finally
        {
            _inErrorHandler = false;
        }
    }
}
