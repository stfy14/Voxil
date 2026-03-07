// --- START OF FILE shadow_upsample.frag ---
#version 450 core

layout(location = 0) out vec2 outShadowAo;

in vec2 uv;

uniform sampler2D uShadowHalfRes; // Теперь это может быть FullRes, HalfRes или QuarterRes
uniform sampler2D uGData;

// НОВЫЙ ПАРАМЕТР: 1 (Full Res), 2 (Half Res), 4 (Quarter Res)
uniform int uShadowDownscale; 

void main() {
    ivec2 fullCoord = ivec2(gl_FragCoord.xy);
    ivec2 shadowCoord = fullCoord / uShadowDownscale;
    
    // Если разрешение 100% (Full Res), нам не нужен умный апсэмплинг! 
    // Просто отдаем точный пиксель, экономя FPS на блюре.
    if (uShadowDownscale <= 1) {
        outShadowAo = texelFetch(uShadowHalfRes, shadowCoord, 0).rg;
        return;
    }

    ivec2 fullSize = textureSize(uGData, 0);
    ivec2 shadowSize = textureSize(uShadowHalfRes, 0);

    vec3 centerNormal = texelFetch(uGData, fullCoord, 0).rgb;
    float centerLen   = dot(centerNormal, centerNormal);

    if (centerLen < 0.01) {
        outShadowAo = texelFetch(uShadowHalfRes, shadowCoord, 0).rg;
        return;
    }
    centerNormal = normalize(centerNormal);

    vec2  result      = vec2(0.0);
    float totalWeight = 0.0;

    for (int dx = -1; dx <= 1; dx++) {
        for (int dy = -1; dy <= 1; dy++) {
            ivec2 sCoord = clamp(shadowCoord + ivec2(dx, dy), ivec2(0), shadowSize - ivec2(1));
            
            // Находим пиксель G-буфера, который соответствует этому уменьшенному пикселю
            ivec2 fCoord = clamp(sCoord * uShadowDownscale, ivec2(0), fullSize - ivec2(1));

            vec3  sampleNormal = texelFetch(uGData, fCoord, 0).rgb;
            float sampleLen    = dot(sampleNormal, sampleNormal);

            if (sampleLen < 0.01) continue;

            float normalSim = max(0.0, dot(centerNormal, normalize(sampleNormal)));
            float weight    = pow(normalSim, 16.0);

            result      += texelFetch(uShadowHalfRes, sCoord, 0).rg * weight;
            totalWeight += weight;
        }
    }

    if (totalWeight > 1e-4)
        outShadowAo = result / totalWeight;
    else
        outShadowAo = texelFetch(uShadowHalfRes, shadowCoord, 0).rg; // fallback
}