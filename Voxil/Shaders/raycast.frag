#version 450 core
out vec4 FragColor;
in vec2 uv;
uniform mat4 uInvView;
uniform mat4 uInvProjection;
uniform bool uShowDebugHeatmap;
#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"

void main() {
    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);
    if(abs(rayDir.x)<1e-6) rayDir.x=1e-6; if(abs(rayDir.y)<1e-6) rayDir.y=1e-6; if(abs(rayDir.z)<1e-6) rayDir.z=1e-6; rayDir=normalize(rayDir);

    int steps = 0;
    float tStaticHit = uRenderDistance;
    uint matID = 0u;
    vec3 normal = vec3(0);

    // Трассировка
    bool hit = TraceStaticRay(uCamPos, rayDir, uRenderDistance, 0.0, tStaticHit, matID, normal, steps);

    if (uShowDebugHeatmap) {
        float val = float(steps) / 200.0;
        vec3 col = (val < 0.5) ? mix(vec3(0,0,1), vec3(0,1,0), val*2.0) : mix(vec3(0,1,0), vec3(1,0,0), (val-0.5)*2.0);
        FragColor = vec4(col, 1.0);
        return;
    }

    vec3 color = GetSkyColor(rayDir, uSunDir);
    if (hit) {
        vec3 pos = uCamPos + rayDir * tStaticHit;
        vec3 albedo = GetColor(matID);
        float ndotl = max(dot(normal, uSunDir), 0.0);
        color = albedo * (ndotl * 1.0 + 0.2);
        color = ApplyFog(color, rayDir, uSunDir, tStaticHit, uRenderDistance);
    }
    FragColor = vec4(ApplyPostProcess(color), 1.0);
}