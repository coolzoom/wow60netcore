using System.Numerics;

namespace Engine.Rendering;

/// <summary>View frustum planes extracted from a row-vector view-projection matrix.</summary>
public readonly struct Frustum
{
    private readonly Plane[] _planes;

    public Frustum(Matrix4x4 viewProjection)
    {
        var m = viewProjection;
        _planes =
        [
            Normalize(new Plane(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41)),
            Normalize(new Plane(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41)),
            Normalize(new Plane(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42)),
            Normalize(new Plane(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42)),
            Normalize(new Plane(m.M13, m.M23, m.M33, m.M43)),
            Normalize(new Plane(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)),
        ];
    }

    public bool Intersects(Vector3 center, float radius)
    {
        foreach (var plane in _planes)
            if (Plane.DotCoordinate(plane, center) < -radius)
                return false;
        return true;
    }

    private static Plane Normalize(Plane plane) => Plane.Normalize(plane);
}
