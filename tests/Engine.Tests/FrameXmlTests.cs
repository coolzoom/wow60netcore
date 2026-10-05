using System.Numerics;
using System.Text;
using FrameXml;
using FrameXml.Lua;
using FrameXml.Objects;
using MoonSharp.Interpreter;

namespace Engine.Tests;

public class TocAndPathTests
{
    [Fact]
    public void Toc_SkipsBomAndCommentsAndReadsMetadata()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "## Interface: 11200\r\n## Title: Demo  \r\n## Dependencies: A, B\r\n# comment\r\nFirst.xml  \r\n\r\nSub\\Second.lua\n")).ToArray();
        var toc = TocFile.Parse(bytes);
        Assert.Equal(["First.xml", "Sub\\Second.lua"], toc.Files);
        Assert.Equal(11200, toc.InterfaceVersion);
        Assert.Equal("Demo", toc["title"]);
        Assert.Equal(["A", "B"], toc.Dependencies);
    }

    [Theory]
    [InlineData(@"Interface\GlueXML\..\FrameXML\UI.xsd", @"Interface\FrameXML\UI.xsd")]
    [InlineData("Interface/AddOns/Foo/./Foo.toc", @"Interface\AddOns\Foo\Foo.toc")]
    [InlineData(@"a\b\..\..\c.lua", "c.lua")]
    public void Path_CollapsesParentSegments(string input, string expected) =>
        Assert.Equal(expected, UiPath.Normalize(input));

    [Fact]
    public void Markup_ParsesColorsAndEscapes()
    {
        var runs = TextMarkup.Parse("a|cffff0000red|r b||c|nd").ToList();
        Assert.Equal(["a", "red", " b|c\nd"], runs.Select(r => r.Text));
        Assert.Equal(new Color4(1, 0, 0), runs[1].Color);
        Assert.Null(runs[2].Color);
    }
}

public class Lua50CompatTests
{
    private static (LuaEngine Lua, UiLog Log) Engine()
    {
        var log = new UiLog();
        return (new LuaEngine(log), log);
    }

    [Fact]
    public void Transform_WrapsTableIterationAndAddsArg()
    {
        var code = "function f(a, ...) for k, v in t do end end";
        var result = Lua50Compat.Transform(code);
        Assert.Contains("in __lua50_iter(t) do", result);
        Assert.Contains("local arg = {n = select('#', ...), ...};", result);
        Assert.DoesNotContain("\n", result);
    }

    [Fact]
    public void Transform_LeavesStringsAndCommentsAlone()
    {
        const string code = "local s = \"for k in t do\" -- for x in y do\n--[[ function(...) ]] local goto = 1";
        var result = Lua50Compat.Transform(code);
        Assert.StartsWith("local s = \"for k in t do\" -- for x in y do\n--[[ function(...) ]]", result);
        Assert.EndsWith("local goto_ = 1", result);
    }

    [Fact]
    public void Lua50Escapes_KeepTheEscapedCharacter()
    {
        var (lua, log) = Engine();
        Assert.True(lua.Execute("s = \"“\\%s\\” \\[x\\] \\n\"", "strings"), log.ToString());
        Assert.Equal("“%s” [x] \n", lua["s"].String);
    }

    [Fact]
    public void Lua50Code_RunsWithCompatGlobals()
    {
        var (lua, log) = Engine();
        Assert.True(lua.Execute("""
            local t = {}
            tinsert(t, "a"); tinsert(t, "b")
            local keys = ""
            for i, v in t do keys = keys .. i .. v end
            function count(...) return arg.n end
            for k, v in pairs({x = 1}) do keys = keys .. k end
            result = format("%s %d %d %s %d", keys, getn(t), count(1, nil, 3), strupper("x"), mod(7, 3))
            angle = floor(sin(90) + 0.5)
            """, "test"), log.ToString());
        Assert.Equal("1a2bx 2 3 X 1", lua["result"].String);
        Assert.Equal(1, lua["angle"].Number);
    }

    [Fact]
    public void Errors_KeepLineNumbersAndReachErrorHandler()
    {
        var (lua, log) = Engine();
        lua.Execute("function handler(msg) caught = msg end seterrorhandler(handler)", "setup");
        Assert.False(lua.Execute("local a = 1\nlocal b = nil\nlocal c = b.x", "chunk"));
        var error = Assert.Single(log.Problems).Message;
        Assert.Contains("chunk:(3", error);
        Assert.Equal(error, lua["caught"].String);
    }
}

