// --- START OF FILE include/lighting.glsl ---

// =============================================================================
//                              LIGHTING SETTINGS
// =============================================================================
#define SOFT_SHADOW_RADIUS 0.08  // Чуть увеличил радиус для большей мягкости
#define SHADOW_BIAS 0.002        // Чуть увеличил смещение, чтобы убрать шум на гранях
#define AO_STRENGTH 0.8
#define AO_CULL_DISTANCE 64.0
// =============================================================================

// Хелпер проверки вокселя
bool IsSolidVoxelSpace(ivec3 pos) {
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * VOXEL_RESOLUTION;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * VOXEL_RESOLUTION;
    if (any(lessThan(pos, boundMin)) || any(greaterThanEqual(pos, boundMax))) return false;
    ivec3 chunkCoord = pos >> BIT_SHIFT;

    uint chunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
    if (chunkIdx == 0xFFFFFFFFu) return false;

    ivec3 local = pos & BIT_MASK;
    int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
    return ((packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu) != 0u;
}

float GetCornerOcclusion(ivec3 pos, ivec3 side1, ivec3 side2) {
    bool s1 = IsSolidVoxelSpace(pos + side1);
    bool s2 = IsSolidVoxelSpace(pos + side2);
    bool c = IsSolidVoxelSpace(pos + side1 + side2);
    if (s1 && s2) return 3.0;
    return float(s1) + float(s2) + float(c);
}

float CalculateAO(vec3 hitPos, vec3 normal) {
    float dist = length(hitPos - uCamPos);
    if (dist > AO_CULL_DISTANCE) return 1.0;

    vec3 aoRayOrigin = hitPos + normal * 0.001;
    vec3 voxelPos = aoRayOrigin * VOXELS_PER_METER;
    ivec3 ipos = ivec3(floor(voxelPos));

    ivec3 n = ivec3(normal);
    vec3 localPos = fract(voxelPos);

    ivec3 t, b; vec2 uvSurf;
    if (abs(n.y) > 0.5) { t = ivec3(1, 0, 0); b = ivec3(0, 0, 1); uvSurf = localPos.xz; }
    else if (abs(n.x) > 0.5) { t = ivec3(0, 0, 1); b = ivec3(0, 1, 0); uvSurf = localPos.zy; }
    else { t = ivec3(1, 0, 0); b = ivec3(0, 1, 0); uvSurf = localPos.xy; }

    float occ00 = GetCornerOcclusion(ipos, -t, -b), occ10 = GetCornerOcclusion(ipos, t, -b);
    float occ01 = GetCornerOcclusion(ipos, -t, b), occ11 = GetCornerOcclusion(ipos, t, b);
    vec2 smoothUV = uvSurf * uvSurf * (3.0 - 2.0 * uvSurf);
    float finalOcc = mix(mix(occ00, occ10, smoothUV.x), mix(occ01, occ11, smoothUV.x), smoothUV.y);

    float ao = clamp(pow(AO_STRENGTH, finalOcc) + (IGN(gl_FragCoord.xy) - 0.5) / float(VOXEL_RESOLUTION), 0.0, 1.0);
    return mix(ao, 1.0, smoothstep(AO_CULL_DISTANCE * 0.8, AO_CULL_DISTANCE, dist));
}

// ----------------------------------------------------------------------------
// Shadow Helpers
// ----------------------------------------------------------------------------

float CalculateSoftShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    float distToCam = length(hitPos - uCamPos);
    vec3 shadowOrigin = hitPos + normal * SHADOW_BIAS;

    // --- GRADIENT LOD SYSTEM ---
    // Плавно снижаем кол-во сэмплов с расстоянием, но НЕ переключаемся на Hard Shadow (1 луч),
    // чтобы сохранить "мягкое" ощущение.
    int maxSamples = uSoftShadowSamples;
    int effectiveSamples = maxSamples;
    float currentRadius = SOFT_SHADOW_RADIUS;

    if (distToCam > 80.0) {
        effectiveSamples = max(2, maxSamples / 8); // Очень далеко: 2-4 луча (легкий шум, но мягко)
        currentRadius *= 3.0; // Тени более размытые вдали
    } else if (distToCam > 40.0) {
        effectiveSamples = max(4, maxSamples / 4); // Далеко: 4-8 лучей
        currentRadius *= 2.0;
    } else if (distToCam > 15.0) {
        effectiveSamples = max(8, maxSamples / 2); // Средне: 8-16 лучей
        currentRadius *= 1.5;
    }
    // Ближе 15м = Полное качество (32 луча)

    // Если пользователь поставил очень низкие настройки, не падаем ниже 1 сэмпла
    if (effectiveSamples < 1) effectiveSamples = 1;

    // Basis construction
    vec3 up = abs(sunDir.y) < 0.999 ? vec3(0, 1, 0) : vec3(0, 0, 1);
    vec3 right = normalize(cross(sunDir, up));
    up = cross(right, sunDir);

    float totalVisibility = 0.0;
    float noiseVal = random(gl_FragCoord.xy);

    // Ограничиваем дальность лучей тени (нет смысла искать тень за 100м от точки)
    float maxShadowDist = 60.0;

    for (int k = 0; k < effectiveSamples; k++) {
        float angle = (float(k) + noiseVal) * 2.39996;
        float r = sqrt(float(k) + 0.5) / sqrt(float(effectiveSamples));
        vec2 offset = vec2(cos(angle), sin(angle)) * r * currentRadius;
        vec3 offsetDir = right * offset.x + up * offset.y;
        vec3 shadowDir = normalize(sunDir + offsetDir);

        bool hit = false;
        float hitDistMeters = 0.0;

        // 1. Dynamic Check
        float tDyn = 100000.0; int idDyn = -1; vec3 nDyn = vec3(0);
        if (uObjectCount > 0 && TraceDynamicRay(shadowOrigin, shadowDir, maxShadowDist, tDyn, idDyn, nDyn)) {
            hit = true;
            hitDistMeters = tDyn;
        }

        // 2. Static Check (Optimized)
        if (!hit) {
            float tStatic = 100000.0;
            uint matID = 0u;
            if (TraceShadowRay(shadowOrigin, shadowDir, maxShadowDist, tStatic, matID)) {
                hit = true;
                hitDistMeters = tStatic;
            }
        }

        if (!hit) {
            totalVisibility += 1.0;
        } else {
            // --- ИСПРАВЛЕНИЕ АРТЕФАКТА "90 ГРАДУСОВ" ---
            // Было: smoothstep(2.0, 25.0, ...) -> Все что ближе 2м было черным.
            // Стало: smoothstep(0.1, 10.0, ...) -> Мягкость начинается с 10см.
            // Это позволит теням от соседних ступенек слегка размываться.
            totalVisibility += smoothstep(0.1, 10.0, hitDistMeters);
        }
    }

    return smoothstep(0.0, 1.0, totalVisibility / float(effectiveSamples));
}

float CalculateHardShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    vec3 shadowOrigin = hitPos + normal * SHADOW_BIAS;
    float tHit = 0.0; uint matID = 0u;
    if (TraceShadowRay(shadowOrigin, sunDir, 100.0, tHit, matID)) return 0.0;
    return 1.0;
}

float CalculateShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    #ifdef SHADOW_MODE_HARD
        return CalculateHardShadow(hitPos, normal, sunDir);
    #elif defined(SHADOW_MODE_SOFT)
        return CalculateSoftShadow(hitPos, normal, sunDir);
    #else
        return 1.0;
    #endif
}