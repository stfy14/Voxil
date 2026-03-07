// --- START OF FILE shadow.frag ---

#version 450 core
layout(location = 0) out vec2 outShadowAo;

in vec2 uv;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"

uniform sampler2D uGColor;
uniform sampler2D uGData;

// НОВЫЙ ПАРАМЕТР: 1 (Full Res), 2 (Half Res), 4 (Quarter Res)
uniform int uShadowDownscale; 

void main() {
    ivec2 shadowCoord = ivec2(gl_FragCoord.xy);
    
    // Динамически масштабируем координаты в зависимости от настроек графики
    ivec2 fullCoord = shadowCoord * uShadowDownscale; 

    float tFinal       = texelFetch(uGColor, fullCoord, 0).a;
    vec4  dataVal      = texelFetch(uGData,  fullCoord, 0);
    vec3  normal       = dataVal.rgb;
    float directFactor = dataVal.a;

    if (tFinal > uRenderDistance + 1.0) {
        outShadowAo = vec2(1.0, 1.0);
        return;
    }

    // Идеально точный UV для текущего масштаба теней
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
    if (hasLight && directFactor > 0.001) {
        shadow = CalculateShadow(hitPos, normal, activeLightDir);
    }

    float ao = 1.0;
    #ifdef ENABLE_AO
    ao = CalculateAO(hitPos, normal);
    #endif

    outShadowAo = vec2(shadow, ao);
}