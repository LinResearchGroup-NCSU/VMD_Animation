Shader "VMD/QuickSurf"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            // An untagged pass is SRPDefaultUnlit in URP and also works in the Built-in test renderer.
            Cull Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "VMDLighting.cginc"
            // Written by VMDQuickSurfGrid.compute; positions and normals are in VMD coordinates.
            struct SurfaceVertex { float3 position; float3 normal; uint color; };
            StructuredBuffer<SurfaceVertex> _Vertices;
            float4x4 _RawToWorld, _RawToWorldNormal;
            struct Output { float4 vertex : SV_POSITION; float3 normal : TEXCOORD0; float3 world : TEXCOORD1; float4 color : COLOR; };
            Output vert(uint id : SV_VertexID)
            {
                SurfaceVertex v = _Vertices[id];
                Output o;
                o.world = mul(_RawToWorld, float4(v.position, 1)).xyz;
                o.vertex = UnityWorldToClipPos(o.world);
                o.normal = mul((float3x3)_RawToWorldNormal, v.normal);
                o.color = float4(float3(v.color & 255, (v.color >> 8) & 255, (v.color >> 16) & 255) / 255.0, 1);
                return o;
            }
            float4 frag(Output i) : SV_Target
            {
                float3 view = normalize(_WorldSpaceCameraPos - i.world);
                float3 n = normalize(i.normal);
                // Light the side facing the camera, also inside a surface cut by the near plane.
                if (dot(n, view) < 0) n = -n;
                return float4(VMDLighting(i.color.rgb, n, i.world), 1);
            }
            ENDCG
        }
    }
}
