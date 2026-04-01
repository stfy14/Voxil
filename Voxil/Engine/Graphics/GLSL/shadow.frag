// --- START OF FILE shadow.frag.txt ---
#version 450 core

layout(location = 0) out vec2 outShadowAo;
layout(location = 1) out vec4 outPointLights; // Сохраняем размытый свет и GI сюда

in vec2 uv;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"

#ifdef ENABLE_GI
#include "include/vct.glsl"
#endif

uniform sampler2D uGColor;
uniform sampler2D uGData;

uniform int uShadowDownscale;

void main() {
    ivec2 shadowCoord = ivec2(gl_FragCoord.xy);
    ivec2 fullCoord   = shadowCoord * uShadowDownscale;

    float tFinal       = texelFetch(uGColor, fullCoord, 0).a;
    vec4  dataVal      = texelFetch(uGData,  fullCoord, 0);
    vec3  normal       = dataVal.rgb;
    float directFactor = dataVal.a;

    if (tFinal > uRenderDistance + 1.0) {
        outShadowAo = vec2(1.0, 1.0);
        outPointLights = vec4(0.0);
        return;
    }

    vec2 fullTexSize = vec2(textureSize(uGColor, 0));
    vec2 exactUV = (vec2(fullCoord) + 0.5) / fullTexSize;

    vec4 target = uInvProjection * vec4(exactUV * 2.0 - 1.0, 1.0, 1.0);
    target.xyz /= target.w;
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);

    if (abs(rayDir.x) < 1e-6) rayDir.x = (rayDir.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = (rayDir.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = (rayDir.z < 0.0) ? -1e-6 : 1e-6;
    rayDir = normalize(rayDir);

    vec3 hitPos = uCamPos + rayDir * tFinal;

    float sunIntensity  = clamp(uSunDir.y  * 5.0, 0.0, 1.0);
    float moonIntensity = clamp(uMoonDir.y * 5.0, 0.0, 1.0) * 0.25
    * clamp(-uSunDir.y * 5.0, 0.0, 1.0);

    vec3 activeLightDir = uSunDir;
    bool hasLight       = (sunIntensity > 0.05);
    if (!hasLight && moonIntensity > 0.01) {
        activeLightDir = uMoonDir;
        hasLight = true;
    }

    float shadow = 1.0;
    if (hasLight) {
        if (directFactor <= 0.001) {
            shadow = 0.0;
        } else {
            shadow = CalculateShadow(hitPos, normal, activeLightDir);
        }
    }

    float ao = 1.0;
    #ifdef ENABLE_AO
    ao = CalculateAO(hitPos, normal);
    #endif

    outShadowAo = vec2(shadow, ao);

    vec3 pointLightColor = vec3(0.0);
    if (uPointLightCount > 0) {
        pointLightColor = EvaluatePointLights(hitPos, normal);
    }

    #ifdef ENABLE_GI
    vec3 giIrradiance = SampleGIVCT(hitPos, normal);
    pointLightColor += giIrradiance * max(ao, 0.15);
    #endif

    outPointLights = vec4(pointLightColor, 1.0);
}