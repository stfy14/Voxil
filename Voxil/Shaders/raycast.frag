#version 450 core
out vec4 FragColor;
in vec2 uv;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"
#include "include/water_impl.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"

void main() {
    // 1. Генерация луча
    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);
    if(abs(rayDir.x)<1e-6) rayDir.x=1e-6; if(abs(rayDir.y)<1e-6) rayDir.y=1e-6; if(abs(rayDir.z)<1e-6) rayDir.z=1e-6; rayDir=normalize(rayDir);

    int steps = 0;

    // 2. Трассировка
    float tStatic = uRenderDistance; uint matStatic = 0u; vec3 normStatic = vec3(0);
    bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, 0.0, tStatic, matStatic, normStatic, steps);

    float tDyn = uRenderDistance; int idDyn = -1; vec3 normDyn = vec3(0);
    bool hitDyn = false;
    if (uObjectCount > 0) {
        hitDyn = TraceDynamicRay(uCamPos, rayDir, min(tStatic, uRenderDistance), tDyn, idDyn, normDyn, steps);
    }

    // 3. Выбор ближайшего
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

    // 4. Heatmap
    if (uShowDebugHeatmap) {
        float val = float(steps) / 250.0; // Увеличил делитель для лучшей градации
        vec3 col = (val < 0.5) ? mix(vec3(0,0,1), vec3(0,1,0), val*2.0) : mix(vec3(0,1,0), vec3(1,0,0), (val-0.5)*2.0);
        FragColor = vec4(col, 1.0);
        return;
    }

    // 5. Рендеринг
    vec3 finalColor = GetSkyColor(rayDir, uSunDir);
    if (hit) {
        vec3 hitPos = uCamPos + rayDir * tFinal;

        // === ТВОЯ ОРИГИНАЛЬНАЯ ЛОГИКА ВОДЫ (ОТРУБИЛ К ХУЯМ ВЕРНУТЬ НА 4u)===
        if (!isDynamic && matID == 999u) {
            vec3 waterNormal = get_water_normal_photon(hitPos, normal, hitPos.xz, vec2(0), 1.0, false);
            vec3 reflDir = reflect(rayDir, waterNormal);

            vec3 reflectionColor;
            float tRefl = uRenderDistance; uint matRefl = 0u; vec3 normRefl = vec3(0);
            if (TraceStaticRay(hitPos, reflDir, uRenderDistance, 0.01, tRefl, matRefl, normRefl)) {
                vec3 hitPosRefl = hitPos + reflDir * tRefl;
                vec3 albedoRefl = GetColor(matRefl);
                float shadowRefl = CalculateShadow(hitPosRefl, normRefl, uSunDir);
                float ndotlRefl = max(dot(normRefl, uSunDir), 0.0);
                reflectionColor = albedoRefl * (ndotlRefl * shadowRefl * 0.8 + 0.2);
                reflectionColor = ApplyFog(reflectionColor, reflDir, uSunDir, tRefl, uRenderDistance);
            } else {
                reflectionColor = GetSkyColor(reflDir, uSunDir);
            }

            vec3 refractionColor;
            #ifdef ENABLE_WATER_TRANSPARENCY
                float tRefr = uRenderDistance; uint matRefr = 0u;
            if (TraceRefractionRay(hitPos, rayDir, 64.0, tRefr, matRefr)) {
                vec3 hitPosRefr = hitPos + rayDir * tRefr;
                vec3 albedoRefr = GetColor(matRefr);
                float depth = tRefr;
                vec3 absorption = exp(-depth * vec3(0.2, 0.08, 0.04));
                refractionColor = albedoRefr * absorption * 0.7;
            } else {
                refractionColor = vec3(0.05, 0.1, 0.2);
            }
            #else
                refractionColor = vec3(0.0, 0.05, 0.1);
            #endif

            float fresnel = 0.04 + (1.0 - 0.04) * pow(1.0 - max(dot(waterNormal, -rayDir), 0.0), 5.0);
            finalColor = mix(refractionColor, reflectionColor, fresnel);
            finalColor += vec3(1.0) * pow(max(dot(reflect(-uSunDir, waterNormal), -rayDir), 0.0), 64.0) * 0.5;
        }
        else {
            // === Твоя логика обычных блоков ===
            float ndotl = max(dot(normal, uSunDir), 0.0);
            float shadow = 1.0;
            if (ndotl > 0.0) shadow = CalculateShadow(hitPos, normal, uSunDir);
            float ao = 1.0;
            #ifdef ENABLE_AO
             if (!isDynamic) ao = CalculateAO(hitPos, normal);
            #endif
             vec3 direct = vec3(1.0, 0.98, 0.9) * ndotl * shadow;
            vec3 ambient = vec3(0.6, 0.7, 0.9) * 0.3 * ao;
            finalColor = albedo * (direct + ambient);
        }

        finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);
    }

    FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
}