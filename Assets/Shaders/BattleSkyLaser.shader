Shader "BattleSky/SciFi Laser"
{
    Properties
    {
        [HDR] _CoreColor ("Core Color", Color) = (1, 0.4, 0.1, 1)
        [HDR] _GlowColor ("Glow Color", Color) = (1, 0.6, 0.2, 1)
        _GlowIntensity ("Glow Intensity", Float) = 3
        _CoreWidth ("Core Width", Range(0, 1)) = 0.5
        _ScrollSpeed ("Scroll Speed", Float) = 4
        [HDR] _RimColor ("Rim Color", Color) = (1, 0.8, 0.4, 1)
        _RimPower ("Rim Power", Range(0, 8)) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "IgnoreProjector"="True" "Queue"="Transparent" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off
        LOD 100

        Pass
        {
            Name "FORWARD"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 position : SV_POSITION;
            };

            float4 _CoreColor;
            float4 _GlowColor;
            float _GlowIntensity;
            float _CoreWidth;
            float _ScrollSpeed;
            float4 _RimColor;
            float _RimPower;

            v2f vert (appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.uv.x += _Time.y * _ScrollSpeed;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Animated scanline / energy core
                float pulse = sin(_Time.y * 8 + i.uv.x * 20) * 0.5 + 0.5;
                float core = smoothstep(_CoreWidth - 0.1, _CoreWidth + 0.1, abs(i.uv.y - 0.5) * 2);
                float glow = 1 - smoothstep(0.2, 0.8, abs(i.uv.y - 0.5) * 2);

                float3 coreCol = _CoreColor.rgb * pulse;
                float3 glowCol = _GlowColor.rgb * glow * _GlowIntensity * (1 - core);
                float3 rimCol = _RimColor.rgb * pow(saturate(1 - abs(i.uv.y * 2 - 1)), _RimPower);

                float3 final = coreCol + glowCol + rimCol;

                return float4(final, 1);
            }
            ENDCG
        }
    }
}