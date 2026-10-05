using System.Collections.Concurrent;
using Engine.Rendering;
using Formats.Blp;
using Formats.Models;
using Formats.Mpq;
using Silk.NET.OpenGL;
using Texture = Engine.Rendering.Texture;

namespace Client.World;

public sealed class GpuModel(Mesh mesh, ModelData data) : IDisposable
{
    public Mesh Mesh { get; } = mesh;
    public ModelData Data { get; } = data;

    public void Dispose() => Mesh.Dispose();
}

/// <summary>
/// Decodes textures and models on the thread pool and uploads them on the render thread.
/// Lookups return null until the asset is ready, so callers simply skip it for that frame.
/// </summary>
public sealed class AssetCache : IDisposable
{
    private const int UploadsPerFrame = 48;

    private readonly GL _gl;
    private readonly MpqFileSystem _files;
    private readonly ConcurrentDictionary<string, Task<RgbaImage?>> _imageTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ModelData?>> _modelTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuModel?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<M2Skeleton?>> _skeletons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<M2Effects?>> _effects = new(StringComparer.OrdinalIgnoreCase);
    private int _uploadBudget;

    public Texture Fallback { get; }
    public int PendingCount => _imageTasks.Count(t => !t.Value.IsCompleted) + _modelTasks.Count(t => !t.Value.IsCompleted);
    public int TextureCount => _textures.Count;
    public int ModelCount => _models.Count;
    public long Bytes { get; private set; }

    public AssetCache(GL gl, MpqFileSystem files)
    {
        _gl = gl;
        _files = files;
        Fallback = new Texture(gl, 1, 1, [200, 200, 200, 255]);
    }

    public void BeginFrame() => _uploadBudget = UploadsPerFrame;

    public Texture? Texture(string name) => Texture(name, () => Image(name));

    /// <summary>A texture built by <paramref name="build"/> on the thread pool (e.g. a composited character skin), cached by key.</summary>
    public Texture? Texture(string key, Func<RgbaImage?> build)
    {
        if (_textures.TryGetValue(key, out var texture))
            return texture;

        var task = _imageTasks.GetOrAdd(key, _ => Task.Run(build));
        if (!task.IsCompleted || _uploadBudget <= 0)
            return null;

        _uploadBudget--;
        var image = task.Result;
        texture = image is null ? null : new Texture(_gl, image.Width, image.Height, image.Pixels);
        Bytes += texture?.Bytes ?? 0;
        _textures[key] = texture;
        _imageTasks.TryRemove(key, out _);
        return texture;
    }

    public bool Exists(string name) => _files.Exists(name);

    /// <summary>Decodes a BLP synchronously (any thread); null when missing or unreadable.</summary>
    public RgbaImage? Image(string name) => Load(name, data => BlpImage.Decode(data));

    public GpuModel? Model(string name)
    {
        if (_models.TryGetValue(name, out var model))
            return model;

        var task = _modelTasks.GetOrAdd(name, n => Task.Run(() => LoadModel(n)));
        if (!task.IsCompleted || _uploadBudget <= 0)
            return null;

        _uploadBudget--;
        var data = task.Result;
        model = data is null ? null : new GpuModel(new Mesh(_gl, data.Vertices, data.Indices), data);
        Bytes += model?.Mesh.Bytes ?? 0;
        _models[name] = model;
        _modelTasks.TryRemove(name, out _);
        return model;
    }

    /// <summary>Bones and animations of an M2, or null while loading or when it has none.</summary>
    public M2Skeleton? Skeleton(string name)
    {
        var task = _skeletons.GetOrAdd(name, n => Task.Run(() => Load<M2Skeleton>(n, data => M2Skeleton.Read(data)!)));
        return task.IsCompleted ? task.Result : null;
    }

    /// <summary>Like <see cref="Skeleton"/>, but tells "still loading" (false) apart from "has no skeleton" (true, null).</summary>
    public bool TryGetSkeleton(string name, out M2Skeleton? skeleton)
    {
        skeleton = Skeleton(name);
        return _skeletons.TryGetValue(name, out var task) && task.IsCompleted;
    }

    /// <summary>Colors, UV animations, cameras, lights and particles of an M2, or null while loading or when unreadable.</summary>
    public M2Effects? Effects(string name)
    {
        var task = _effects.GetOrAdd(name, n => Task.Run(() => Load(n, M2Effects.Read)));
        return task.IsCompleted ? task.Result : null;
    }

    /// <summary>CPU-side model data without requiring a GPU upload; starts loading if needed.</summary>
    public bool TryGetModelData(string name, out ModelData? data)
    {
        if (_models.TryGetValue(name, out var model))
        {
            data = model?.Data;
            return true;
        }
        var task = _modelTasks.GetOrAdd(name, n => Task.Run(() => LoadModel(n)));
        data = task.IsCompleted ? task.Result : null;
        return task.IsCompleted;
    }

    private ModelData? LoadModel(string name) =>
        name.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)
            ? Load(name, root => ModelData.FromWmo(new WmoModel(root, i => _files.TryRead(WmoModel.GroupFileName(name, i)))))
            : Load(name, data => ModelData.FromM2(new M2Model(data)));

    private T? Load<T>(string name, Func<byte[], T> parse) where T : class
    {
        try
        {
            return _files.TryRead(name) is { } data ? parse(data) : null;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to load {name}: {e.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var texture in _textures.Values)
            texture?.Dispose();
        foreach (var model in _models.Values)
            model?.Dispose();
        Fallback.Dispose();
    }
}
