Shader "Sprites/DropHighlight"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [PerRendererData] _Color ("Tint", Color) = (1,1,1,1)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0

        // 高光参数（移植自 Godot "Highlight CanvasItem"，作者 andich.xyz）
        _HighlightColor ("Highlight Color", Color) = (1, 0.85, 0.25, 1)
        _LineSmoothness ("Line Smoothness", Range(0.001, 0.1)) = 0.045
        _LineWidth ("Line Width", Range(0, 0.2)) = 0.09
        _Brightness ("Brightness", Float) = 3.0
        _RotationDeg ("Rotation Deg", Range(-90, 90)) = 30
        _Distortion ("Distortion", Range(1, 2)) = 1.8
        _Speed ("Speed", Float) = 0.7
        _Position ("Position", Range(0, 1)) = 0
        _PositionMin ("Position Min", Float) = 0.25
        _PositionMax ("Position Max", Float) = 0.5
        _Alpha ("Alpha", Range(0, 1)) = 1
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
            fixed4 _HighlightColor;
            sampler2D _MainTex;

            float _LineSmoothness;
            float _LineWidth;
            float _Brightness;
            float _RotationDeg;
            float _Distortion;
            float _Speed;
            float _Position;
            float _PositionMin;
            float _PositionMax;
            float _Alpha;

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
                fixed4 originalColor = tex2D(_MainTex, IN.texcoord) * IN.color;
                float alpha = originalColor.a;

                // 到边缘的渐变：中心为 1、边缘为 0，Distortion 控制衰减程度
                float2 centerUv = IN.texcoord - 0.5;
                float gradientToEdge = max(abs(centerUv.x), abs(centerUv.y));
                gradientToEdge = gradientToEdge * _Distortion;
                gradientToEdge = 1.0 - gradientToEdge;

                // 绕精灵中心旋转 UV（角度制）
                float angle = _RotationDeg * UNITY_PI / 180.0;
                float2x2 rotation = float2x2(cos(angle), -sin(angle), sin(angle), cos(angle));
                float2 rotatedUv = mul(rotation, centerUv) + 0.5;

                // 扫描线位置：在 Position Min / Max 区间内随时间往返
                float remappedPosition = _PositionMin + (_PositionMax - _PositionMin) * _Position;
                float remappedTime = frac(_Time.y * _Speed + remappedPosition);
                remappedTime = -2.0 + 4.0 * remappedTime;

                float2 offsetUv = rotatedUv + float2(remappedTime, 0.0);
                float lineDist = abs(offsetUv.x);
                lineDist = gradientToEdge * lineDist;
                lineDist = sqrt(lineDist);

                float lineSmoothness = clamp(_LineSmoothness, 0.001, 1.0);
                float offsetPlus = _LineWidth + lineSmoothness;
                float offsetMinus = _LineWidth - lineSmoothness;

                float remappedLine = (lineDist - offsetPlus) / (offsetMinus - offsetPlus);
                remappedLine = clamp(remappedLine * _Brightness, 0.0, 1.0);
                remappedLine = min(remappedLine, _Alpha);

                // 只在精灵可见像素上叠加高光，避免透明区域出现扫描线
                remappedLine *= alpha;

                // 加性叠加金色高光在原始贴图上，并按精灵 alpha 预乘（与 Sprites/Default 一致）
                fixed4 col;
                col.rgb = (originalColor.rgb + remappedLine * _HighlightColor.rgb) * alpha;
                col.a = alpha;
                return col;
            }
            ENDCG
        }
    }
}
