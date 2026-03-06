// --- START OF FILE raycast.frag ---

#version 450 core
out vec4 FragColor;
in vec2 uv;
uniform mat4 uCleanProjection;

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
    // ВАЖНО: глубина пишется ЧИСТОЙ матрицей (без джиттера)
    // Иначе TAA не сможет правильно восстановить мировую позицию
    vec4 clip_space_pos = uCleanProjection * uView * vec4(pos, 1.0);
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
    uint dynMatID = 0u;    // ← НОВОЕ
    bool hitDyn = false;

    float maxDynSearch = hitStatic ? tStatic : uRenderDistance;

    if (uObjectCount > 0) {
        hitDyn = TraceDynamicRay(
            uCamPos, rayDir, min(tStatic, uRenderDistance),
            tDyn, idDyn, normDyn, dynMatID, steps);  // ← передаём dynMatID
    }

    bool hit = false; float tFinal = uRenderDistance; vec3 normal = vec3(0); vec3 albedo = vec3(0); uint matID = 0u; bool isDynamic = false;

    if (hitDyn && tDyn < tStatic) {
        hit = true; tFinal = tDyn;
        normal = normalize(normDyn);
        albedo = (dynMatID != 0u) ? GetColor(dynMatID) : dynObjects[idDyn].color.rgb;
        isDynamic = true;
        
        #ifdef EDITOR_MODE
            vec3 localHit = (dynObjects[idDyn].invModel * vec4(uCamPos + rayDir * tDyn, 1.0)).xyz;
            if (all(greaterThanEqual(localHit, uHoverVoxelMin)) &&
            all(lessThan(localHit, uHoverVoxelMax)))
            {
                albedo = mix(albedo, vec3(1.0), 0.25);
            }
        #endif
    }
    else if (hitStatic) {
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

        // === ВЫБОР ДОМИНИРУЮЩЕГО ИСТОЧНИКА СВЕТА ===
        float sunIntensity = clamp(uSunDir.y * 5.0, 0.0, 1.0);
        // Луна дает свет, только когда она выше горизонта И когда солнце село 
        // (чтобы не перебивать дневной свет и не создавать двойных теней)
        float moonIntensity = clamp(uMoonDir.y * 5.0, 0.0, 1.0) * 0.25 * clamp(-uSunDir.y * 5.0, 0.0, 1.0);

        vec3 activeLightDir;
        vec3 lightColor;
        float lightIntensity;

        if (sunIntensity > 0.05) {
            // День / Закат
            activeLightDir = uSunDir;
            lightColor = vec3(1.0, 0.95, 0.85);
            lightIntensity = sunIntensity;
        } else if (moonIntensity > 0.01) {
            // Ночь (Луна светит)
            activeLightDir = uMoonDir;
            lightColor = vec3(0.2, 0.35, 0.6); // Холодный лунный свет
            lightIntensity = moonIntensity;
        } else {
            // Глухая ночь (Луна за горизонтом)
            activeLightDir = vec3(0, 1, 0); // Вектор вверх, чтобы тени ушли вниз
            lightColor = vec3(0.0);
            lightIntensity = 0.0;
        }

        float ndotl = max(dot(normal, activeLightDir), 0.0);
        float shadow = 1.0;

        // Кастуем тени только если есть хоть какой-то направленный свет
        if (ndotl > 0.0 && lightIntensity > 0.01) {
            shadow = CalculateShadow(hitPos, normal, activeLightDir);
        }

        float ao = 1.0;
        #ifdef ENABLE_AO
        if (!isDynamic) ao = CalculateAO(hitPos, normal);
        #endif

        // Ambient (глобальный рассеянный свет) зависит ТОЛЬКО от солнца
        float dayAmbient = 0.35;
        float nightAmbient = 0.04;
        float currentAmbient = mix(nightAmbient, dayAmbient, clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0));
        vec3 ambient = vec3(0.6, 0.7, 0.9) * currentAmbient * ao;

        vec3 direct = lightColor * ndotl * shadow * lightIntensity;

        finalColor = albedo * (direct + ambient);
        finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);

        FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
        gl_FragDepth = ComputeDepth(hitPos);
    }
    else {
        FragColor = vec4(ApplyPostProcess(finalColor), 1.0);

        // Пишем глубину (максимальную)
        gl_FragDepth = 0.999999;
    }
}