// --- START OF FILE composite.frag ---
#version 450 core

layout(location = 0) out vec4 FragColor;

in vec2 uv;

#include "include/common.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"

uniform sampler2D uGColor;      
uniform sampler2D uGData;       
uniform sampler2D uShadowFull;  

void main() {
    // ИСПРАВЛЕНИЕ: Никакой интерполяции G-буфера на стыках
    ivec2 fullCoord = ivec2(gl_FragCoord.xy);
    
    vec4  colorData    = texelFetch(uGColor, fullCoord, 0);
    float directFactor = texelFetch(uGData, fullCoord, 0).a;
    vec2  shadowAoVal  = texelFetch(uShadowFull, fullCoord, 0).rg;

    vec3  albedo = colorData.rgb;
    float tFinal = colorData.a;
    float shadow = shadowAoVal.r;
    float ao     = shadowAoVal.g;

    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    target.xyz /= target.w; // <- Точная проекция
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);
    
    if (abs(rayDir.x) < 1e-6) rayDir.x = (rayDir.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = (rayDir.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = (rayDir.z < 0.0) ? -1e-6 : 1e-6;
    rayDir = normalize(rayDir);

    if (tFinal > uRenderDistance + 1.0) {
        FragColor = vec4(ApplyPostProcess(albedo), 1.0);
        return;
    }

    vec3 hitPos = uCamPos + rayDir * tFinal;

    float sunIntensity  = clamp(uSunDir.y  * 5.0, 0.0, 1.0);
    float moonIntensity = clamp(uMoonDir.y * 5.0, 0.0, 1.0) * 0.25
                        * clamp(-uSunDir.y * 5.0, 0.0, 1.0);

    vec3 lightColor;
    if (sunIntensity > 0.05) {
        lightColor = vec3(1.0, 0.95, 0.85);
    } else if (moonIntensity > 0.01) {
        lightColor = vec3(0.2, 0.35, 0.6);
    } else {
        lightColor = vec3(0.0);
    }

    float dayAmbient     = 0.35;
    float nightAmbient   = 0.04;
    float currentAmbient = mix(nightAmbient, dayAmbient,
                               clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0));
    vec3 ambient = vec3(0.6, 0.7, 0.9) * currentAmbient * ao;

    vec3 direct = lightColor * directFactor * shadow;

    vec3 finalColor = albedo * (direct + ambient);
    finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);

    FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
}