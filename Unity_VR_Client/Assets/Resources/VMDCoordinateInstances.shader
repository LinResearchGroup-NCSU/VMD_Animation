// Draws the instanced atom spheres and bond cylinders of VMDCoordinateScene (VDW
// and CPK on the coordinate route) through the VMDCoordinateInstances material.
// Lighting comes from VMDLighting.cginc. Back faces are culled (the ShaderLab
// default), unlike the other three VMD shaders, which draw both faces.
Shader "VMD/CoordinateInstances"
{
    // Per-instance color; VMDCoordinateScene sets it for each batch as an array.
    Properties { _AtomColor ("Atom color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            // Must match the batch size of 511 instances in VMDCoordinateScene.
            #pragma instancing_options forcemaxcount:511
            #include "UnityCG.cginc"
            #include "VMDLighting.cginc"
            UNITY_INSTANCING_BUFFER_START(Atoms)
                UNITY_DEFINE_INSTANCED_PROP(float4, _AtomColor)
            UNITY_INSTANCING_BUFFER_END(Atoms)
            // The instance ID selects the per-instance color. The stereo macros
            // serve Single Pass Instanced XR rendering (README, "Stereo rendering").
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex : SV_POSITION; float3 normal : TEXCOORD0; float3 world : TEXCOORD1; float4 color : COLOR; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.color = UNITY_ACCESS_INSTANCED_PROP(Atoms, _AtomColor);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return float4(VMDLighting(i.color.rgb, normalize(i.normal), i.world), 1);
            }
            ENDCG
        }
    }
}
