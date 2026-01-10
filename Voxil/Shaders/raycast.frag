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
    // Защита от деления на ноль
    if(abs(rayDir.x)<1e-6) rayDir.x=1e-6;
    if(abs(rayDir.y)<1e-6) rayDir.y=1e-6;
    if(abs(rayDir.z)<1e-6) rayDir.z=1e-6;
    rayDir=normalize(rayDir);

    // === ЛОГИКА BEAM OPTIMIZATION ===
    float tStart = 0.0;

    #ifdef ENABLE_BEAM_OPTIMIZATION
        if (uIsBeamPass == 0) {
        float beamDist = texture(uBeamTexture, uv).r;

        // Если луч улетел в бесконечность (небо) на первом проходе - выходим сразу!
        // Это дает основной буст FPS на открытых пространствах.
        if (beamDist >= uRenderDistance - 1.0) {
            vec3 finalColor = GetSkyColor(rayDir, uSunDir);
            FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
            return;
        }

        // Иначе начинаем трассировку ближе к объекту
        tStart = max(0.0, beamDist - 4.0 * VOXELS_PER_METER);
    }
    #endif

    int steps = 0;

    // 2. Трассировка
    float tStatic = uRenderDistance; uint matStatic = 0u; vec3 normStatic = vec3(0);
    // Передаем tStart в функцию трассировки!
    bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, tStart, tStatic, matStatic, normStatic, steps);

    // === ЗАПИСЬ РЕЗУЛЬТАТА ДЛЯ BEAM PASS ===
    #ifdef ENABLE_BEAM_OPTIMIZATION
        if (uIsBeamPass == 1) {
        // Если мы ничего не нашли, пишем макс дистанцию
        float dist = hitStatic ? tStatic : uRenderDistance;
        // Пишем в красный канал (R)
        FragColor = vec4(dist, 0.0, 0.0, 1.0);
        return; // Прерываем выполнение, освещение считать не нужно!
    }
    #endif

    // Динамические объекты (для них Beam Opt обычно не делают, т.к. они могут быть мелкими и проскочить)
    float tDyn = uRenderDistance; int idDyn = -1; vec3 normDyn = vec3(0);
    bool hitDyn = false;
    if (uObjectCount > 0) {
        // Динамику ищем всегда от 0 (или tStart, если уверены), но лучше перестраховаться
        // Можно использовать tStart, но динамические объекты часто не пишутся в PageTable
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

    // 4. Heatmap (Дебаг нагрузки)
    if (uShowDebugHeatmap) {
        float val = float(steps) / 250.0;
        vec3 col = (val < 0.5) ? mix(vec3(0,0,1), vec3(0,1,0), val*2.0) : mix(vec3(0,1,0), vec3(1,0,0), (val-0.5)*2.0);
        FragColor = vec4(col, 1.0);
        return;
    }

    // 5. Рендеринг (освещение, туман)
    vec3 finalColor = GetSkyColor(rayDir, uSunDir);

    if (hit) {
        vec3 hitPos = uCamPos + rayDir * tFinal;
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
        finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);
    }

    FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
}