namespace Engine.Rendering;

public static class Shaders
{
    private const string VertexCommon = """
        #version 330 core
        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aNormal;
        layout (location = 2) in vec2 aUv;
        layout (location = 3) in vec4 aColor;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec3 vNormal;
        out vec2 vUv;
        out vec4 vColor;
        out vec3 vWorld;
        out float vViewDistance;

        void main()
        {
            vec4 world = uModel * vec4(aPosition, 1.0);
            vec4 view = uView * world;
            vNormal = mat3(uModel) * aNormal;
            vUv = aUv;
            vColor = aColor;
            vWorld = world.xyz;
            vViewDistance = length(view.xyz);
            gl_Position = uProjection * view;
        }
        """;

    private const string FragmentCommon = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUv;
        in vec4 vColor;
        in vec3 vWorld;
        in float vViewDistance;

        uniform vec3 uLightDirection;
        uniform vec3 uFogColor;
        uniform float uFogDensity;

        out vec4 FragColor;

        vec3 shade(vec3 albedo)
        {
            float diffuse = abs(dot(normalize(vNormal), -normalize(uLightDirection)));
            return albedo * (0.45 + 0.65 * diffuse);
        }

        vec3 fog(vec3 color)
        {
            float amount = 1.0 - exp(-pow(vViewDistance * uFogDensity, 2.0));
            return mix(color, uFogColor, clamp(amount, 0.0, 1.0));
        }
        """;

    public const string LitVertex = VertexCommon;

    /// <summary>Untextured, vertex-colored geometry.</summary>
    public const string LitFragment = FragmentCommon + """

        uniform float uAlpha;

        void main()
        {
            FragColor = vec4(fog(shade(vColor.rgb)), uAlpha);
        }
        """;

    /// <summary>Textured models: texture * vertex color, with optional alpha test.</summary>
    public const string TexturedFragment = FragmentCommon + """

        uniform sampler2D uTexture;
        uniform float uAlphaTest;

        void main()
        {
            vec4 texel = texture(uTexture, vUv) * vColor;
            if (texel.a < uAlphaTest)
                discard;
            FragColor = vec4(fog(shade(texel.rgb)), texel.a);
        }
        """;

    /// <summary>Textures with lighting already baked in (e.g. minimap tiles for far terrain).</summary>
    public const string UnlitTexturedFragment = FragmentCommon + """

        uniform sampler2D uTexture;

        void main()
        {
            FragColor = vec4(fog(texture(uTexture, vUv).rgb), 1.0);
        }
        """;

    /// <summary>Terrain splatting: up to four tiled layers blended by an RGB alpha map.</summary>
    public const string TerrainFragment = FragmentCommon + """

        uniform sampler2D uLayer0;
        uniform sampler2D uLayer1;
        uniform sampler2D uLayer2;
        uniform sampler2D uLayer3;
        uniform sampler2D uAlphaMap;
        uniform float uDetailScale;

        void main()
        {
            vec2 detail = vWorld.xz * uDetailScale;
            vec3 blend = texture(uAlphaMap, vUv).rgb;
            vec3 color = texture(uLayer0, detail).rgb;
            color = mix(color, texture(uLayer1, detail).rgb, blend.r);
            color = mix(color, texture(uLayer2, detail).rgb, blend.g);
            color = mix(color, texture(uLayer3, detail).rgb, blend.b);
            FragColor = vec4(fog(shade(color)), 1.0);
        }
        """;
}
