Shader "BattleSky/SciFi Asteroid"
{
    Properties
    {
        _Color ("Color", Color) = (0.4, 0.4, 0.35, 1)
        _Roughness ("Roughness", Range(0, 1)) = 0.8
        _RockColor ("Rock Color", Color) = (0.5, 0.48, 0.42, 1)
        _CrackColor ("Crack Color", Color) = (0.3, 0.28, 0.22, 1)
        _CrackScale ("Crack Scale", Float) = 8
        _CrackThickness ("Crack Thickness", Float) = 0.04
        [HDR] _GlowColor ("Edge Glow Color", Color) = (0.2, 0.3, 0.4, 1)
        _GlowIntensity ("Edge Glow Intensity", Float) = 0.5
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
                float3 worldNormal : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
                float4 position : SV_POSITION;
            };

            float4 _Color;
            float _Roughness;
            float4 _RockColor;
            float4 _CrackColor;
            float _CrackScale;
            float _CrackThickness;
            float4 _GlowColor;
            float _GlowIntensity;

            v2f vert (appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(_WorldSpaceCameraPos - mul(unity_ObjectToWorld, v.vertex).xyz);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Crack pattern
                float2 uv = i.uv * _CrackScale;
                float crack = smoothstep(_CrackThickness, _CrackThickness + 0.03,
                    abs(sin(uv.x * 3.14159) * sin(uv.y * 3.14159)) * 2 - 1);

                float3 rock = _RockColor.rgb * _Color.rgb;
                float3 crackCol = _CrackColor.rgb * crack * 0.5;

                // Edge glow
                float fresnel = pow(saturate(dot(i.viewDir, i.worldNormal)), 3);
                fresnel = 1 - fresnel;
                float3 edge = _GlowColor.rgb * fresnel * _GlowIntensity;

                float3 final = rock + crackCol + edge;

                return float4(final, 1);
            }
            ENDCG
        }
    }
}