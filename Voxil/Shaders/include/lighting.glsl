// --- START OF FILE include/lighting.glsl ---

#define AO_STRENGTH 0.6
#define SOFT_SHADOW_RADIUS 0.01
#define SHADOW_BIAS 0.003

bool IsSolidForAO(ivec3 pos) {
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * VOXEL_RESOLUTION;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * VOXEL_RESOLUTION;
    if (any(lessThan(pos, boundMin)) || any(greaterThanEqual(pos, boundMax))) return false;

    ivec3 chunkCoord = pos >> BIT_SHIFT;
    uint chunkSlot = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
    if (chunkSlot == 0xFFFFFFFFu) return false;

    // === SOLID CHUNK CHECK ===
    if ((chunkSlot & 0x80000000u) != 0u) {
        return true;
    }
    // =========================

    ivec3 local = pos & BIT_MASK;
    ivec3 bMapPos = local / BLOCK_SIZE;
    int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);
    uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
    uvec2 maskVal = packedMasks[maskBaseOffset + blockIdx];

    if (maskVal.x == 0u && maskVal.y == 0u) return false;

    int lx = local.x % BLOCK_SIZE; int ly = local.y % BLOCK_SIZE; int lz = local.z % BLOCK_SIZE;
    int bitIdx = lx + BLOCK_SIZE * (ly + BLOCK_SIZE * lz);
    bool hasVoxel = (bitIdx < 32) ? ((maskVal.x & (1u << bitIdx)) != 0u) : ((maskVal.y & (1u << (bitIdx - 32))) != 0u);
    if (!hasVoxel) return false;

    int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
    uint matID = GetVoxelData(chunkSlot, idx);
    return (matID != 0u && matID != 4u);
}

bool IsSolidVoxelSpace(ivec3 pos) { return IsSolidForAO(pos); }

float GetCornerOcclusionScaled(ivec3 basePos, ivec3 side1, ivec3 side2, int stride) {
    bool s1 = IsSolidForAO(basePos + side1 * stride);
    bool s2 = IsSolidForAO(basePos + side2 * stride);
    bool c = IsSolidForAO(basePos + (side1 + side2) * stride);
    if (s1 && s2) return 3.0;
    return float(s1) + float(s2) + float(c);
}

float CalculateAO(vec3 hitPos, vec3 normal) {

    // === СИНХРОНИЗАЦИЯ AO С ЧАНКАМИ ===
    // Вместо дистанции до пикселя, считаем дистанцию до ЦЕНТРА ЧАНКА, в котором пиксель.
    // Это заставит AO переключаться ровно по границам чанков, как и геометрия.

    vec3 chunkCoordFloat = floor(hitPos / float(CHUNK_SIZE));
    vec3 chunkCenter = (chunkCoordFloat + 0.5) * float(CHUNK_SIZE);
    float distChunkToCam = distance(uCamPos, chunkCenter);

    #ifdef ENABLE_LOD
        bool useLodAO = (distChunkToCam > uLodDistance);
    if (useLodAO && uDisableEffectsOnLOD == 1) return 1.0;
    #else
        bool useLodAO = false;
    #endif
    // ===================================

    int stepDist = useLodAO ? BLOCK_SIZE : 1;

    vec3 aoRayOrigin = hitPos + normal * 0.01 * float(stepDist);
    vec3 voxelPos = aoRayOrigin * VOXELS_PER_METER;

    ivec3 ipos = ivec3(floor(voxelPos));
    ivec3 n = ivec3(normal);

    // На LOD выравниваем позицию внутри блока, чтобы убрать шум
    vec3 localPos = useLodAO ? fract(voxelPos / float(BLOCK_SIZE)) : fract(voxelPos);

    ivec3 t, b;
    vec2 uvSurf;
    if (abs(n.y) > 0.5) { t = ivec3(1, 0, 0); b = ivec3(0, 0, 1); uvSurf = localPos.xz; }
    else if (abs(n.x) > 0.5) { t = ivec3(0, 0, 1); b = ivec3(0, 1, 0); uvSurf = localPos.zy; }
    else { t = ivec3(1, 0, 0); b = ivec3(0, 1, 0); uvSurf = localPos.xy; }

    float occ00 = GetCornerOcclusionScaled(ipos, -t, -b, stepDist);
    float occ10 = GetCornerOcclusionScaled(ipos,  t, -b, stepDist);
    float occ01 = GetCornerOcclusionScaled(ipos, -t,  b, stepDist);
    float occ11 = GetCornerOcclusionScaled(ipos,  t,  b, stepDist);

    vec2 smoothUV = uvSurf * uvSurf * (3.0 - 2.0 * uvSurf);
    float finalOcc = mix(mix(occ00, occ10, smoothUV.x), mix(occ01, occ11, smoothUV.x), smoothUV.y);

    float ao = pow(0.5, finalOcc);
    if (!useLodAO) ao += (IGN(gl_FragCoord.xy) - 0.5) * 0.1;

    return clamp(mix(1.0, ao, AO_STRENGTH), 0.0, 1.0);
}

// Функции теней оставляем старые, они используют TraceShadowRay из tracing.glsl,
// который уже настроен на работу "по чанкам" (он использует cMapPos для определения LOD).

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
    #ifdef ENABLE_LOD
        // Для теней тоже используем "чанковую" дистанцию, чтобы они не отключались кусками посреди чанка
    vec3 chunkCoordFloat = floor(hitPos / float(CHUNK_SIZE));
    vec3 chunkCenter = (chunkCoordFloat + 0.5) * float(CHUNK_SIZE);
    float distChunkToCam = distance(uCamPos, chunkCenter);

    if (distChunkToCam > uLodDistance && uDisableEffectsOnLOD == 1) return 1.0;
    #endif

    #ifdef SHADOW_MODE_HARD
        return CalculateHardShadow(hitPos, normal, sunDir);
    #elif defined(SHADOW_MODE_SOFT)
        return CalculateSoftShadow(hitPos, normal, sunDir);
    #else
        return 1.0;
    #endif
}