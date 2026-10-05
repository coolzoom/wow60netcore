using System.Xml.Linq;
using FrameXml.Lua;
using FrameXml.Objects;
using MoonSharp.Interpreter;
using static FrameXml.Lua.LuaBindings;

namespace FrameXml;

/// <summary>Functions every FrameScript state has, glue or in-game.</summary>
internal static class CoreApi
{
    public static void Register(UiScreen ui)
    {
        Fn(ui, "CreateFrame", a =>
        {
            var node = new XElement(a.Str(0) ?? "Frame");
            if (a.Str(1) is { } name) node.SetAttributeValue("name", name);
            if (a.Str(3) is { } inherits) node.SetAttributeValue("inherits", inherits);
            return O(ui.Loader.CreateFrame(node, ui.Bindings.ObjectOf<Frame>(a[2])));
        });
        Fn(ui, "debuginfo", _ => DynValue.Nil);
        Fn(ui, "FrameXML_Debug", a => { ui.Loader.Verbose = a.Bool(0) && a.Num(0, 1) != 0; return DynValue.Nil; });
        Fn(ui, "GetTime", _ => N(ui.Time));
        Fn(ui, "GetScreenWidth", _ => N(ui.Width));
        Fn(ui, "GetScreenHeight", _ => N(ui.Height));
        Fn(ui, "GetCursorPosition", _ => Tuple(N(ui.MousePosition.X), N(ui.MousePosition.Y)));
    }

    private static void Fn(UiScreen ui, string name, Func<LuaArgs, DynValue> body) =>
        ui.Lua.Globals[name] = DynValue.NewCallback((_, args) => body(new LuaArgs(args, 0)), name);
}
