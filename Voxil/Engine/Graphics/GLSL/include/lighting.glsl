#define AO_STRENGTH 0.6
#define BLOCKER_SAMPLES 6
#define SOFT_SHADOW_RADIUS 0.08

bool IsSolidForAO(ivec3 pos) {
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * VOXEL_RESOLUTION;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * VOXEL_RESOLUTION;
    if (any(lessThan(pos, boundMin)) || any(greaterThanEqual(pos, boundMax))) return false;

    ivec3 chunkCoord = pos >> BIT_SHIFT;
    uint chunkSlot = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
    if (chunkSlot == 0xFFFFFFFFu) return false;

    if ((chunkSlot & 0x80000000u) != 0u) return true;

    #ifdef ENABLE_LOD
    vec3 chunkCenter = (vec3(chunkCoord) + 0.5) * float(CHUNK_SIZE);
    if (distance(uCamPos, chunkCenter) > uLodDistance) {
        ivec3 local = pos & BIT_MASK;
        ivec3 bMapPos = local / BLOCK_SIZE;
        int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);
        uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
        uvec2 maskVal = packedMasks[maskBaseOffset + blockIdx];
        return (maskVal.x != 0u || maskVal.y != 0u);
    }
    #endif

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

float GetCornerOcclusionScaled(ivec3 basePos, ivec3 side1, ivec3 side2, int stride) {
    bool s1 = IsSolidForAO(basePos + side1 * stride);
    bool s2 = IsSolidForAO(basePos + side2 * stride);
    bool c = IsSolidForAO(basePos + (side1 + side2) * stride);
    if (s1 && s2) return 3.0;
    return float(s1) + float(s2) + float(c);
}

float CalculateAO(vec3 hitPos, vec3 normal) {
    vec3 chunkCoordFloat = floor(hitPos / float(CHUNK_SIZE));
    vec3 chunkCenter = (chunkCoordFloat + 0.5) * float(CHUNK_SIZE);
    float distChunkToCam = distance(uCamPos, chunkCenter);

    #ifdef ENABLE_LOD
    bool useLodAO = (distChunkToCam > uLodDistance);
    if (useLodAO && uDisableEffectsOnLOD == 1) return 1.0;
    #else
    bool useLodAO = false;
    #endif

    int stepDist = useLodAO ? BLOCK_SIZE : 1;
    float distToCam = length(hitPos - uCamPos);
    float aoBias = 0.015 + distToCam * 0.001;

    vec3 aoRayOrigin = hitPos + normal * aoBias * float(stepDist);
    vec3 voxelPos = aoRayOrigin * VOXELS_PER_METER;

    ivec3 ipos = ivec3(floor(voxelPos));
    ivec3 n = ivec3(normal);
    vec3 localPos = useLodAO ? fract(voxelPos / float(BLOCK_SIZE)) : fract(voxelPos);

    ivec3 t, b; vec2 uvSurf;
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
    return clamp(mix(1.0, ao, AO_STRENGTH), 0.0, 1.0);
}

// =============================================================================
// УНИВЕРСАЛЬНЫЙ ТРЕЙСИНГ ТЕНЕЙ (Игнорирует светящиеся блоки)
// =============================================================================

bool TraceAnyShadow(vec3 origin, vec3 dir, float maxDist, out float hitDist) {
    float tStatic = 0.0; uint matStatic = 0u;
    bool hitStatic = false;

    if (TraceShadowRay(origin, dir, maxDist, tStatic, matStatic)) {
        if (matStatic != 7u && tStatic < maxDist) {
            hitDist = tStatic;
            hitStatic = true;
        }
    }

    float tDyn = 100000.0; int idDyn = -1; vec3 nDyn = vec3(0); uint matDyn = 0u; int steps = 0;
    bool hitDyn = false;

    if (uObjectCount > 0) {
        if (TraceDynamicRay(origin, dir, maxDist, tDyn, idDyn, nDyn, matDyn, steps)) {
            if (matDyn != 7u && tDyn < maxDist) {
                hitDyn = true;
            }
        }
    }

    if (hitStatic && hitDyn) { hitDist = min(tStatic, tDyn); return true; }
    if (hitStatic) { hitDist = tStatic; return true; }
    if (hitDyn) { hitDist = tDyn; return true; }

    return false;
}

// =============================================================================
// ТЕНИ ОТ СОЛНЦА И ЛУНЫ
// =============================================================================

float CalculateSoftShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    float distToCam = length(hitPos - uCamPos);
    float bias = 0.015 + distToCam * 0.0015;
    vec3 shadowOrigin = hitPos + normal * bias;
    float maxShadowDist = 200.0;

    vec3 up    = abs(sunDir.y) < 0.999 ? vec3(0,1,0) : vec3(0,0,1);
    vec3 right = normalize(cross(sunDir, up));
    up         = cross(right, sunDir);
    float noiseVal = IGN(gl_FragCoord.xy);

    float avgBlockerDist = 0.0;
    int   blockerCount   = 0;
    for (int k = 0; k < BLOCKER_SAMPLES; k++) {
        float angle = (float(k) + noiseVal) * 2.39996;
        float r     = sqrt(float(k) + 0.5) / sqrt(float(BLOCKER_SAMPLES));
        vec2  off   = vec2(cos(angle), sin(angle)) * r * SOFT_SHADOW_RADIUS;
        vec3  dir   = normalize(sunDir + right * off.x + up * off.y);

        float hd = 0.0;
        if (TraceAnyShadow(shadowOrigin, dir, maxShadowDist, hd)) {
            avgBlockerDist += hd;
            blockerCount++;
        }
    }
    if (blockerCount == 0) return 1.0;
    avgBlockerDist /= float(blockerCount);

    float penumbra = clamp(avgBlockerDist / maxShadowDist, 0.0, 1.0);
    float shadowRadius = penumbra * SOFT_SHADOW_RADIUS;
    if (shadowRadius < 0.0005) return 0.0;

    int   effectiveSamples = uSoftShadowSamples;
    float currentRadius    = shadowRadius;
    if (distToCam > 80.0)      { effectiveSamples = 2;                              currentRadius *= 0.5; }
    else if (distToCam > 40.0) { effectiveSamples = max(4, uSoftShadowSamples / 4); }
    else if (distToCam > 15.0) { effectiveSamples = max(8, uSoftShadowSamples / 2); }
    if (effectiveSamples < 1)    effectiveSamples = 1;

    float totalVisibility = 0.0;
    for (int k = 0; k < effectiveSamples; k++) {
        float angle     = (float(k) + noiseVal) * 2.39996;
        float r         = sqrt(float(k) + 0.5) / sqrt(float(effectiveSamples));
        vec2  off       = vec2(cos(angle), sin(angle)) * r * currentRadius;
        vec3  shadowDir = normalize(sunDir + right * off.x + up * off.y);

        float hd = 0.0;
        if (!TraceAnyShadow(shadowOrigin, shadowDir, maxShadowDist, hd)) {
            totalVisibility += 1.0;
        }
    }
    return smoothstep(0.0, 1.0, totalVisibility / float(effectiveSamples));
}

float CalculateHardShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    float distToCam = length(hitPos - uCamPos);
    float bias = 0.015 + distToCam * 0.0015;
    vec3 shadowOrigin = hitPos + normal * bias;
    float hd = 0.0;
    if (TraceAnyShadow(shadowOrigin, sunDir, 200.0, hd)) return 0.0;
    return 1.0;
}

float CalculateShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    #ifdef ENABLE_LOD
    vec3 chunkCoordFloat = floor(hitPos / float(CHUNK_SIZE));
    vec3 chunkCenter = (chunkCoordFloat + 0.5) * float(CHUNK_SIZE);
    float distChunkToCam = distance(uCamPos, chunkCenter);

    if (distChunkToCam > uLodDistance && uDisableEffectsOnLOD == 1) return 1.0;
    if (distChunkToCam > uLodDistance) return CalculateHardShadow(hitPos, normal, sunDir);
    #endif

    #ifdef SHADOW_MODE_HARD
        return CalculateHardShadow(hitPos, normal, sunDir);
    #elif defined(SHADOW_MODE_SOFT)
        return CalculateSoftShadow(hitPos, normal, sunDir);
    #else
        return 1.0;
    #endif
}

// =============================================================================
// ТОЧЕЧНЫЕ ИСТОЧНИКИ И МЯГКИЕ ТЕНИ (POINT LIGHTS)
// =============================================================================
#define MAX_POINT_LIGHTS 32
struct PointLightData { vec4 posRadius; vec4 colorIntensity; };
layout(std430, binding = 18) readonly buffer PointLightBuffer { PointLightData pointLights[]; };
uniform int uPointLightCount;

