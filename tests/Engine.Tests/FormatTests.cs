using System.Numerics;
using Formats;
using Formats.Blp;
using Formats.Dbc;
using Formats.Models;
using Formats.Mpq;
using Formats.Terrain;

namespace Engine.Tests;

public class FormatUnitTests
{
    [Fact]
    public void MpqHash_MatchesKnownTableKeys()
    {
        Assert.Equal(0xC3AF3770u, MpqCrypto.Hash("(hash table)", MpqCrypto.HashFileKey));
        Assert.Equal(0xEC83B3A3u, MpqCrypto.Hash("(block table)", MpqCrypto.HashFileKey));
        Assert.Equal(MpqCrypto.Hash("a\\b.txt", 1), MpqCrypto.Hash("A/B.TXT", 1));
    }

    [Fact]
    public void MpqDecrypt_RoundTripsThroughKnownCiphertext()
    {
        uint[] data = [0x12345678, 0x9ABCDEF0];
        MpqCrypto.Decrypt(data, 0xC3AF3770);
        Assert.NotEqual(0x12345678u, data[0]);
    }

    [Fact]
    public void Dxt1_DecodesSolidAndInterpolatedColors()
    {
        // c0 = pure red (0xF800), c1 = pure blue (0x001F); indices: pixel0=c0, pixel1=c1, pixel2=2/3 red, rest c0.
        byte[] block = [0x00, 0xF8, 0x1F, 0x00, 0b_10_01_00, 0, 0, 0];
        var pixels = Dxt.DecodeDxt1(block, 4, 4, punchThroughAlpha: false);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[4..8]);
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, pixels[8..12]);
    }

    [Fact]
    public void ChunkReader_ReversesIds()
    {
        byte[] data = [(byte)'R', (byte)'E', (byte)'V', (byte)'M', 4, 0, 0, 0, 18, 0, 0, 0];
        var chunk = Assert.Single(ChunkReader.Read(data));
        Assert.Equal("MVER", chunk.Id);
        Assert.Equal(8, chunk.Offset);
        Assert.Equal(4, chunk.Size);
    }

    [Fact]
    public void WorldSpace_RoundTripsAndFindsTiles()
    {
        var world = new Vector3(-9464f, 62f, 56f); // Goldshire
        var render = WorldSpace.FromWorld(world);
        Assert.True(Vector3.Distance(world, WorldSpace.ToWorld(render)) < 0.01f);
        Assert.Equal((31, 49), WorldSpace.TileAt(render.X, render.Z));
    }
}

/// <summary>Runs only when the 1.12 client's Data directory is found above the test binaries.</summary>
public sealed class GameDataFactAttribute : FactAttribute
{
    public GameDataFactAttribute()
    {
        if (GameData.Directory is null)
            Skip = "Client Data/*.MPQ not found.";
    }
}

public static class GameData
{
    public static readonly string? Directory = MpqFileSystem.FindDataDirectory(AppContext.BaseDirectory);
    private static readonly Lazy<MpqFileSystem> Lazy = new(() => new MpqFileSystem(Directory!));
    public static MpqFileSystem Files => Lazy.Value;
}

public class GameDataTests
{
    private static MpqFileSystem Fs => GameData.Files;

