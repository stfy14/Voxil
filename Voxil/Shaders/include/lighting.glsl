// --- START OF FILE include/lighting.glsl ---

#define AO_STRENGTH 0.6
#define SOFT_SHADOW_RADIUS 0.01
#define SHADOW_BIAS 0.003

bool IsSolidVoxelSpace(ivec3 pos) {
    // 1. Проверка границ мира
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * VOXEL_RESOLUTION;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * VOXEL_RESOLUTION;
    if (any(lessThan(pos, boundMin)) || any(greaterThanEqual(pos, boundMax))) return false;

    // 2. Проверка Page Table (есть ли чанк)
    ivec3 chunkCoord = pos >> BIT_SHIFT;
    uint chunkSlot = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
    if (chunkSlot == 0xFFFFFFFFu) return false;

    // 3. ОПТИМИЗАЦИЯ: Проверка Bitmask (есть ли блок 4x4)
    // Этого не было. Мы избегаем чтения GetVoxelData для пустого воздуха.

    // Вычисляем локальные координаты
    ivec3 local = pos & BIT_MASK; // 0..31
    ivec3 bMapPos = local / BLOCK_SIZE; // 0..7
    int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);

    // Читаем маску
    uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
    uvec2 maskVal = packedMasks[maskBaseOffset + blockIdx];

    // Если блок 4x4 полностью пуст - выходим сразу
    if (maskVal.x == 0u && maskVal.y == 0u) return false;

    // Проверяем конкретный бит
    int lx = local.x % BLOCK_SIZE;
    int ly = local.y % BLOCK_SIZE;
    int lz = local.z % BLOCK_SIZE;
    int bitIdx = lx + BLOCK_SIZE * (ly + BLOCK_SIZE * lz);

    bool hasVoxel = (bitIdx < 32) ? ((maskVal.x & (1u << bitIdx)) != 0u) : ((maskVal.y & (1u << (bitIdx - 32))) != 0u);

    if (!hasVoxel) return false;

    // 4. Только если бит установлен, читаем тип материала (чтобы игнорировать воду)
    int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
    uint matID = GetVoxelData(chunkSlot, idx);

    // Игнорируем воду (ID 4) для AO, чтобы под водой не было черноты на дне,
    // и чтобы сама вода не затеняла берег.
    return (matID != 0u && matID != 4u);
}

float GetCornerOcclusion(ivec3 pos, ivec3 side1, ivec3 side2) {
    bool s1 = IsSolidVoxelSpace(pos + side1);
    bool s2 = IsSolidVoxelSpace(pos + side2);
    bool c = IsSolidVoxelSpace(pos + side1 + side2);
    if (s1 && s2) return 3.0;
    return float(s1) + float(s2) + float(c);
}

float CalculateAO(vec3 hitPos, vec3 normal) {
    vec3 aoRayOrigin = hitPos + normal * 0.005;
    vec3 voxelPos = aoRayOrigin * VOXELS_PER_METER;
    ivec3 ipos = ivec3(floor(voxelPos));
    ivec3 n = ivec3(normal);
    vec3 localPos = fract(voxelPos);
    ivec3 t, b;
    vec2 uvSurf;
    if (abs(n.y) > 0.5) { t = ivec3(1, 0, 0); b = ivec3(0, 0, 1); uvSurf = localPos.xz; }
    else if (abs(n.x) > 0.5) { t = ivec3(0, 0, 1); b = ivec3(0, 1, 0); uvSurf = localPos.zy; }
    else { t = ivec3(1, 0, 0); b = ivec3(0, 1, 0); uvSurf = localPos.xy; }
    float occ00 = GetCornerOcclusion(ipos, -t, -b);
    float occ10 = GetCornerOcclusion(ipos,  t, -b);
    float occ01 = GetCornerOcclusion(ipos, -t,  b);
    float occ11 = GetCornerOcclusion(ipos,  t,  b);
    vec2 smoothUV = uvSurf * uvSurf * (3.0 - 2.0 * uvSurf);
    float finalOcc = mix(mix(occ00, occ10, smoothUV.x), mix(occ01, occ11, smoothUV.x), smoothUV.y);
    float ao = clamp(pow(0.5, finalOcc) + (IGN(gl_FragCoord.xy) - 0.5) * 0.1, 0.0, 1.0);
    return mix(1.0, ao, AO_STRENGTH);
}

float CalculateSoftShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    float distToCam = length(hitPos - uCamPos);
    vec3 shadowOrigin = hitPos + normal * SHADOW_BIAS;
    int maxSamples = uSoftShadowSamples;
    int effectiveSamples = maxSamples;
    float currentRadius = SOFT_SHADOW_RADIUS;
    if (distToCam > 80.0) { effectiveSamples = 2; currentRadius *= 0.5; }
    else if (distToCam > 40.0) { effectiveSamples = max(4, maxSamples / 4); currentRadius *= 1.0; }
    else if (distToCam > 15.0) { effectiveSamples = max(8, maxSamples / 2); currentRadius *= 1.0; }
    if (effectiveSamples < 1) effectiveSamples = 1;
    vec3 up = abs(sunDir.y) < 0.999 ? vec3(0, 1, 0) : vec3(0, 0, 1);
    vec3 right = normalize(cross(sunDir, up));
    up = cross(right, sunDir);
    float totalVisibility = 0.0;
    float noiseVal = IGN(gl_FragCoord.xy);
    float maxShadowDist = 200.0;
    for (int k = 0; k < effectiveSamples; k++) {
        float angle = (float(k) + noiseVal) * 2.39996;
        float r = sqrt(float(k) + 0.5) / sqrt(float(effectiveSamples));
        vec2 offset = vec2(cos(angle), sin(angle)) * r * currentRadius;
        vec3 offsetDir = right * offset.x + up * offset.y;
        vec3 shadowDir = normalize(sunDir + offsetDir);
        bool hit = false;
        if (distToCam < 150.0) {
            float tDyn = 100000.0; int idDyn = -1; vec3 nDyn = vec3(0);
            int dummy = 0;
            if (uObjectCount > 0 && TraceDynamicRay(shadowOrigin, shadowDir, maxShadowDist, tDyn, idDyn, nDyn, dummy)) hit = true;
        }
        if (!hit) {
            float tHit = 0.0; uint matID = 0u;
            if (TraceShadowRay(shadowOrigin, shadowDir, maxShadowDist, tHit, matID)) hit = true;
        }
        if (!hit) totalVisibility += 1.0;
    }
    return smoothstep(0.0, 1.0, totalVisibility / float(effectiveSamples));
}

float CalculateHardShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    vec3 shadowOrigin = hitPos + normal * SHADOW_BIAS;
    float tHit = 0.0; uint matID = 0u;
    if (TraceShadowRay(shadowOrigin, sunDir, 200.0, tHit, matID)) return 0.0;
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