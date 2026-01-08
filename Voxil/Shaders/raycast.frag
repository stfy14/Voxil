#version 450 core
out vec4 FragColor;
in vec2 uv;

uniform mat4 uInvView;
uniform mat4 uInvProjection;

uniform bool uIsBeamPass;
uniform sampler2D uBeamTexture;

// --- РАСКОММЕНТИРУЙ ЭТУ СТРОКУ, ЧТОБЫ УВИДЕТЬ РАБОТУ ОПТИМИЗАЦИИ ---
// Красный цвет = это расстояние мы "пропрыгнули" (сэкономили вычисления)
//#define DEBUG_BEAM_VIEW 

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"
#include "include/water_impl.glsl"

void main() {
    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);

    // Защита от деления на ноль
    if (abs(rayDir.x) < 1e-6) rayDir.x = 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = 1e-6;
    rayDir = normalize(rayDir);

    vec3 safeSunDir = normalize(length(uSunDir) < 0.01 ? vec3(0.2, 0.4, 0.8) : uSunDir);

    // --- BEAM OPTIMIZATION LOGIC ---
    float tStart = 0.0;

    #ifdef ENABLE_BEAM_OPTIMIZATION
    if (!uIsBeamPass) {
        // Мы используем делитель 2 в C# коде (_beamWidth = Screen / 2)
        ivec2 beamCoord = ivec2(gl_FragCoord.xy) / 2;
        float minBeamDist = 100000.0;

        // Выборка 3x3 (или 2x2) для безопасности. Берем МИНИМАЛЬНУЮ глубину соседей.
        // Это гарантирует, что мы не перепрыгнем тонкий объект на границе пикселей.
        for(int x = -1; x <= 1; x++) {
            for(int y = -1; y <= 1; y++) {
                float d = texelFetch(uBeamTexture, beamCoord + ivec2(x, y), 0).r;
                // Если d == 0 (например, небо не записалось или ошибка), считаем дистанцию большой
                // Но лучше считать, что 0 - это ошибка записи, поэтому игнорируем? 
                // Нет, Beam Pass должен писать реальную глубину.
                if (d < minBeamDist) minBeamDist = d;
            }
        }

        // --- АГРЕССИВНЫЕ НАСТРОЙКИ ---
        // Было: 3.0 и 4.0 чанка (48м и 64м). Это слишком много для теста.
        // Стало: 0.5 и 1.0 чанка (8м и 16м).
        const float SAFETY_DISTANCE = float(CHUNK_SIZE) * 0.5;   // Отступаем назад на 8 метров
        const float MIN_OPTIMIZE_DIST = float(CHUNK_SIZE) * 1.0; // Начинаем оптимизировать, если до препятствия > 16 метров

        if (minBeamDist > MIN_OPTIMIZE_DIST) {
            float rawStart = minBeamDist - SAFETY_DISTANCE;
            // Округляем вниз до границы чанка для выравнивания DDA
            tStart = floor(rawStart / float(CHUNK_SIZE)) * float(CHUNK_SIZE);
            tStart = max(0.0, tStart);
        }
    }
    #endif
    // -------------------------------------------

    // === ТРАССИРОВКА ДИНАМИКИ ===
    float tDynamicHit = uRenderDistance;
    int bestObjID = -1;
    vec3 bestLocalNormal = vec3(0);
    bool hitDyn = false;

    // Динамику всегда проверяем с 0, так как Beam Pass мог пропустить движущийся объект
    // (Хотя в идеале Beam Pass должен учитывать и динамику, но сейчас он учитывает всё).
    // Если Beam Pass учитывает динамику, можно использовать tStart, но безопаснее проверять динамику отдельно,
    // так как она может быть "тонкой". Для простоты пока используем полную проверку.
    if (uObjectCount > 0) {
        hitDyn = TraceDynamicRay(uCamPos, rayDir, uRenderDistance, tDynamicHit, bestObjID, bestLocalNormal);
    }

    // === ТРАССИРОВКА СТАТИКИ ===
    float tStaticHit = uRenderDistance;
    uint staticHitMat = 0u;
    vec3 finalNormal = vec3(0);

    // ВАЖНО: Передаем tStart, полученный из Beam Optimization
    bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, tStart, tStaticHit, staticHitMat, finalNormal);

    // Итоговая глубина
    float tEnd = min(tStaticHit, tDynamicHit);

    // === 1. ЭТО BEAM PASS? ЗАПИСЫВАЕМ ГЛУБИНУ ===
    if (uIsBeamPass) {
        FragColor = vec4(tEnd, 0.0, 0.0, 1.0);
        return;
    }

    // === 2. ЭТО ОСНОВНОЙ ПАС ===

    #ifdef DEBUG_BEAM_VIEW
    // ВИЗУАЛИЗАЦИЯ ОПТИМИЗАЦИИ
    // Красный = пропущенное расстояние
    // Серый = реальная отрисовка
    if (tStart > 0.0) {
        // Мы пропустили tStart метров.
        FragColor = vec4(1.0, 0.0, 0.0, 1.0); // Красный, если оптимизация сработала
        // Или более детально:
        // FragColor = vec4(tStart / 100.0, (tEnd - tStart) / 100.0, 0.0, 1.0);
        return;
    }
    #endif

    vec3 finalColor = GetSkyColor(rayDir, safeSunDir);
    float finalDepth = 0.999999;

    if (tEnd < uRenderDistance) {
        bool isDynamic = tDynamicHit < tStaticHit;
        vec3 hitPos = uCamPos + rayDir * tEnd;
        vec3 normal, albedo;
        float ao = 1.0;

        if (isDynamic) {
            normal = normalize(transpose(inverse(mat3(dynObjects[bestObjID].model))) * bestLocalNormal);
            albedo = dynObjects[bestObjID].color.rgb;
        } else {
            normal = finalNormal;
            albedo = GetColor(staticHitMat);
            #ifdef ENABLE_AO
                // ОПТИМИЗАЦИЯ: Не считать AO для воды (ID 4)
            if (staticHitMat != 4u) {
                ao = CalculateAO(hitPos + normal * 0.001, normal);
            }
            #endif
        }

        float ndotl = max(dot(normal, safeSunDir), 0.0);
        float shadowFactor = (ndotl > 0.0) ? CalculateShadow(hitPos, normal, safeSunDir) : 0.0;

        vec3 sunLightColor = vec3(1.0, 0.98, 0.95);
        vec3 direct = albedo * sunLightColor * 1.2 * ndotl * shadowFactor;
        vec3 ambient = albedo * mix(vec3(0.15, 0.15, 0.2), vec3(0.3, 0.5, 0.8), 0.5 + 0.5 * normal.y) * 0.7 * ao;
        finalColor = direct + ambient;

        // Вода (стандартный блок)
        if (!isDynamic && staticHitMat == 4u) {
            vec3 viewDir = -rayDir;
            vec3 tangentViewDir = vec3(viewDir.x, viewDir.z, viewDir.y);
            vec2 parallaxUV = get_water_parallax_coord(tangentViewDir, hitPos.xz, vec2(0), false);
            vec3 waterNormalTangent = get_water_normal_photon(hitPos, finalNormal, parallaxUV, vec2(0), 1.0, false);
            vec3 waterNormal = normalize(vec3(waterNormalTangent.x, waterNormalTangent.z, waterNormalTangent.y));
            float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(waterNormal, viewDir), 0.0), 5.0);
            vec3 refDir = reflect(rayDir, waterNormal);
            if (refDir.y < 1e-4) refDir.y = 1e-4; refDir = normalize(refDir);
            vec3 refOrigin = hitPos + finalNormal * 0.002;
            float tRefStatic = 150.0; uint matRef = 0u; vec3 refNormStatic = vec3(0);
            float tRefDyn = 150.0; int idRef = -1; vec3 refNormDyn = vec3(0);
            bool hitRefStatic = TraceStaticRay(refOrigin, refDir, 150.0, 0.0, tRefStatic, matRef, refNormStatic);
            bool hitRefDyn = uObjectCount > 0 ? TraceDynamicRay(refOrigin, refDir, 150.0, tRefDyn, idRef, refNormDyn) : false;
            vec3 reflectionColor = GetSkyColor(refDir, safeSunDir);
            float tRefFinal = min(tRefStatic, tRefDyn);
            if (tRefFinal < 150.0) {
                vec3 refAlbedo, refN;
                if(tRefDyn < tRefStatic) { refAlbedo = dynObjects[idRef].color.rgb; refN = normalize(transpose(inverse(mat3(dynObjects[idRef].model))) * refNormDyn); }
                else { refAlbedo = GetColor(matRef); refN = refNormStatic; }
                float refLight = max(dot(refN, safeSunDir), 0.2);
                reflectionColor = refAlbedo * refLight * sunLightColor;
                reflectionColor = mix(reflectionColor, GetSkyColor(refDir, safeSunDir), pow(clamp(tRefFinal / 150.0, 0.0, 1.0), 2.0));
            }
            vec3 refractionColor = vec3(0.05, 0.1, 0.15);
            #ifdef ENABLE_WATER_TRANSPARENCY
            vec3 refrOrigin = hitPos - finalNormal * 0.002; float tBot = 40.0; uint matBot = 0u;
            // TraceRefractionRay (которая использует TraceShadowRay) теперь пропускает чанки!
            if (TraceRefractionRay(refrOrigin, rayDir, 40.0, tBot, matBot)) {
                vec3 botPos = refrOrigin + rayDir * tBot; vec3 botAlbedo = GetColor(matBot);
                float caustics = GetCaustics(botPos, uTime); float causticFade = exp(-tBot * 0.3);
                vec3 causticLight = sunLightColor * caustics * causticFade * 1.5;
                float botDiff = max(dot(vec3(0,1,0), safeSunDir), 0.0) * shadowFactor + 0.2;
                vec3 botFinal = botAlbedo * (sunLightColor * botDiff + causticLight);
                vec3 absorb = vec3(WATER_ABSORPTION_R_UNDERWATER, WATER_ABSORPTION_G_UNDERWATER, WATER_ABSORPTION_B_UNDERWATER) * 2.5;
                vec3 transmission = exp(-absorb * tBot);
                refractionColor = botFinal * transmission + vec3(WATER_SCATTERING_UNDERWATER) * (1.0 - transmission);
            }
            #endif
            vec3 halfVec = normalize(safeSunDir - viewDir);
            float specular = pow(max(dot(waterNormal, halfVec), 0.0), 200.0) * shadowFactor;
            finalColor = mix(refractionColor, reflectionColor, fresnel) + specular * sunLightColor;
        }

        finalColor = ApplyFog(finalColor, rayDir, safeSunDir, tEnd, uRenderDistance);
        vec4 clipPos = uProjection * uView * vec4(hitPos, 1.0);
        finalDepth = (clipPos.z / clipPos.w) * 0.5 + 0.5;
    }

    finalColor = ApplyPostProcess(finalColor);
    FragColor = vec4(finalColor, 1.0);
    gl_FragDepth = finalDepth;
}