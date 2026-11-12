// Draws the meshes imported from a VMD OBJ/MTL export (the "VMD mesh" route). VMDStaticScene
// creates one material per OBJ part and supplies the MTL material per vertex: COLOR is the
// diffuse color with the opacity in alpha, TEXCOORD0 the specular color (xyz) and exponent
// (w), and TEXCOORD1 the ambient color. Both faces are drawn and lit. Alpha blending is always
// on; for transparent parts VMDStaticScene turns depth writes off (_ZWrite 0) and uses the
// transparent queue. VMDLighting.cginc repeats this lighting for the coordinate-route shaders.
Shader "VMD/StaticGeometry"
{
    // Depth writes: 1 by default; VMDStaticScene sets 0 for transparent parts.
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
            // The instance ID and the stereo macros serve Single Pass
            // Instanced XR rendering (README, "Stereo rendering").
            struct Input {
                float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR;
                float4 specular : TEXCOORD0; float3 ambient : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Output {
                float4 position : SV_POSITION; float3 normal : TEXCOORD0; float3 world : TEXCOORD1;
                float4 color : COLOR; float4 specular : TEXCOORD2; float3 ambient : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Output vert(Input v) {
                Output o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(Output, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.position = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = v.color; o.specular = v.specular; o.ambient = v.ambient;
                return o;
            }
            float4 frag(Output i, fixed facing : VFACE) : SV_Target {
                // _WorldSpaceCameraPos is per eye in stereo, so set the eye index first.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // VFACE is negative on back faces; turn the
                // normal so that both sides are lit alike.
                float3 n = normalize(i.normal) * (facing >= 0 ? 1 : -1);
                float3 view = normalize(_WorldSpaceCameraPos - i.world);
                // Fixed light direction in world space; no scene light is
                // used. The diffuse term gets a constant 0.3 added, and the
                // highlight is Blinn-Phong with the MTL exponent (specular.w).
                float3 light = normalize(float3(0.3, 0.6, -0.8));
                float diffuse = max(0, dot(n, light));
                float spec = pow(max(0, dot(n, normalize(light + view))), i.specular.w);
                return float4(i.ambient + i.color.rgb * (0.3 + diffuse) + i.specular.rgb * spec, i.color.a);
            }
            ENDCG
        }
    }
}
