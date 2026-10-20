Shader "BattleSky/SciFi Ship"
{
    Properties
    {
        [HDR] _BaseColor ("Base Color", Color) = (0.1, 0.15, 0.2, 1)
        _Metallic ("Metallic", Range(0, 1)) = 0.9
        _Smoothness ("Smoothness", Range(0, 1)) = 0.85
        _PanelColor ("Panel Line Color", Color) = (0.3, 0.4, 0.5, 1)
        _PanelScale ("Panel Scale", Float) = 5
        _PanelThickness ("Panel Thickness", Float) = 0.03
        [HDR] _EmissiveColor ("Emissive Color", Color) = (0, 0.5, 1, 1)
        _EmissiveIntensity ("Emissive Intensity", Float) = 2
        _RimColor ("Rim Light Color", Color) = (0.4, 0.7, 1, 1)
        _RimPower ("Rim Power", Range(0, 8)) = 3
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "IgnoreProjector"="True" "Queue"="Geometry" }
        Cull Back
        ZWrite On
        LOD 200

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
            float _Metallic;
            float _Smoothness;
            float4 _PanelColor;
            float _PanelScale;
            float _PanelThickness;
            float4 _EmissiveColor;
            float _EmissiveIntensity;
            float4 _RimColor;
            float _RimPower;

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
                // Panel line grid
                float2 uv = i.uv * _PanelScale;
                float panelX = smoothstep(_PanelThickness, _PanelThickness + 0.02, abs(frac(uv.x) - 0.5) * 2 - 1);
                float panelY = smoothstep(_PanelThickness, _PanelThickness + 0.02, abs(frac(uv.y) - 0.5) * 2 - 1);
                float panel = min(panelX, panelY);

                // Base metallic
                float3 base = _BaseColor.rgb;
                float3 panelCol = base * _PanelColor.rgb * panel;

                // Fresnel rim light
                float fresnel = pow(saturate(dot(i.viewDir, i.worldNormal)), _RimPower);
                fresnel = 1 - fresnel;
                float3 rim = _RimColor.rgb * fresnel * 0.5;

                // Emissive accents along panels
                float emissive = panel * _EmissiveIntensity;
                float3 emit = _EmissiveColor.rgb * emissive;

                float3 final = base + panelCol + rim + emit;

                return float4(final, 1);
            }
            ENDCG
        }
    }
}