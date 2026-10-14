// 普通混合的精灵着色器(内置渲染管线)。
//
// 与 Unity 自带的 Sprites/Default 同构(预乘 alpha,Blend One OneMinusSrcAlpha),区别只有精度:
// 所有计算与插值都用 float,顶点色走 TEXCOORD 而不是 COLOR。内置的 UnitySprites.cginc 用 fixed/half,
// 在移动端会降成低精度,半透明像素会与 CPU 合成器差 1 级(证据 E-031)。
// 与 Dnf/SpriteAdditive 只有混合方式不同。
Shader "Dnf/SpriteNormal"
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
        Blend One OneMinusSrcAlpha

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