    [GameDataFact]
    public void ListFiles_CoverAllContentTypes()
    {
        var files = Fs.AllFiles;
        Assert.True(files.Count > 50_000);
        Assert.Contains(files, f => f.EndsWith(".adt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith(".m2", StringComparison.OrdinalIgnoreCase));
    }

    [GameDataFact]
    public void MapDbc_ContainsContinentsWithLocalizedNames()
    {
        var maps = MapDbc.Read(Fs.Read("DBFilesClient\\Map.dbc"));
        var azeroth = Assert.Single(maps, m => m.Id == 0);
        Assert.Equal("Azeroth", azeroth.Directory);
        Assert.False(string.IsNullOrEmpty(azeroth.Name));
        Assert.Contains(maps, m => m.Id == 1 && m.Directory == "Kalimdor");
    }

    [GameDataFact]
    public void Wdt_ListsExistingTiles()
    {
        var wdt = new WdtFile(Fs.Read("World\\Maps\\Azeroth\\Azeroth.wdt"));
        Assert.True(wdt.HasTile(32, 48));
        Assert.All(wdt.Tiles().Take(20), t => Assert.True(Fs.Exists(AdtFile.FileName("Azeroth", t.X, t.Y))));
    }

    [GameDataFact]
    public void Adt_ChunksTileTheExpectedArea()
    {
        var adt = new AdtFile(Fs.Read(AdtFile.FileName("Azeroth", 32, 48)), 32, 48);
        Assert.Equal(256, adt.Chunks.Count);
        Assert.NotEmpty(adt.Textures);

        var min = new Vector2(32 * WorldSpace.TileSize, 48 * WorldSpace.TileSize);
        foreach (var chunk in adt.Chunks)
        {
            var corner = chunk.Positions[0];
            Assert.Equal(min.X + chunk.IndexX * WorldSpace.ChunkSize, corner.X, 0.05f);
            Assert.Equal(min.Y + chunk.IndexY * WorldSpace.ChunkSize, corner.Z, 0.05f);
            Assert.InRange(chunk.Layers.Length, 1, 4);
            Assert.All(chunk.Layers, l => Assert.InRange(l.TextureIndex, 0, adt.Textures.Count - 1));
        }
    }

    [GameDataFact]
    public void Adt_StoredNormalsMatchTerrainSlope()
    {
        var adt = new AdtFile(Fs.Read(AdtFile.FileName("Azeroth", 32, 48)), 32, 48);
        var agreement = new List<float>();
        foreach (var chunk in adt.Chunks)
        {
            var indices = chunk.BuildIndices();
            for (var t = 0; t < indices.Count; t += 3)
            {
                var a = chunk.Positions[indices[t]];
                var face = Vector3.Normalize(Vector3.Cross(chunk.Positions[indices[t + 1]] - a, chunk.Positions[indices[t + 2]] - a));
                agreement.Add(Vector3.Dot(face, chunk.Normals[indices[t]]));
            }
        }
        Assert.True(agreement.Average() > 0.9f, $"average agreement {agreement.Average()}");
    }

    [GameDataFact]
    public void Adt_DoodadsSitOnTerrain()
    {
        var adt = new AdtFile(Fs.Read(AdtFile.FileName("Azeroth", 32, 48)), 32, 48);
        Assert.NotEmpty(adt.Doodads);
        Assert.NotEmpty(adt.MapObjects);

        var chunkVertices = adt.Chunks.SelectMany(c => c.Positions).ToArray();
        var gaps = adt.Doodads.Take(200).Select(d =>
        {
            var nearest = chunkVertices.MinBy(v => Vector2.DistanceSquared(new(v.X, v.Z), new(d.Position.X, d.Position.Z)));
            return MathF.Abs(nearest.Y - d.Position.Y);
        }).Order().ToList();
        Assert.True(gaps[gaps.Count / 2] < 2f, $"median vertical gap {gaps[gaps.Count / 2]}");
    }

    [GameDataFact]
    public void Wdl_MatchesAdtHeightsAndCoversTheMap()
    {
        var wdl = new WdlFile(Fs.Read("World\\Maps\\Azeroth\\Azeroth.wdl"));
        var wdt = new WdtFile(Fs.Read("World\\Maps\\Azeroth\\Azeroth.wdt"));
        Assert.Equal(wdt.Tiles().Count(), wdl.Tiles().Count());

        var adt = new AdtFile(Fs.Read(AdtFile.FileName("Azeroth", 32, 48)), 32, 48);
        var heights = wdl.Heights(32, 48)!;
        // WDL grid point (row, column) sits on the corner of ADT chunk (IndexX = column, IndexY = row).
        var errors = adt.Chunks.Select(c => MathF.Abs(heights[c.IndexY * WdlFile.GridSize + c.IndexX] - c.Positions[0].Y)).ToList();
        Assert.True(errors.Average() < 2f, $"mean error {errors.Average()}");
    }

    [GameDataFact]
    public void MinimapIndex_ResolvesTilesToExistingTextures()
    {
        var index = new MinimapIndex(Fs.Read(MinimapIndex.TranslationFile));
        Assert.True(index.Count > 1000);
        var file = index.Find("Azeroth", 32, 48);
        Assert.NotNull(file);
        Assert.True(Fs.Exists(file));
        Assert.Equal(256, BlpImage.Decode(Fs.Read(file)).Width);
    }

    [GameDataFact]
    public void Blp_DecodesTerrainAndInterfaceTextures()
    {
        var samples = Fs.AllFiles.Where(f => f.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)).Where((_, i) => i % 500 == 0).ToList();
        foreach (var name in samples)
        {
            var image = BlpImage.Decode(Fs.Read(name));
            Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);
            Assert.Contains(image.Pixels, b => b != 0);
        }
    }

    [GameDataFact]
    public void M2_TreeHasValidGeometryAndTextures()
    {
        var model = new M2Model(Fs.Read("World\\Azeroth\\Elwynn\\PassiveDoodads\\Trees\\ElwynnTree01\\ElwynnPine01.m2"));
        Assert.Equal(47, model.Positions.Length);
        Assert.Equal(0, model.Indices.Length % 3);
        Assert.All(model.Indices, i => Assert.True(i < model.Positions.Length));
        Assert.NotEmpty(model.Batches);
        Assert.All(model.Batches, b =>
        {
            Assert.True(b.IndexStart + b.IndexCount <= model.Indices.Length);
            Assert.True(b.Texture is null || Fs.Exists(b.Texture), b.Texture);
        });
    }

    [GameDataFact]
    public void M2_EveryDoodadInTileParses()
    {
        var adt = new AdtFile(Fs.Read(AdtFile.FileName("Azeroth", 32, 48)), 32, 48);
        foreach (var name in adt.Doodads.Select(d => M2Model.NormalizePath(d.Model)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var model = new M2Model(Fs.Read(name));
            Assert.All(model.Indices, i => Assert.True(i < model.Positions.Length, name));
        }
    }

    [GameDataFact]
    public void Wmo_LoadsGroupsWithMaterials()
    {
        const string root = "World\\wmo\\Azeroth\\Buildings\\Redridge_Stable\\Redridge_Stable.wmo";
        var wmo = new WmoModel(Fs.Read(root), i => Fs.TryRead(WmoModel.GroupFileName(root, i)));
        Assert.NotEmpty(wmo.Groups);
        Assert.NotEmpty(wmo.Materials);
        foreach (var group in wmo.Groups)
        {
            Assert.All(group.Indices, i => Assert.True(i < group.Positions.Length));
            Assert.All(group.Batches, b => Assert.InRange(b.Material, 0, wmo.Materials.Count - 1));
        }
        Assert.All(wmo.Materials.Where(m => m.Texture.Length > 0), m => Assert.True(Fs.Exists(m.Texture), m.Texture));
    }
}
