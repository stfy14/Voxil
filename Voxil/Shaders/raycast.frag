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
    // Билинейная интерполяция вручную
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

    // Берем МИНИМУМ. Если хотя бы один из 4-х пикселей попал в объект,
    // мы считаем, что тут есть объект. Это безопасно.
    return min(min(d00, d10), min(d01, d11));
}

void main() {
    // 1. Генерация луча
    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);
    // Защита от деления на ноль
    if(abs(rayDir.x)<1e-6) rayDir.x=1e-6;
    if(abs(rayDir.y)<1e-6) rayDir.y=1e-6;
    if(abs(rayDir.z)<1e-6) rayDir.z=1e-6;
    rayDir=normalize(rayDir);

    int steps = 0;

    // =================================================================================
    // ПРОХОД 1: КАРТА ГЛУБИНЫ (BEAM PASS)
    // Трассируем ВСЕ, чтобы получить честную картину мира.
    // =================================================================================
    #ifdef ENABLE_BEAM_OPTIMIZATION
    if (uIsBeamPass == 1) {
        // Трассируем статику
        float tStatic = uRenderDistance; uint matStatic = 0u; vec3 normStatic = vec3(0);
        bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, 0.0, tStatic, matStatic, normStatic, steps);

        // Трассируем динамику
        // ОПТИМИЗАЦИЯ: Ограничиваем поиск динамики расстоянием до статики.
        // Нет смысла искать кубы за стеной.
        float maxDynDist = hitStatic ? tStatic : uRenderDistance;
        float tDyn = uRenderDistance; int idDyn = -1; vec3 normDyn = vec3(0);
        bool hitDyn = false;

        if (uObjectCount > 0) {
            hitDyn = TraceDynamicRay(uCamPos, rayDir, maxDynDist, tDyn, idDyn, normDyn, steps);
        }

        // Выбираем ближайшую дистанцию
        // Если ничего не попали - пишем uRenderDistance
        float finalDepth = uRenderDistance;
        if (hitDyn) finalDepth = tDyn;
        else if (hitStatic) finalDepth = tStatic;

        // Записываем результат: R = Глубина.
        FragColor = vec4(finalDepth, 0.0, 0.0, 1.0);
        return;
    }
    #endif

    // =================================================================================
    // ПРОХОД 2: ОСНОВНОЙ РЕНДЕР (MAIN PASS)
    // Используем данные из Прохода 1, чтобы не делать лишнюю работу.
    // =================================================================================

    float tStart = 0.0;

    #ifdef ENABLE_BEAM_OPTIMIZATION
    if (uIsBeamPass == 0) {
        float beamDist = GetConservativeBeamDist(uv);

        // ГЛАВНАЯ ОПТИМИЗАЦИЯ:
        // Если карта глубины говорит, что тут пусто (>= uRenderDistance),
        // значит мы ТОЧНО знаем, что это небо.
        // Нам НЕ НУЖНО запускать TraceStaticRay или TraceDynamicRay.
        if (beamDist >= uRenderDistance - 1.0) {
            vec3 finalColor = GetSkyColor(rayDir, uSunDir);
            FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
            return; // <-- ВЫХОДИМ СРАЗУ. 0 шагов трассировки!
        }

        // Если объект есть, начинаем трассировку не с 0, а поближе к нему.
        // Отступаем 4 метра для безопасности (чтобы не провалиться сквозь воксель из-за интерполяции)
        tStart = max(0.0, beamDist - 4.0);
    }
    #endif

    // 2. Повторная трассировка (Refinement)
    // Мы знаем ПРИМЕРНО, где объект (благодаря tStart), но нам нужно точно найти
    // точку пересечения, нормаль и материал. Так как мы начали близко, это будет очень быстро.

    float tStatic = uRenderDistance; uint matStatic = 0u; vec3 normStatic = vec3(0);
    bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, tStart, tStatic, matStatic, normStatic, steps);

    // Динамику тоже ищем, но теперь у нас есть оптимизированный tStatic, чтобы ограничить поиск
    float tDyn = uRenderDistance; int idDyn = -1; vec3 normDyn = vec3(0);
    bool hitDyn = false;
    if (uObjectCount > 0) {
        // Мы можем использовать tStart и для динамики, если уверены, 
        // что dynamic bounding box учтен в Beam Pass (а мы это сделали выше).
        // Но для надежности динамику часто ищут с нуля или с безопасного отступа.
        // Попробуем с tStart, так как мы починили Beam Pass.
        hitDyn = TraceDynamicRay(uCamPos, rayDir, min(tStatic, uRenderDistance), tDyn, idDyn, normDyn, steps);
    }

    // 3. Выбор ближайшего (Z-Buffer logic)
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

    // 4. Heatmap (для проверки производительности)
    if (uShowDebugHeatmap) {
        float val = float(steps) / 300.0;
        vec3 col = vec3(0.0);
        if (val < 0.25)      col = mix(vec3(0,0,0.5), vec3(0,0,1), val * 4.0);
        else if (val < 0.5)  col = mix(vec3(0,0,1),   vec3(0,1,0), (val-0.25)*4.0);
        else if (val < 0.75) col = mix(vec3(0,1,0),   vec3(1,1,0), (val-0.5)*4.0);
        else                 col = mix(vec3(1,1,0),   vec3(1,0,0), (val-0.75)*4.0);
        FragColor = vec4(col, 1.0);
        return;
    }

    // 5. Рендеринг
    vec3 finalColor = GetSkyColor(rayDir, uSunDir);

    if (hit) {
        vec3 hitPos = uCamPos + rayDir * tFinal;
        float ndotl = max(dot(normal, uSunDir), 0.0);

        // ОПТИМИЗАЦИЯ ТЕНЕЙ:
        // Если поверхность отвернута от солнца, тень считать не надо - там и так темно.
        float shadow = 1.0;
        if (ndotl > 0.0) {
            shadow = CalculateShadow(hitPos, normal, uSunDir);
        }

        float ao = 1.0;
        #ifdef ENABLE_AO
            // Динамике часто не нужен дорогой воксельный AO
        if (!isDynamic) ao = CalculateAO(hitPos, normal);
        #endif

        vec3 direct = vec3(1.0, 0.98, 0.9) * ndotl * shadow;
        vec3 ambient = vec3(0.6, 0.7, 0.9) * 0.3 * ao;

        finalColor = albedo * (direct + ambient);
        finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);
    }

    FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
}