public class UiLoaderTests
{
    private const string Header = "<Ui xmlns=\"http://www.blizzard.com/wow/ui/\">";

    private static UiScreen Load(params (string Path, string Text)[] files)
    {
        var source = new MemoryUiFileSource();
        foreach (var (path, text) in files)
            source.Add(path, text);
        var ui = new UiScreen(source);
        ui.SetScreenSize(1024, 768);
        Assert.True(ui.Loader.LoadToc(@"Interface\Test\Test.toc"), ui.Log.ToString());
        return ui;
    }

    private static string Lua(UiScreen ui, string expression)
    {
        ui.Lua.Execute($"__result = tostring({expression})", "eval");
        return ui.Lua["__result"].String;
    }

    [Fact]
    public void CheckButton_SetCheckedZeroUnchecks()
    {
        var ui = Load(
            (@"Interface\Test\Test.toc", "Main.xml"),
            (@"Interface\Test\Main.xml", Header + "<CheckButton name=\"Box\" checked=\"true\"/></Ui>"));
        ui.Lua.Execute("Box:SetChecked(0)", "test");
        Assert.Equal("nil", Lua(ui, "Box:GetChecked()"));
        ui.Lua.Execute("Box:SetChecked(1)", "test");
        Assert.Equal("1", Lua(ui, "Box:GetChecked()"));
        ui.Lua.Execute("Box:SetChecked(nil)", "test");
        Assert.Equal("nil", Lua(ui, "Box:GetChecked()"));
    }

    [Fact]
    public void Toc_LoadsFilesRelativeToTocAndIncludesRelativeToFile()
    {
        var ui = Load(
            (@"Interface\Test\Test.toc", "Main.xml\nStrings.lua"),
            (@"Interface\Test\Main.xml", Header + "<Script file=\"Code.lua\"/><Include file=\"Sub\\Child.xml\"/><Script>order = order .. 'inline,'</Script></Ui>"),
            (@"Interface\Test\Code.lua", "order = 'code,'"),
            (@"Interface\Test\Sub\Child.xml", Header + "<Script file=\"Child.lua\"/></Ui>"),
            (@"Interface\Test\Sub\Child.lua", "order = order .. 'child,'"),
            (@"Interface\Test\Strings.lua", "order = order .. 'strings'"));
        Assert.Equal("code,child,inline,strings", Lua(ui, "order"));
        Assert.Empty(ui.Log.Problems);
    }

    [Fact]
    public void Frames_InheritTemplatesExpandParentAndRunOnLoadChildrenFirst()
    {
        var ui = Load(
            (@"Interface\Test\Test.toc", "Main.xml"),
            (@"Interface\Test\Main.xml", Header + """
                <Script>order = ""</Script>
                <Frame name="Template" virtual="true">
                  <Size><AbsDimension x="200" y="100"/></Size>
                  <Layers><Layer level="BACKGROUND"><Texture name="$parentBg"><Color r="1" g="0" b="0"/></Texture></Layer></Layers>
                  <Frames><Button name="$parentButton" text="OKAY"><Size x="50" y="20"/><Anchors><Anchor point="BOTTOMRIGHT"/></Anchors>
                    <Scripts><OnLoad>order = order .. this:GetName() .. ","</OnLoad></Scripts></Button></Frames>
                  <Scripts><OnLoad>order = order .. "template:" .. this:GetName() .. ","</OnLoad></Scripts>
                </Frame>
                <Frame name="Dialog" inherits="Template">
                  <Anchors><Anchor point="CENTER"><Offset><AbsDimension x="10" y="-20"/></Offset></Anchor></Anchors>
                  <Scripts><OnLoad>order = order .. "dialog"</OnLoad></Scripts>
                </Frame>
                """ + "</Ui>"),
            (@"Interface\Test\Strings.lua", ""));
        var dialog = ui.Find<Frame>("Dialog")!;
        Assert.Equal(new UiRect(1024 / 2f - 100 + 10, 384 - 50 - 20, 1024 / 2f + 100 + 10, 384 + 50 - 20), dialog.Rect);

        var button = ui.Find<Button>("DialogButton")!;
        Assert.Same(dialog, button.Parent);
        Assert.Equal(dialog.Rect!.Value.Right, button.Rect!.Value.Right);
        Assert.Equal("OKAY", button.Text);
        Assert.Equal(new Color4(1, 0, 0), ui.Find<Texture>("DialogBg")!.SolidColor);
        Assert.Equal("DialogButton,dialog", Lua(ui, "order"));
        Assert.Equal("Dialog", Lua(ui, "DialogButton:GetParent():GetName()"));
    }

