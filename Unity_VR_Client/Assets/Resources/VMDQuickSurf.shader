// Draws the QuickSurf triangles that VMDQuickSurfGrid.compute writes into a GPU buffer,
// for VMDQuickSurfScene (QuickSurf on the coordinate route). There is no mesh: the
// script draws with Graphics.RenderPrimitives, and the vertex shader reads position,
// normal and color from _Vertices by vertex index. _RawToWorld and _RawToWorldNormal,
// set by the script every frame, take positions and normals from VMD coordinates to
// world space. Both faces are drawn, and lighting comes from VMDLighting.cginc.
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
            // Single Pass Instanced XR draws each primitive twice; the instance ID selects the eye.
            struct Input { uint id : SV_VertexID; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Output { float4 vertex : SV_POSITION; float3 normal : TEXCOORD0; float3 world : TEXCOORD1; float4 color : COLOR; UNITY_VERTEX_OUTPUT_STEREO };
            Output vert(Input input)
            {
                SurfaceVertex v = _Vertices[input.id];
                Output o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_OUTPUT(Output, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.world = mul(_RawToWorld, float4(v.position, 1)).xyz;
                o.vertex = UnityWorldToClipPos(o.world);
                o.normal = mul((float3x3)_RawToWorldNormal, v.normal);
                // The color is RGBA8 in one uint with red in
                // the lowest byte (Pack in the compute shader).
                o.color = float4(float3(v.color & 255, (v.color >> 8) & 255, (v.color >> 16) & 255) / 255.0, 1);
                return o;
            }
            float4 frag(Output i) : SV_Target
            {
                // _WorldSpaceCameraPos is per eye in stereo, so set the eye index first.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
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
