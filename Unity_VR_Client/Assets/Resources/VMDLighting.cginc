#ifndef VMD_LIGHTING_INCLUDED
#define VMD_LIGHTING_INCLUDED
// Lighting shared by every live (coordinate-driven) renderer. It reproduces how a VMD
// OBJ export looks in VMDStaticGeometry.shader: VMD's Wavefront exporter ignores the
// material and writes Ka 0, Kd = 0.65 color, Ks = 0.5 color and Ns 40 for every rep.
float3 VMDLighting(float3 color, float3 normal, float3 world)
{
    float3 view = normalize(_WorldSpaceCameraPos - world);
    float3 light = normalize(float3(0.3, 0.6, -0.8));
    float diffuse = max(0, dot(normal, light));
    float specular = pow(max(0, dot(normal, normalize(light + view))), 40);
    return color * 0.65 * (0.3 + diffuse) + color * 0.5 * specular;
}
#endif