    [Fact]
    public void Loader_ReportsClientWarnings()
    {
        var ui = Load(
            (@"Interface\Test\Test.toc", "Main.xml\nMissing.xml"),
            (@"Interface\Test\Main.xml", Header + """
                <Frame name="T" virtual="true"/>
                <Frame name="T" virtual="true"/>
                <Widget name="Odd"/>
                <Frame name="A"><Layers><Layer><Texture name="ATex" file="Interface\Nope"/></Layer></Layers>
                  <Anchors><Anchor point="TOP" relativeTo="Nowhere"/></Anchors></Frame>
                """ + "</Ui>"));
        var messages = ui.Log.Problems.Select(p => p.Message).ToList();
        Assert.Contains("Virtual object named T already exists", messages);
        Assert.Contains("Unknown frame type: Widget", messages);
        Assert.Contains(@"Texture ATex: Unable to load texture file Interface\Nope", messages);
        Assert.Contains("Couldn't find relative frame: Nowhere", messages);
        Assert.Contains(@"Couldn't open Interface\Test\Missing.xml", messages);
        Assert.Equal(new Color4(0, 1, 0), ui.Find<Texture>("ATex")!.SolidColor);
    }

    [Fact]
    public void FontStrings_InheritFontObjectsAndLocalizeText()
    {
        var ui = Load(
            (@"Interface\Test\Test.toc", "Strings.lua\nMain.xml"),
            (@"Interface\Test\Strings.lua", "HELLO = \"你好\""),
            (@"Interface\Test\Main.xml", Header + """
                <Font name="BaseFont" font="Fonts\FRIZQT__.TTF" outline="NORMAL"><FontHeight><AbsValue val="14"/></FontHeight><Color r="1" g="0.82" b="0"/></Font>
                <Frame name="F"><Layers><Layer level="OVERLAY">
                  <FontString name="$parentLabel" inherits="BaseFont" text="HELLO" justifyH="LEFT"/>
                  <FontString name="$parentRaw" font="Fonts\ARIALN.TTF" text="NOT_A_GLOBAL"><FontHeight val="10"/></FontString>
                </Layer></Layers></Frame>
                """ + "</Ui>"));
        var label = ui.Find<FontString>("FLabel")!;
        Assert.Equal("你好", label.Text);
        Assert.Equal(14, label.Font.Height);
        Assert.Equal("OUTLINE", label.Font.Flags);
        Assert.Equal("LEFT", label.Font.JustifyH);
        Assert.Same(ui.FindFont("BaseFont"), label.FontObject);
        var raw = ui.Find<FontString>("FRaw")!;
        Assert.Equal("NOT_A_GLOBAL", raw.Text);
        Assert.Equal(@"Fonts\ARIALN.TTF", raw.Font.File);
        Assert.Equal(DrawLayer.Overlay, raw.Layer);
        Assert.Equal("BaseFont", Lua(ui, "FLabel:GetFontObject():GetName()"));
    }

    [Fact]
    public void ShowHideAndEvents_RunScriptsWithThisAndArgs()
    {
        var ui = Load(
            (@"Interface\Test\Test.toc", "Main.xml"),
            (@"Interface\Test\Main.xml", Header + """
                <Script>shown = ""</Script>
                <Frame name="Hidden" hidden="true"><Scripts><OnShow>shown = shown .. "hidden,"</OnShow></Scripts></Frame>
                <Frame name="Visible">
                  <Scripts>
                    <OnLoad>this:RegisterEvent("TEST_EVENT")</OnLoad>
                    <OnShow>shown = shown .. "visible,"</OnShow>
                    <OnEvent>got = event .. ":" .. arg1 .. ":" .. this:GetName()</OnEvent>
                  </Scripts>
                </Frame>
                """ + "</Ui>"));
        Assert.Equal("visible,", Lua(ui, "shown"));
        ui.Find<Frame>("Hidden")!.Show();
        Assert.Equal("visible,hidden,", Lua(ui, "shown"));
        ui.FireEvent("TEST_EVENT", "x");
        Assert.Equal("TEST_EVENT:x:Visible", Lua(ui, "got"));
        Assert.Equal("nil", Lua(ui, "this"));
    }

