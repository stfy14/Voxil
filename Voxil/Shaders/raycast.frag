#version 450 core
out vec4 FragColor;
in vec2 uv;

// 1. Подключаем модули
#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"
#include "include/water_impl.glsl"

void main() {
    vec3 safeSunDir = normalize(length(uSunDir) < 0.01 ? vec3(0.0, 0.1, 0.8) : uSunDir);

    // --- 2. Генерация луча ---
    vec2 pos = uv * 2.0 - 1.0;
    vec4 target = inverse(uProjection) * vec4(pos, 1.0, 1.0);
    vec3 rayDir = normalize((inverse(uView) * vec4(target.xyz, 0.0)).xyz);

    // ИСПРАВЛЕНИЕ: Фикс артефактов + ПОВТОРНАЯ НОРМАЛИЗАЦИЯ
    if (abs(rayDir.x) < 1e-6) rayDir.x = 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = 1e-6;
    rayDir = normalize(rayDir); // <--- Важно! Иначе солнце пропадет или будет искажено

    // --- 3. Рендер неба (Base) ---
    vec3 finalColor = GetSkyColor(rayDir, safeSunDir);
    float finalDepth = 0.999999;

    // --- 4. Трассировка ---
    float tDynamicHit = 100000.0;
    int bestObjID = -1;
    vec3 bestLocalNormal = vec3(0);

    // 4.1 Динамика
    if (uObjectCount > 0) {
        vec3 gridSpaceRo = (uCamPos - uGridOrigin) / uGridStep;
        vec3 invDir = 1.0 / rayDir;
        vec3 t0 = -gridSpaceRo * invDir, t1 = (vec3(uGridSize) - gridSpaceRo) * invDir;
        vec3 tmin = min(t0, t1), tmax = max(t0, t1);
        float tEnter = max(max(tmin.x, tmin.y), tmin.z), tExit = min(min(tmax.x, tmax.y), tmax.z);
        if (tExit >= tEnter && tExit > 0.0) {
            vec3 currPos = gridSpaceRo + rayDir * (max(0.0, tEnter) + 0.001);
            ivec3 mapPos = ivec3(floor(currPos)), stepDir = ivec3(sign(rayDir));
            vec3 deltaDist = abs(1.0 / rayDir);
            vec3 sideDist = (sign(rayDir) * (vec3(mapPos) - currPos) + (0.5 + 0.5 * sign(rayDir))) * deltaDist;
            vec3 mask;
            for (int i = 0; i < 128; i++) {
                if (any(lessThan(mapPos, ivec3(0))) || any(greaterThanEqual(mapPos, ivec3(uGridSize)))) break;
                int val = imageLoad(uObjectGrid, mapPos).r;
                if (val > 0) {
                    uint dynMat; vec3 localN;
                    float tHit = CheckDynamicHitByID(val - 1, uCamPos, rayDir, dynMat, localN);
                    if (tHit >= 0.0 && tHit < tDynamicHit) {
                        tDynamicHit = tHit; bestObjID = val - 1; bestLocalNormal = localN;
                    }
                }
                mask = (sideDist.x < sideDist.y) ?
                ((sideDist.x < sideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
                ((sideDist.y < sideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                if ((dot(mask, sideDist - deltaDist) + max(0.0, tEnter)) * uGridStep > tDynamicHit) break;
                sideDist += mask * deltaDist; mapPos += ivec3(mask) * stepDir;
            }
        }
    }

    // 4.2 Статика
    float tStaticHit = 100000.0; // Значение по умолчанию, если не попадем
    uint staticHitMat = 0u;
    vec3 finalNormal = vec3(0);

    // Теперь TraceStaticRay принимает inout и не сломает tStaticHit, если вернет false
    TraceStaticRay(uCamPos, rayDir, uRenderDistance, tStaticHit, staticHitMat, finalNormal);

    float tEnd = min(tStaticHit, tDynamicHit);

    // --- 5. Расчет освещения поверхности ---
    if (tEnd < 99000.0) {
        bool isDynamic = tDynamicHit < tStaticHit;
        vec3 hitPos = uCamPos + rayDir * tEnd, normal, albedo;
        float ao = 1.0;

        if (isDynamic) {
            normal = normalize(transpose(inverse(mat3(dynObjects[bestObjID].model))) * bestLocalNormal);
            albedo = dynObjects[bestObjID].color.rgb;
            ao = 0.8;
        } else {
            normal = finalNormal;
            albedo = GetColor(staticHitMat);
            ao = CalculateAO(hitPos + normal * 0.001, normal);
        }

        // Тени
        float ndotl = max(dot(normal, safeSunDir), 0.0);
        float shadowFactor = 0.0;
        if (ndotl > 0.0) {
            shadowFactor = CalculateSoftShadow(hitPos, normal, safeSunDir);
        }

        vec3 sunLightColor = vec3(1.0, 0.98, 0.95);
        vec3 direct = albedo * sunLightColor * 1.2 * ndotl * shadowFactor;
        vec3 ambient = albedo * mix(vec3(0.15, 0.15, 0.2), vec3(0.3, 0.5, 0.8), 0.5 + 0.5 * normal.y) * 0.7 * ao;
        finalColor = direct + ambient;

        // --- 6. Вода ---
        if (!isDynamic && staticHitMat == 4u) {
            vec3 viewDir = -rayDir;
            vec3 tangentViewDir = vec3(viewDir.x, viewDir.z, viewDir.y);
            bool flowing = false;
            vec2 flowDir = vec2(1.0, 0.0);

            vec2 originalUV = hitPos.xz;
            vec2 parallaxUV = get_water_parallax_coord(tangentViewDir, originalUV, flowDir, flowing);
            vec3 waterNormalTangent = get_water_normal_photon(hitPos, finalNormal, parallaxUV, flowDir, 1.0, flowing);
            vec3 waterNormal = normalize(vec3(waterNormalTangent.x, waterNormalTangent.z, waterNormalTangent.y));

            float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(waterNormal, -rayDir), 0.0), 5.0);

            vec3 refDir = reflect(rayDir, waterNormal);
            if (refDir.y < 0.05) { refDir.y = 0.05; refDir = normalize(refDir); }

            vec3 reflectionColor;
            float tRef = 100000.0; uint matRef = 0u; vec3 refNorm = vec3(0);

            if (TraceStaticRay(hitPos + normal * 0.01, refDir, 200.0, tRef, matRef, refNorm)) {
                vec3 refAlbedo = GetColor(matRef);
                vec3 totalLight = sunLightColor * 1.5 * max(dot(refNorm, safeSunDir), 0.0) +
                vec3(0.2, 0.4, 0.6) * 0.8 * max(dot(refNorm, -safeSunDir), 0.0) +
                mix(vec3(0.4, 0.25, 0.2), vec3(0.6, 0.55, 0.6), refNorm.y * 0.5 + 0.5);

                reflectionColor = refAlbedo * totalLight;
                float absorption = 1.0 - exp(-tRef * 0.015);
                reflectionColor = mix(reflectionColor, vec3(0.1, 0.15, 0.2), absorption);

                vec3 horizonColor = vec3(0.60, 0.75, 0.95);
                vec3 skyTopColor = vec3(0.3, 0.5, 0.85);
                reflectionColor = mix(reflectionColor, mix(horizonColor, skyTopColor, max(refDir.y, 0.0)),
                                      pow(clamp(tRef / 200.0, 0.0, 1.0), 4.0));
            } else {
                reflectionColor = GetSkyColor(refDir, safeSunDir);
            }

            vec3 refractionColor = vec3(0.0);
            vec3 underwaterRo = hitPos + rayDir * 0.01;
            vec3 underwaterRd = rayDir;

            ivec3 uMapPos = ivec3(floor(underwaterRo));
            ivec3 uStepDir = ivec3(sign(underwaterRd));
            vec3 uDeltaDist = abs(1.0 / underwaterRd);
            vec3 uSideDist = (sign(underwaterRd) * (vec3(uMapPos) - underwaterRo) + (0.5 + 0.5 * sign(underwaterRd))) * uDeltaDist;
            vec3 uMask;
            float tWaterDepth = 0.0;
            bool hitBottom = false;
            uint bottomMat = 0u;

            for (int k = 0; k < 64; k++) {
                uMask = (uSideDist.x < uSideDist.y) ?
                ((uSideDist.x < uSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
                ((uSideDist.y < uSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                uSideDist += uMask * uDeltaDist;
                uMapPos += ivec3(uMask) * uStepDir;
                ivec3 chunkCoord = uMapPos >> 4;
                int chunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
                if (chunkIdx != -1) {
                    ivec3 local = uMapPos & 15;
                    int idx = local.x + 16 * (local.y + 16 * local.z);
                    uint m = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;
                    if (m != 0u && m != 4u) {
                        tWaterDepth = dot(uMask, uSideDist - uDeltaDist);
                        bottomMat = m;
                        hitBottom = true;
                        break;
                    }
                }
                if (dot(uMask, uSideDist - uDeltaDist) > 80.0) break;
            }

            vec3 waterAbsorb = vec3(WATER_ABSORPTION_R_UNDERWATER, WATER_ABSORPTION_G_UNDERWATER, WATER_ABSORPTION_B_UNDERWATER);
            vec3 extinction = waterAbsorb * 2.0;

            if (hitBottom) {
                vec3 bottomPos = underwaterRo + underwaterRd * tWaterDepth;
                vec3 bottomAlbedo = GetColor(bottomMat);

                float caustics = GetCaustics(bottomPos, uTime);
                float causticFade = exp(-tWaterDepth * 0.3);
                vec3 causticLight = sunLightColor * caustics * causticFade * 0.8;
                float bottomLightBase = max(dot(vec3(0,1,0), safeSunDir), 0.0) * shadowFactor * 0.8 + 0.2;
                vec3 bottomFinalColor = bottomAlbedo * (sunLightColor * bottomLightBase + causticLight);

                vec3 transmission = exp(-extinction * tWaterDepth);
                refractionColor = bottomFinalColor * transmission;
                refractionColor += vec3(WATER_SCATTERING_UNDERWATER) * (1.0 - transmission) * sunLightColor * 0.5;
            } else {
                refractionColor = vec3(0.05, 0.1, 0.2);
            }

            vec3 halfVec = normalize(safeSunDir - rayDir);
            float specular = pow(max(dot(waterNormal, halfVec), 0.0), 200.0) * shadowFactor;

            finalColor = mix(refractionColor, reflectionColor, fresnel) + specular * sunLightColor;
        }

        // --- 7. Туман ---
        finalColor = ApplyFog(finalColor, rayDir, safeSunDir, tEnd, uRenderDistance);

        vec4 clipPos = uProjection * uView * vec4(hitPos, 1.0);
        finalDepth = (clipPos.z / clipPos.w) * 0.5 + 0.5;
    }

    // --- 8. Пост-процессинг ---
    finalColor = ApplyPostProcess(finalColor);

    FragColor = vec4(finalColor, 1.0);
    gl_FragDepth = finalDepth;
}