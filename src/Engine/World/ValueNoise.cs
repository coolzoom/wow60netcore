namespace Engine.World;

/// <summary>Deterministic 2D value noise in the range [0, 1].</summary>
public sealed class ValueNoise(int seed)
{
    public float Sample(float x, float y)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var tx = Smooth(x - x0);
        var ty = Smooth(y - y0);

        var top = float.Lerp(Hash(x0, y0), Hash(x0 + 1, y0), tx);
        var bottom = float.Lerp(Hash(x0, y0 + 1), Hash(x0 + 1, y0 + 1), tx);
        return float.Lerp(top, bottom, ty);
    }

    public float Fractal(float x, float y, int octaves)
    {
        float sum = 0, amplitude = 1, total = 0, frequency = 1;
        for (var i = 0; i < octaves; i++)
        {
            sum += Sample(x * frequency, y * frequency) * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2;
        }
        return sum / total;
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    private float Hash(int x, int y)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 144269504);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }
}
