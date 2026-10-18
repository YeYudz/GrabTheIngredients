Shader "Sprites/HitFlash"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [PerRendererData] _Color ("Tint", Color) = (1,1,1,1)

        // 受击白闪参数（由 SpriteHitFlash 组件通过 MaterialPropertyBlock 每帧写入 _FlashAmount）
        _FlashColor ("Flash Color", Color) = (1,1,1,1)
        _FlashAmount ("Flash Amount", Range(0,1)) = 0

        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
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

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ PIXELSNAP_ON
            #pragma target 2.0
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            fixed4 _Color;
            sampler2D _MainTex;

            fixed4 _FlashColor;
            float _FlashAmount;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.texcoord = IN.texcoord;
                OUT.color = IN.color * _Color;
                #ifdef PIXELSNAP_ON
                OUT.vertex = UnityPixelSnap(OUT.vertex);
                #endif
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                // 采样精灵贴图并叠加渲染器色调（与 Sprites/Default 一致的取色方式）
                fixed4 spriteColor = tex2D(_MainTex, IN.texcoord) * IN.color;

                // 朝闪白色插值：只改 RGB、保留原始 alpha，避免闪白时轮廓向外扩散
                fixed3 finalRgb = lerp(spriteColor.rgb, _FlashColor.rgb, _FlashAmount);

                // 预乘 alpha 输出（配合 Blend One OneMinusSrcAlpha）
                fixed4 col;
                col.rgb = finalRgb * spriteColor.a;
                col.a = spriteColor.a;
                return col;
            }
            ENDCG
        }
    }
}
