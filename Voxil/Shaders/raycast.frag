#version 450 core
out vec4 FragColor;
in vec2 uv;

uniform mat4 uInvView;
uniform mat4 uInvProjection;
uniform bool uShowDebugHeatmap;

// Подключаем все библиотеки
#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"

// ВАЖНО: Подключаем реализацию воды
#include "include/water_impl.glsl" 
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"

void main() {
    // Генерация луча
    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);

    if(abs(rayDir.x) < 1e-6) rayDir.x = 1e-6;
    if(abs(rayDir.y) < 1e-6) rayDir.y = 1e-6;
    if(abs(rayDir.z) < 1e-6) rayDir.z = 1e-6;
    rayDir = normalize(rayDir);

    int steps = 0;

    // 1. Статический мир
    float tStatic = uRenderDistance;
    uint matStatic = 0u;
    vec3 normStatic = vec3(0);
    bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, 0.0, tStatic, matStatic, normStatic, steps);

    // 2. Динамические объекты
    float tDyn = uRenderDistance;
    int idDyn = -1;
    vec3 normDyn = vec3(0);
    bool hitDyn = false;

    if (uObjectCount > 0) {
        hitDyn = TraceDynamicRay(uCamPos, rayDir, min(tStatic, uRenderDistance), tDyn, idDyn, normDyn, steps);
    }

    // --- ВЫБОР БЛИЖАЙШЕГО ---
    bool hit = false;
    float tFinal = uRenderDistance;
    vec3 normal = vec3(0);
    vec3 albedo = vec3(0);
    uint matID = 0u;
    bool isDynamic = false;

    if (hitDyn && tDyn < tStatic) {
        hit = true;
        tFinal = tDyn;

        // === ИСПРАВЛЕНИЕ НОРМАЛЕЙ ДИНАМИКИ ===
        // Нормаль приходит в локальном пространстве (AABB).
        // Нам нужно повернуть её так же, как повернут объект.
        // dynObjects[idDyn].model - это mat4. Берем mat3 для вращения.
        mat3 rotMatrix = mat3(dynObjects[idDyn].model);
        normal = normalize(rotMatrix * normDyn);

        matID = 999u; // Условный ID динамики
        albedo = dynObjects[idDyn].color.rgb;
        isDynamic = true;
    }
    else if (hitStatic) {
        hit = true;
        tFinal = tStatic;
        normal = normStatic;
        matID = matStatic;
        albedo = GetColor(matID);
        isDynamic = false;
    }

    // Heatmap (Debug)
    if (uShowDebugHeatmap) {
        float val = float(steps) / 200.0;
        vec3 col = (val < 0.5) ? mix(vec3(0,0,1), vec3(0,1,0), val*2.0) : mix(vec3(0,1,0), vec3(1,0,0), (val-0.5)*2.0);
        FragColor = vec4(col, 1.0);
        return;
    }

    // Фон (Небо)
    vec3 finalColor = GetSkyColor(rayDir, uSunDir);

    if (hit) {
        vec3 hitPos = uCamPos + rayDir * tFinal;

        // === ВОДА ===
        // Если это статика и материал 4 (Вода)
        if (!isDynamic && matID == 4u) {
            #ifdef WATER_MODE_PROCEDURAL
                // 1. Нормаль волн
            vec3 flatNormal = vec3(0, 1, 0); // Предполагаем поверхность воды горизонтальной
            vec3 waterNormal = get_water_normal_photon(hitPos, flatNormal, hitPos.xz, vec2(1, 0), 1.0, false);
            normal = waterNormal;
            #endif

            // 2. Расчет освещения поверхности воды (блики)
            float ndotl = max(dot(normal, uSunDir), 0.0);
            float specular = pow(max(dot(reflect(-uSunDir, normal), -rayDir), 0.0), 64.0);

            // 3. Прозрачность (Трассируем дальше, игнорируя воду)
            float tFloor = tFinal;
            uint matFloor = 0u;
            // TraceRefractionRay должен быть реализован в tracing.glsl (см. ниже)
            bool hitFloor = TraceRefractionRay(hitPos, rayDir, uRenderDistance - tFinal, tFloor, matFloor);

            vec3 floorColor = vec3(0.1, 0.2, 0.5); // Дефолтный цвет глубины
            if (hitFloor) {
                // Если нашли дно -> вычисляем его цвет
                vec3 floorPos = hitPos + rayDir * tFloor;
                // Простое освещение дна (можно добавить тени дна, если мощный GPU)
                vec3 floorAlbedo = GetColor(matFloor);
                floorColor = floorAlbedo * (max(dot(vec3(0,1,0), uSunDir), 0.2));

                // Абсорбция (глубина)
                float depth = tFloor;
                vec3 absorption = exp(-depth * vec3(WATER_ABSORPTION_R_UNDERWATER, WATER_ABSORPTION_G_UNDERWATER, WATER_ABSORPTION_B_UNDERWATER));
                floorColor *= absorption;
            }

            // 4. Отражение неба (Fresnel)
            float fresnel = 0.04 + (1.0 - 0.04) * pow(1.0 - max(dot(normal, -rayDir), 0.0), 5.0);
            vec3 skyRefl = GetSkyColor(reflect(rayDir, normal), uSunDir);

            // Смешиваем дно и небо
            finalColor = mix(floorColor, skyRefl, fresnel);
            finalColor += vec3(1.0) * specular * 0.5; // Блики солнца
        }
        else
        {
            // === ОБЫЧНЫЕ БЛОКИ (Земля, Динамика) ===

            float ndotl = max(dot(normal, uSunDir), 0.0);

            // Тени
            float shadow = 1.0;
            if (ndotl > 0.0) {
                // Сдвигаем точку начала луча тени, чтобы не попасть в себя
                shadow = CalculateShadow(hitPos, normal, uSunDir);
            }

            // AO
            float ao = 1.0;
            #ifdef ENABLE_AO
            if (!isDynamic) ao = CalculateAO(hitPos, normal);
            else ao = 0.7; // Простая имитация AO для кубиков
            #endif

            vec3 directLight = vec3(1.0, 0.98, 0.95) * ndotl * shadow;
            vec3 ambientLight = vec3(0.6, 0.7, 0.9) * 0.3 * ao;

            finalColor = albedo * (directLight + ambientLight);
        }

        // Туман
        finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);
    }

    FragColor = vec4(ApplyPostProcess(finalColor), 1.0);
}