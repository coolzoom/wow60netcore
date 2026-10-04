using System.Numerics;

namespace Engine.World;

public interface IHeightField
{
    /// <summary>Walkable area on the XZ plane.</summary>
    Vector2 Min { get; }
    Vector2 Max { get; }

    /// <summary>
    /// Height of the highest ground at (X, Z) that is not above <c>probe.Y</c>, so floors above the
    /// character (ceilings, upper storeys) are ignored. False where no ground data is available yet.
    /// </summary>
    bool TryGetHeight(Vector3 probe, out float height);
}
