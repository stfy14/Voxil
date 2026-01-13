// --- START OF FILE raycast.frag ---

#version 450 core
out vec4 FragColor;
in vec2 uv;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"
#include "include/water_impl.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"

float GetConservativeBeamDist(vec2 uv) {
    vec2 texSize = vec2(textureSize(uBeamTexture, 0));
    vec2 pixelCoord = uv * texSize - 0.5;
    vec2 baseCoord = floor(pixelCoord) + 0.5;

    vec2 uv00 = (baseCoord + vec2(0.0, 0.0)) / texSize;
    vec2 uv10 = (baseCoord + vec2(1.0, 0.0)) / texSize;
    vec2 uv01 = (baseCoord + vec2(0.0, 1.0)) / texSize;
    vec2 uv11 = (baseCoord + vec2(1.0, 1.0)) / texSize;

    float d00 = texture(uBeamTexture, uv00).r;
    float d10 = texture(uBeamTexture, uv10).r;
    float d01 = texture(uBeamTexture, uv01).r;
    float d11 = texture(uBeamTexture, uv11).r;

    return min(min(d00, d10), min(d01, d11));
}

float ComputeDepth(vec3 pos) {
    vec4 clip_space_pos = uProjection * uView * vec4(pos, 1.0);
    return (clip_space_pos.z / clip_space_pos.w) * 0.5 + 0.5;
}

void main() {
    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);
    if(abs(rayDir.x)<1e-6) rayDir.x=1e-6;
    if(abs(rayDir.y)<1e-6) rayDir.y=1e-6;
    if(abs(rayDir.z)<1e-6) rayDir.z=1e-6;
    rayDir=normalize(rayDir);

    int steps = 0;

    #ifdef ENABLE_BEAM_OPTIMIZATION
    if (uIsBeamPass == 1) {
        // Трассируем ТОЛЬКО статику
        float tStatic = uRenderDistance; uint matStatic = 0u; vec3 normStatic = vec3(0);
        bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, 0.0, tStatic, matStatic, normStatic, steps);

        // --- УБИРАЕМ ТРАССИРОВКУ ДИНАМИКИ ИЗ BEAM PASS ---
    /*
        float maxDynDist = hitStatic ? tStatic : uRenderDistance;
        float tDyn = uRenderDistance; int idDyn = -1; vec3 normDyn = vec3(0);
        bool hitDyn = false;

        if (uObjectCount > 0) {
            hitDyn = TraceDynamicRay(uCamPos, rayDir, maxDynDist, tDyn, idDyn, normDyn, steps);
        }
        */

        // В карту глубин пишем только расстояние до земли/стен
        float finalDepth = uRenderDistance;
        if (hitStatic) finalDepth = tStatic;
        // if (hitDyn) finalDepth = tDyn; // <-- Убираем

        FragColor = vec4(finalDepth, 0.0, 0.0, 1.0);
        return;
    }
    #endif

    float tStart = 0.0;
    bool skipStatic = false;

    #ifdef ENABLE_BEAM_OPTIMIZATION
    if (uIsBeamPass == 0) {
        float beamDist = GetConservativeBeamDist(uv);

        // Если Beam Pass говорит, что тут небо (далеко)
        if (beamDist >= uRenderDistance - 1.0) {
            // Мы НЕ выходим сразу, так как тут может быть динамический объект!
            // Мы просто помечаем, что статику искать не надо.
            skipStatic = true;
        }
        else {
            tStart = max(0.0, beamDist - 4.0);
        }
    }
    #endif

    float tStatic = uRenderDistance;
    uint matStatic = 0u;
    vec3 normStatic = vec3(0);
    bool hitStatic = false;

    if (!skipStatic) {
        hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, tStart, tStatic, matStatic, normStatic, steps);
    }

    // --- ДИНАМИКА ТРАССИРУЕТСЯ ВСЕГДА ---
    float tDyn = uRenderDistance; 
    int idDyn = -1; 
    vec3 normDyn = vec3(0);
    bool hitDyn = false;

    float maxDynSearch = hitStatic ? tStatic : uRenderDistance;
    
    if (uObjectCount > 0) {
        hitDyn = TraceDynamicRay(uCamPos, rayDir, min(tStatic, uRenderDistance), tDyn, idDyn, normDyn, steps);
    }

    bool hit = false; float tFinal = uRenderDistance; vec3 normal = vec3(0); vec3 albedo = vec3(0); uint matID = 0u; bool isDynamic = false;

    if (hitDyn && tDyn < tStatic) {
        hit = true; tFinal = tDyn;
        mat3 rotMatrix = mat3(dynObjects[idDyn].model);
        normal = normalize(rotMatrix * normDyn);
        albedo = dynObjects[idDyn].color.rgb;
        isDynamic = true;
    } else if (hitStatic) {
        hit = true; tFinal = tStatic; normal = normStatic; matID = matStatic; albedo = GetColor(matID);
    }

    if (uShowDebugHeatmap) {
        float val = float(steps) / 300.0;
        vec3 col = vec3(0.0);
        if (val < 0.25)      col = mix(vec3(0,0,0.5), vec3(0,0,1), val * 4.0);
        else if (val < 0.5)  col = mix(vec3(0,0,1),   vec3(0,1,0), (val-0.25)*4.0);
        else if (val < 0.75) col = mix(vec3(0,1,0),   vec3(1,1,0), (val-0.5)*4.0);
        else                 col = mix(vec3(1,1,0),   vec3(1,0,0), (val-0.75)*4.0);
        FragColor = vec4(col, 1.0);

        // === ИСПРАВЛЕНИЕ: ПИШЕМ ГЛУБИНУ ДЛЯ ХИТМАПА ===
        if (hit) gl_FragDepth = ComputeDepth(uCamPos + rayDir * tFinal);
        else gl_FragDepth = 0.999999;
        return;
    }

    vec3 finalColor = GetSkyColor(rayDir, uSunDir);

    if (hit) {
        vec3 hitPos = uCamPos + rayDir * tFinal;
        float ndotl = max(dot(normal, uSunDir), 0.0);

        float shadow = 1.0;
        if (ndotl > 0.0) {
            shadow = CalculateShadow(hitPos, normal, uSunDir);
        }

        float ao = 1.0;
        #ifdef ENABLE_AO
        if (!isDynamic) ao = CalculateAO(hitPos, normal);
        #endif

        vec3 direct = vec3(1.0, 0.98, 0.9) * ndotl * shadow;
        vec3 ambient = vec3(0.6, 0.7, 0.9) * 0.3 * ao;

        finalColor = albedo * (direct + ambient);
        finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);

        FragColor = vec4(ApplyPostProcess(finalColor), 1.0);

        // Пишем глубину
        gl_FragDepth = ComputeDepth(hitPos);
    }
    else {
        FragColor = vec4(ApplyPostProcess(finalColor), 1.0);

        // Пишем глубину (максимальную)
        gl_FragDepth = 0.999999;
    }
}