    [Fact]
    public void Input_ClicksButtonsAndTypesIntoEditBoxes()
    {
        var ui = Load(
            (@"Interface\Test\Test.toc", "Main.xml"),
            (@"Interface\Test\Main.xml", Header + """
                <Button name="Go"><Size x="100" y="40"/><Anchors><Anchor point="BOTTOMLEFT"/></Anchors>
                  <Scripts><OnClick>clicked = arg1</OnClick><OnEnter>entered = 1</OnEnter></Scripts></Button>
                <EditBox name="Box" letters="5"><Size x="100" y="20"/><Anchors><Anchor point="TOPLEFT"/></Anchors>
                  <Scripts><OnEnterPressed>submitted = this:GetText()</OnEnterPressed></Scripts></EditBox>
                """ + "</Ui>"));
        ui.MouseMove(new Vector2(50, 20));
        Assert.Equal("1", Lua(ui, "entered"));
        ui.MouseDown();
        Assert.True(ui.Find<Button>("Go")!.Pushed);
        ui.MouseUp();
        Assert.Equal("LeftButton", Lua(ui, "clicked"));

        Assert.Same(ui.Find<EditBox>("Box"), ui.KeyboardFocus);
        ui.Char("abc");
        ui.KeyDown("BACKSPACE");
        ui.Char("defg");
        ui.KeyDown("ENTER");
        Assert.Equal("abdef", Lua(ui, "submitted"));
    }

    [Fact]
    public void LuaApi_CreatesFramesAndAnchorsRegions()
    {
        var ui = Load(
            (@"Interface\Test\Test.toc", "Main.lua"),
            (@"Interface\Test\Main.lua", """
                local f = CreateFrame("Frame", "Made", nil)
                f:SetWidth(300); f:SetHeight(200)
                f:SetPoint("TOPLEFT", 10, -10)
                local t = f:CreateTexture("$parentIcon", "ARTWORK")
                t:SetWidth(32); t:SetHeight(32)
                t:SetPoint("CENTER", f, "BOTTOMRIGHT", -16, 16)
                f.custom = "field"
                """));
        Assert.Empty(ui.Log.Problems);
        Assert.Equal(new UiRect(10, 768 - 210, 310, 758), ui.Find<Frame>("Made")!.Rect);
        Assert.Equal(new UiRect(278, 558, 310, 590), ui.Find<Texture>("MadeIcon")!.Rect);
        Assert.Equal("field", Lua(ui, "getglobal('Made').custom"));
        Assert.Equal("Texture", Lua(ui, "MadeIcon:GetObjectType()"));
    }
}

/// <summary>Loads the real glue screens from the installed client's MPQs.</summary>
public class GlueXmlDataTests
{
    private static string GameDirectory => Path.GetDirectoryName(GameData.Directory!.TrimEnd(Path.DirectorySeparatorChar))!;

    private static UiScreen LoadGlue(bool loose)
    {
        var ui = new UiScreen(new MpqUiFileSource(GameData.Files, loose ? GameDirectory : null));
        ui.SetScreenSize(1280, 720);
        var api = new GlueApi();
        api.LoadConfig(GameDirectory);
        api.AcceptAgreements();
        Assert.True(ui.LoadGlue(api), ui.Log.ToString());
        return ui;
    }

    [GameDataFact]
    public void GlueXml_LoadsWithoutErrorsAndShowsLogin()
    {
        var ui = LoadGlue(loose: false);
        var errors = ui.Log.Problems.Where(p => p.Level == UiLogLevel.Error).Select(p => p.Message).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.Equal("login", ui.CurrentScreen);
        Assert.True(ui.Find<Frame>("AccountLogin")!.IsVisible);
        Assert.True(ui.Find<Frame>("AccountLoginUI")!.IsVisible);
        Assert.False(ui.Find<Frame>("CharacterSelect")!.IsVisible);
        Assert.Same(ui.Find<EditBox>("AccountLoginAccountEdit"), ui.KeyboardFocus);
        Assert.Contains("1.12.1 (5875)", ui.Find<FontString>("AccountLoginVersion")!.Text);
        Assert.True(ui.Frames.Count > 300);
        Assert.Contains(ui.Log.Entries, e => e.Message == @"** Loading table of contents Interface\GlueXML\GlueXML.toc");
    }

