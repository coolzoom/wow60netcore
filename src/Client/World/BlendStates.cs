using System.Numerics;
using Formats.Models;
using Silk.NET.OpenGL;

namespace Client.World;

public static class BlendStates
{
    /// <summary>Enables blending for transparent modes with the M2 blend equation; opaque and alpha-keyed draws turn it off.</summary>
    public static void Apply(GL gl, BlendMode blend)
    {
        if (blend < BlendMode.Alpha)
        {
            gl.Disable(EnableCap.Blend);
            return;
        }
        gl.Enable(EnableCap.Blend);
        switch (blend)
        {
            case BlendMode.Additive: gl.BlendFunc(BlendingFactor.One, BlendingFactor.One); break;
            case BlendMode.AdditiveAlpha: gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); break;
            case BlendMode.Modulate: gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero); break;
            case BlendMode.Modulate2x: gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor); break;
            default: gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); break;
        }
    }

    /// <summary>What a fragment fades to in fog: the fog color, or for additive and modulating modes the value that adds nothing.</summary>
    public static Vector3 FogColor(BlendMode blend, Vector3 fog) => blend switch
    {
        BlendMode.Additive or BlendMode.AdditiveAlpha => Vector3.Zero,
        BlendMode.Modulate => Vector3.One,
        BlendMode.Modulate2x => new Vector3(0.5f),
        _ => fog,
    };
}
