// 加法混合的精灵着色器(内置渲染管线)。
//
// 与 Unity 自带的 Sprites/Default 完全相同,只有一处不同:混合方式从 "Blend One OneMinusSrcAlpha" 改成 "Blend One One"。
// Sprites/Default 的片元着色器输出的是预乘 alpha 的颜色(rgb × a,a = 贴图 alpha × 顶点色 alpha),
// 所以 "Blend One One" 得到的就是  dst + src.rgb × src.a × 染色 alpha,
// 即 CPU 合成器(Dnf.Render.RgbaCanvas)里的加法混合公式(证据 E-030)。
// 精灵的染色 alpha 通过 SpriteRenderer.color 的 a 分量传入。
Shader "Dnf/SpriteAdditive"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment SpriteFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"
            ENDCG
        }
    }
}