    [GameDataFact]
    public void GlueXml_LooseOverrideAddsServerAddressBox()
    {
        if (!File.Exists(Path.Combine(GameDirectory, "Interface", "GlueXML", "ServerAddress.xml")))
            return;
        var ui = LoadGlue(loose: true);
        var errors = ui.Log.Problems.Where(p => p.Level == UiLogLevel.Error).Select(p => p.Message).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        var box = ui.Find<EditBox>("AccountLoginServerEdit")!;
        Assert.True(box.IsVisible);
        Assert.Same(ui.Find<Frame>("AccountLoginUI"), box.Parent);
        Assert.False(string.IsNullOrEmpty(box.Text));

        ui.Lua.Execute("AccountLoginServerEdit:SetText('127.0.0.1')", "test");
        ui.Lua.Execute("__realm = GetCVar('realmList')", "test");
        Assert.Equal("127.0.0.1", ui.Lua["__realm"].String);
    }

    [GameDataFact]
    public void GlueXml_LoginButtonOpensStatusDialog()
    {
        var ui = LoadGlue(loose: false);
        ui.Find<EditBox>("AccountLoginAccountEdit")!.SetText("player");
        ui.Find<Button>("AccountLoginLoginButton")!.Click();
        Assert.True(ui.Find<Frame>("GlueDialog")!.IsVisible);
        Assert.DoesNotContain(ui.Log.Problems, p => p.Level == UiLogLevel.Error);
    }

    [GameDataFact]
    public void GlueXml_EulaThenTermsMustBeScrolledAndAccepted()
    {
        var ui = new UiScreen(new MpqUiFileSource(GameData.Files));
        ui.SetScreenSize(1280, 720);
        var api = new GlueApi();
        Assert.True(ui.LoadGlue(api));
        var accept = ui.Find<Button>("TOSAccept")!;

        foreach (var (page, cvar) in new[] { ("EULA", "readEULA"), ("TOS", "readTOS") })
        {
            Assert.True(ui.Find<Frame>("TOSFrame")!.IsVisible);
            Assert.False(ui.Find<Frame>("AccountLoginUI")!.IsVisible);
            var text = ui.Find<SimpleHtml>(page + "Text")!;
            Assert.True(text.IsVisible);
            Assert.Contains("Blizzard", text.PlainText);
            Assert.False(accept.Enabled);

            text.Height = 3000;
            var scroll = ui.Find<ScrollFrame>(page + "ScrollFrame")!;
            scroll.UpdateScrollChildRect();
            var r = scroll.Rect!.Value;
            ui.MouseMove(new Vector2((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
            for (var i = 0; i < 30; i++)
                ui.MouseWheel(-1);
            Assert.Equal(scroll.VerticalRange, scroll.VerticalScroll);
            Assert.True(accept.Enabled);

            accept.Click();
            Assert.Equal("1", api.GetCVar(cvar));
        }
        Assert.False(ui.Find<Frame>("TOSFrame")!.IsVisible);
        Assert.True(ui.Find<Frame>("AccountLoginUI")!.IsVisible);
        Assert.DoesNotContain(ui.Log.Problems, p => p.Level == UiLogLevel.Error);
    }

    [GameDataFact]
    public void FrameXml_LoadsTocAndCreatesUiParent()
    {
        var ui = new UiScreen(new MpqUiFileSource(GameData.Files));
        Assert.True(ui.LoadFrameXml(loadAddOns: false), ui.Log.ToString());
        Assert.NotNull(ui.Find<Frame>("UIParent"));
        Assert.NotNull(ui.Find<GameTooltip>("GameTooltip"));
        var errors = ui.Log.Problems.Count(p => p.Level == UiLogLevel.Error);
        Assert.True(ui.Loader.FilesLoaded > 150, $"{ui.Loader.FilesLoaded} files");
        Assert.True(ui.Frames.Count > 1000, $"{ui.Frames.Count} frames, {errors} errors");
    }
}
