Shader "BattleSky/SciFi Shield"
{
    Properties
    {
        [HDR] _BaseColor ("Base Color", Color) = (0.2, 0.6, 1, 1)
        _GridColor ("Grid Color", Color) = (0.8, 0.9, 1, 1)
        _GridScale ("Grid Scale", Float) = 20
        _GridThickness ("Grid Thickness", Float) = 0.02
        _FresnelPower ("Fresnel Power", Range(0, 8)) = 4
        _PulseSpeed ("Pulse Speed", Float) = 2
        _PulseIntensity ("Pulse Intensity", Float) = 0.3
        [HDR] _GlowColor ("Glow Color", Color) = (0.4, 0.8, 1, 1)
        _GlowIntensity ("Glow Intensity", Float) = 2
        _Cutoff ("Cutoff", Range(0, 1)) = 0.1
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
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
                float3 viewDir : TEXCOORD3;
                float4 position : SV_POSITION;
            };

            float4 _BaseColor;
            float4 _GridColor;
            float _GridScale;
            float _GridThickness;
            float _FresnelPower;
            float _PulseSpeed;
            float _PulseIntensity;
            float4 _GlowColor;
            float _GlowIntensity;
            float _Cutoff;

            v2f vert (appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(_WorldSpaceCameraPos - o.worldPos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Hexagonal energy grid pattern
                float2 uv = i.uv * _GridScale;
                float grid = 0;
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                    {
                        float2 shifted = uv + float2(x, y) * _GridScale;
                        float d = abs(sin(shifted.x) * sin(shifted.y));
                        grid = max(grid, d);
                    }
                grid = smoothstep(_GridThickness, _GridThickness + 0.02, grid);

                // Pulse animation
                float pulse = sin(_Time.y * _PulseSpeed) * 0.5 + 0.5;
                float pulseGlow = 1 + pulse * _PulseIntensity;

                // Fresnel rim
                float fresnel = pow(saturate(dot(i.viewDir, i.worldNormal)), _FresnelPower);
                fresnel = 1 - fresnel;

                float3 base = _BaseColor.rgb * pulseGlow;
                float3 gridCol = _GridColor.rgb * grid * pulseGlow;
                float3 glow = _GlowColor.rgb * _GlowIntensity * fresnel * pulseGlow;

                float3 final = base + gridCol + glow;
                float alpha = _BaseColor.a * pulseGlow + grid * 0.5 + fresnel * 0.3;

                return float4(final, saturate(alpha));
            }
            ENDCG
        }
    }
}