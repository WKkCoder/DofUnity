// 加法混合的精灵着色器(内置渲染管线)。
//
// 与 Unity 自带的 Sprites/Default 同构,有两处不同:
//   1. 混合方式从 "Blend One OneMinusSrcAlpha" 改成 "Blend One One"。片元输出的是预乘 alpha 的颜色
//      (rgb × a,a = 贴图 alpha × 染色 alpha),所以 "Blend One One" 得到  dst + src.rgb × src.a × 染色 alpha,
//      即 CPU 合成器(Dnf.Render.RgbaCanvas)里的加法混合公式(证据 E-030)。
//   2. 所有计算与插值都用 float:内置的 UnitySprites.cginc 用 fixed/half,在移动端会降成低精度,
//      颜色与贴图采样会差 1 到 2 级(证据 E-031);顶点色走 TEXCOORD 而不是 COLOR,避免被当成低精度插值。
// 精灵的染色 alpha 通过 SpriteRenderer.color 的 a 分量传入。
Shader "Dnf/SpriteAdditive"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
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
            #pragma vertex DnfSpriteVert
            #pragma fragment DnfSpriteFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            #ifdef UNITY_INSTANCING_ENABLED
                UNITY_INSTANCING_BUFFER_START(PerDrawSprite)
                    // SpriteRenderer.color(未批处理或实例化时)
                    UNITY_DEFINE_INSTANCED_PROP(float4, unity_SpriteRendererColorArray)
                    UNITY_DEFINE_INSTANCED_PROP(float2, unity_SpriteFlipArray)
                UNITY_INSTANCING_BUFFER_END(PerDrawSprite)
                #define _RendererColor UNITY_ACCESS_INSTANCED_PROP(PerDrawSprite, unity_SpriteRendererColorArray)
                #define _Flip UNITY_ACCESS_INSTANCED_PROP(PerDrawSprite, unity_SpriteFlipArray)
            #endif

            CBUFFER_START(UnityPerDrawSprite)
            #ifndef UNITY_INSTANCING_ENABLED
                float4 _RendererColor;
                float2 _Flip;
            #endif
            CBUFFER_END

            float4 _Color;
            sampler2D_float _MainTex;

            struct DnfAppData
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DnfV2F
            {
                float4 vertex   : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                float4 tint     : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DnfV2F DnfSpriteVert(DnfAppData IN)
            {
                DnfV2F OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                float4 flipped = float4(IN.vertex.xy * _Flip, IN.vertex.z, 1.0);
                OUT.vertex = UnityObjectToClipPos(flipped);
                OUT.texcoord = IN.texcoord;
                OUT.tint = IN.color * _Color * _RendererColor;
                return OUT;
            }

            float4 DnfSpriteFrag(DnfV2F IN) : SV_Target
            {
                float4 c = tex2D(_MainTex, IN.texcoord) * IN.tint;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
