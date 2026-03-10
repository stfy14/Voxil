#version 450 core

layout(location = 0) out vec2 outShadowAo;
layout(location = 1) out vec4 outPointLights; // Сохраняем размытый свет сюда

in vec2 uv;

uniform sampler2D uShadowHalfRes;
uniform sampler2D uPointLightHalfRes;
uniform sampler2D uGData;

uniform int uShadowDownscale;

void main() {
    ivec2 fullCoord = ivec2(gl_FragCoord.xy);
    ivec2 shadowCoord = fullCoord / uShadowDownscale;

    if (uShadowDownscale <= 1) {
        outShadowAo = texelFetch(uShadowHalfRes, shadowCoord, 0).rg;
        outPointLights = texelFetch(uPointLightHalfRes, shadowCoord, 0);
        return;
    }

    ivec2 fullSize = textureSize(uGData, 0);
    ivec2 shadowSize = textureSize(uShadowHalfRes, 0);

    vec3 centerNormal = texelFetch(uGData, fullCoord, 0).rgb;
    float centerLen   = dot(centerNormal, centerNormal);

    if (centerLen < 0.01) {
        outShadowAo = texelFetch(uShadowHalfRes, shadowCoord, 0).rg;
        outPointLights = texelFetch(uPointLightHalfRes, shadowCoord, 0);
        return;
    }
    centerNormal = normalize(centerNormal);

    vec2  resultShadow = vec2(0.0);
    vec3  resultPointLight = vec3(0.0);
    float totalWeight = 0.0;

    for (int dx = -1; dx <= 1; dx++) {
        for (int dy = -1; dy <= 1; dy++) {
            ivec2 sCoord = clamp(shadowCoord + ivec2(dx, dy), ivec2(0), shadowSize - ivec2(1));
            ivec2 fCoord = clamp(sCoord * uShadowDownscale, ivec2(0), fullSize - ivec2(1));

            vec3  sampleNormal = texelFetch(uGData, fCoord, 0).rgb;
            float sampleLen    = dot(sampleNormal, sampleNormal);

            if (sampleLen < 0.01) continue;

            float normalSim = max(0.0, dot(centerNormal, normalize(sampleNormal)));
            float weight    = pow(normalSim, 16.0);

            resultShadow += texelFetch(uShadowHalfRes, sCoord, 0).rg * weight;
            resultPointLight += texelFetch(uPointLightHalfRes, sCoord, 0).rgb * weight;
            totalWeight += weight;
        }
    }

    if (totalWeight > 1e-4) {
        outShadowAo = resultShadow / totalWeight;
        outPointLights = vec4(resultPointLight / totalWeight, 1.0);
    } else {
        outShadowAo = texelFetch(uShadowHalfRes, shadowCoord, 0).rg;
        outPointLights = texelFetch(uPointLightHalfRes, shadowCoord, 0);
    }
}