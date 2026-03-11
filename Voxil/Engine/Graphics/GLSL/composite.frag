#version 450 core

layout(location = 0) out vec4 FragColor;
in vec2 uv;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"

uniform sampler2D uGColor;
uniform sampler2D uGData;
uniform sampler2D uShadowFull;
uniform sampler2D uPointLightFull; // Содержит и PointLights, и GI!

void main() {
    ivec2 fullCoord = ivec2(gl_FragCoord.xy);

    vec4  colorData    = texelFetch(uGColor, fullCoord, 0);
    float directFactor = texelFetch(uGData, fullCoord, 0).a;
    vec3  normal       = texelFetch(uGData, fullCoord, 0).rgb;
    vec2  shadowAoVal  = texelFetch(uShadowFull, fullCoord, 0).rg;
    vec3  pointLightVal = texelFetch(uPointLightFull, fullCoord, 0).rgb;

    vec3  albedo = colorData.rgb;
    bool isEmissive = (albedo.r < 0.0 || albedo.g < 0.0 || albedo.b < 0.0);
    albedo = abs(albedo);

    float tFinal = colorData.a;
    float shadow = shadowAoVal.r;
    float ao     = shadowAoVal.g;

    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    target.xyz /= target.w;
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);

    if (tFinal > uRenderDistance + 1.0) {
        FragColor = vec4(ApplyPostProcess(albedo), 1.0);
        return;
    }

    vec3 hitPos = uCamPos + rayDir * tFinal;

    float sunIntensity  = clamp(uSunDir.y  * 5.0, 0.0, 1.0);
    float moonIntensity = clamp(uMoonDir.y * 5.0, 0.0, 1.0) * 0.25 * clamp(-uSunDir.y * 5.0, 0.0, 1.0);

    vec3 lightColor = vec3(0.0);
    if (sunIntensity > 0.05) lightColor = vec3(1.0, 0.95, 0.85);
    else if (moonIntensity > 0.01) lightColor = vec3(0.2, 0.35, 0.6);

    vec3 direct = lightColor * directFactor * shadow;
    vec3 indirect = vec3(0.0);

    #ifndef ENABLE_GI
    // Старый фоллбэк, если зонды отключены
    float dayAmbient     = 0.35;
    float nightAmbient   = 0.05;
    float currentAmbient = mix(nightAmbient, dayAmbient, clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0));
    indirect = albedo * vec3(0.6, 0.7, 0.9) * currentAmbient * max(ao, 0.15);
    #endif

    // pointLightVal содержит апсемпленный GI и PointLights из shadow.frag
    vec3 pointLightContrib = albedo * pointLightVal;

    vec3 finalColor = albedo * direct + indirect + pointLightContrib;

    if (isEmissive) {
        finalColor += albedo * 25.0;
    }

    finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);
    FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
}