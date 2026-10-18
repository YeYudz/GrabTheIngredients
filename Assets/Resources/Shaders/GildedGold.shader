// 升级选卡「鎏金」特效着色器（UI/GildedGold）。
// 用在 SkillCard_x 卡底 Image 上，由 BuffCardGildEffect 组件通过 Material.SetFloat 驱动：
//  1) 金化：竖向金渐变乘到原卡面颜色上——保留卡面结构（红边×金=古铜，奶白中心×金=鎏金）；
//  2) 流光：一条斜向亮带周期性扫过（受精灵 alpha 裁剪，不会涂出轮廓外）；
//  3) 边缘流金：向四邻采样 alpha，与中心的差值即「靠近轮廓的边缘带」，叠加暖金 rim 并随时间呼吸。
// 不选中时 _GoldAmount/_ShineStrength/_EdgeGlow 全为 0 → 输出与原图一致，可放心常驻换材质。
// 结构对齐项目既有 Sprites/HitFlash（内置管线、单 Pass、无外部依赖）。
Shader "UI/GildedGold"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [PerRendererData] _Color ("Tint", Color) = (1,1,1,1)

        // ===== 鎏金参数 =====
        _GoldAmount ("Gold Blend", Range(0,1)) = 0
        _GoldLight ("Gold Light", Color) = (0.98, 0.84, 0.45, 1)
        _GoldDark ("Gold Dark", Color) = (0.82, 0.60, 0.20, 1)
        _ShinePos ("Shine Position", Range(-0.5,1.5)) = -0.5
        _ShineWidth ("Shine Width", Range(0.02,0.6)) = 0.16
        _ShineStrength ("Shine Strength", Range(0,1)) = 0.55
        _EdgeGlow ("Edge Glow", Range(0,2)) = 0
        _EdgeColor ("Edge Color", Color) = (1, 0.88, 0.5, 1)
        // 轮廓金边的采样半径（UV 比例，卡面 350×600 时 0.01 ≈ 3.5px）
        _EdgeWidth ("Edge Width (UV)", Range(0.002,0.05)) = 0.01

        // ===== UGUI 兼容段（与 UI/Default 一致：遮罩/裁剪/ColorMask）=====
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_CLIP_RECT)] _UseUIClipRect ("Use Clip Rect", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ UNITY_UI_CLIP_RECT
            #pragma target 2.0
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
            };

            fixed4 _Color;
            sampler2D _MainTex;
            float4 _ClipRect;

            fixed _GoldAmount;
            fixed4 _GoldLight;
            fixed4 _GoldDark;
            float _ShinePos;
            float _ShineWidth;
            fixed _ShineStrength;
            fixed _EdgeGlow;
            fixed4 _EdgeColor;
            float _EdgeWidth;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.worldPosition = IN.vertex;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.texcoord = IN.texcoord;
                OUT.color = IN.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                half4 sprite = tex2D(_MainTex, IN.texcoord) * IN.color;
                half a = sprite.a;

                // 1) 金化：竖向金渐变乘到原色（uv.y=0 底部深金 → 1 顶部亮金）
                half3 goldTint = lerp(_GoldDark.rgb, _GoldLight.rgb, IN.texcoord.y);
                half3 rgb = lerp(sprite.rgb, sprite.rgb * goldTint, _GoldAmount);

                // 2) 流光：斜向亮带（d = x + 微量 y 倾斜），band² 让边缘更锐
                half d = IN.texcoord.x + (IN.texcoord.y - 0.5h) * 0.25h;
                half band = 1.0h - saturate(abs(d - _ShinePos) / max(_ShineWidth, 0.001h));
                band *= band;
                rgb += _ShineStrength * band * half3(1.0h, 0.95h, 0.80h);

                // 3) 边缘流金：四邻 alpha 低差 → 轮廓内侧 rim（采样半径按 UV 比例，不依赖 texel size）
                if (_EdgeGlow > 0.001)
                {
                    float e = max(_EdgeWidth, 0.002);
                    float aL = tex2D(_MainTex, IN.texcoord - float2(e, 0)).a;
                    float aR = tex2D(_MainTex, IN.texcoord + float2(e, 0)).a;
                    float aU = tex2D(_MainTex, IN.texcoord + float2(0, e)).a;
                    float aD = tex2D(_MainTex, IN.texcoord - float2(0, e)).a;
                    float edge = saturate((a - min(min(aL, aR), min(aU, aD))) * 3.0);
                    rgb += _EdgeColor.rgb * edge * _EdgeGlow;
                }

                fixed4 col;
                col.rgb = rgb;
                col.a = a;

                #ifdef UNITY_UI_CLIP_RECT
                {
                    float2 inside = step(_ClipRect.xy, IN.worldPosition.xy)
                                  * step(IN.worldPosition.xy, _ClipRect.zw);
                    col.a *= inside.x * inside.y;
                }
                #endif

                return col;
            }
            ENDCG
        }
    }
}
