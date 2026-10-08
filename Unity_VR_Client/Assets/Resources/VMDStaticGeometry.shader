Shader "VMD/StaticGeometry"
{
    Properties { _ZWrite ("Depth Write", Float) = 1 }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off
            ZWrite [_ZWrite]
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input {
                float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR;
                float4 specular : TEXCOORD0; float3 ambient : TEXCOORD1;
            };
            struct Output {
                float4 position : SV_POSITION; float3 normal : TEXCOORD0; float3 world : TEXCOORD1;
                float4 color : COLOR; float4 specular : TEXCOORD2; float3 ambient : TEXCOORD3;
            };
            Output vert(Input v) {
                Output o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = v.color; o.specular = v.specular; o.ambient = v.ambient;
                return o;
            }
            float4 frag(Output i, fixed facing : VFACE) : SV_Target {
                float3 n = normalize(i.normal) * (facing >= 0 ? 1 : -1);
                float3 view = normalize(_WorldSpaceCameraPos - i.world);
                float3 light = normalize(float3(0.3, 0.6, -0.8));
                float diffuse = max(0, dot(n, light));
                float spec = pow(max(0, dot(n, normalize(light + view))), i.specular.w);
                return float4(i.ambient + i.color.rgb * (0.3 + diffuse) + i.specular.rgb * spec, i.color.a);
            }
            ENDCG
        }
    }
}