float CalculatePointLightShadow(vec3 hitPos, vec3 normal, vec3 lightPos, float lightRadius, float distToLight, vec3 L) {
    vec3 shadowOrigin = hitPos + normal * 0.05;
    // Останавливаем луч ДО центра источника, чтобы не затеняться о его физические границы, если они есть
    float traceDist = max(0.0, distToLight - 0.2);

    #ifdef SHADOW_MODE_HARD
        float noise = IGN(gl_FragCoord.xy);
    vec3 up = abs(L.y) < 0.999 ? vec3(0,1,0) : vec3(1,0,0);
    vec3 right = normalize(cross(L, up));
    up = cross(right, L);
    float angle = noise * 6.283185;
    vec2 randOffset = vec2(cos(angle), sin(angle)) * 0.025; // Легкий джиттер убирает артефакты DDA!
    vec3 shadowDir = normalize(L + right * randOffset.x + up * randOffset.y);

    float hd = 0.0;
    if (TraceAnyShadow(shadowOrigin, shadowDir, traceDist, hd)) return 0.0;
    return 1.0;
    #elif defined(SHADOW_MODE_SOFT)
        float noiseVal = IGN(gl_FragCoord.xy);
    vec3 up = abs(L.y) < 0.999 ? vec3(0,1,0) : vec3(1,0,0);
    vec3 right = normalize(cross(L, up));
    up = cross(right, L);

    float lightAreaRadius = 0.15;
    float avgBlockerDist = 0.0;
    int blockerCount = 0;

    for(int k = 0; k < BLOCKER_SAMPLES; k++) {
        float angle = (float(k) + noiseVal) * 2.39996;
        float r = sqrt(float(k) + 0.5) / sqrt(float(BLOCKER_SAMPLES));
        vec2 off = vec2(cos(angle), sin(angle)) * r * lightAreaRadius;
        vec3 dir = normalize(L + right * off.x + up * off.y);

        float hd = 0.0;
        if (TraceAnyShadow(shadowOrigin, dir, traceDist, hd)) {
            avgBlockerDist += hd;
            blockerCount++;
        }
    }

    if (blockerCount == 0) return 1.0;
    avgBlockerDist /= float(blockerCount);

    float penumbra = clamp((distToLight - avgBlockerDist) / avgBlockerDist, 0.0, 1.0);
    float shadowRadius = penumbra * lightAreaRadius;
    if (shadowRadius < 0.001) return 0.0;

    // Для локального света используем максимум половину сэмплов от солнца для FPS
    int samples = max(2, uSoftShadowSamples / 2);
    float totalVis = 0.0;

    for(int k = 0; k < samples; k++) {
        float angle = (float(k) + noiseVal) * 2.39996;
        float r = sqrt(float(k) + 0.5) / sqrt(float(samples));
        vec2 off = vec2(cos(angle), sin(angle)) * r * shadowRadius;
        vec3 dir = normalize(L + right * off.x + up * off.y);

        float hd = 0.0;
        if (!TraceAnyShadow(shadowOrigin, dir, traceDist, hd)) {
            totalVis += 1.0;
        }
    }
    return smoothstep(0.0, 1.0, totalVis / float(samples));
    #else
        return 1.0;
    #endif
}

vec3 EvaluatePointLights(vec3 hitPos, vec3 normal) {
    vec3 total = vec3(0.0);
    for (int i = 0; i < uPointLightCount && i < MAX_POINT_LIGHTS; i++) {
        vec3  lPos  = pointLights[i].posRadius.xyz;
        float lRad  = pointLights[i].posRadius.w;
        vec3  lCol  = pointLights[i].colorIntensity.rgb;
        float lInt  = pointLights[i].colorIntensity.a;

        vec3  toL   = lPos - hitPos;
        float dist  = length(toL);

        if (dist > lRad || dist < 0.05) continue;

        vec3 L = toL / dist;
        float nDotL = max(0.0, dot(normal, L));
        if (nDotL <= 0.0) continue;

        float shadow = CalculatePointLightShadow(hitPos, normal, lPos, lRad, dist, L);
        if (shadow <= 0.001) continue;

        float win = max(0.0, 1.0 - dist / lRad);
        win = win * win * win * win;
        total += lCol * nDotL * (lInt / max(dist * dist, 0.01)) * win * shadow;
    }
    return total;
}