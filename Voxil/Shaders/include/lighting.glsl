// --- START OF FILE include/lighting.glsl ---

// =============================================================================
//                              LIGHTING SETTINGS
// =============================================================================
#define SOFT_SHADOW_RADIUS 0.06
#define SHADOW_BIAS 0.001
#define AO_STRENGTH 0.8
// =============================================================================

bool IsSolidVoxelSpace(ivec3 pos) {
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * VOXEL_RESOLUTION;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * VOXEL_RESOLUTION;
    if (any(lessThan(pos, boundMin)) || any(greaterThanEqual(pos, boundMax))) return false;
    ivec3 chunkCoord = pos >> BIT_SHIFT;
    int chunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
    if (chunkIdx == -1) return false;
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
    vec3 aoRayOrigin = hitPos + normal * 0.001;
    vec3 voxelPos = aoRayOrigin * VOXELS_PER_METER;
    // ИСПРАВЛЕНО: Объявляем ipos здесь
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
    return clamp(pow(AO_STRENGTH, finalOcc) + (IGN(gl_FragCoord.xy) - 0.5) / float(VOXEL_RESOLUTION), 0.0, 1.0);
}

bool CheckDynamicShadowHit(vec3 ro, vec3 rd, float maxDist) {
    float tHitIgnored; int objIDIgnored; vec3 normIgnored;
    return TraceDynamicRay(ro, rd, maxDist, tHitIgnored, objIDIgnored, normIgnored);
}

// Быстрая проверка тени (только статика для начала, динамика - опционально)
bool CheckShadowHit(vec3 ro, vec3 rd, float maxDist) {
    // Упрощенный TraceStaticRay без расчета нормалей
    ivec3 cMapPos = ivec3(floor(ro / float(CHUNK_SIZE)));
    vec3 cDeltaDist = abs(1.0 / rd) * float(CHUNK_SIZE);
    ivec3 cStepDir = ivec3(sign(rd));
    // ... копируем инициализацию DDA ...
    vec3 relPos = ro - (vec3(cMapPos) * float(CHUNK_SIZE));
    vec3 cSideDist = (sign(rd) * (relPos - vec3(cMapPos) * float(CHUNK_SIZE)) + (sign(rd) * 0.5 + 0.5) * float(CHUNK_SIZE)) * abs(1.0/rd);
    vec3 cMask = vec3(0);
    float tCurrent = 0.0;

    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < HARD_SHADOW_STEPS; i++) {
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) return false;

        int chunkIdx = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;
        if (chunkIdx != -1) {
            // Voxel DDA
            vec3 pEntry = ro + rd * (tCurrent + 1e-4);
            vec3 pVoxel = pEntry * VOXELS_PER_METER;
            ivec3 vMapPos = ivec3(floor(pVoxel));
            if (tCurrent > 0.0) vMapPos = ivec3(floor((ro + rd * (tCurrent + 0.001)) * VOXELS_PER_METER));

            vec3 vDeltaDist = abs(1.0 / rd);
            ivec3 vStepDir = cStepDir;
            vec3 vSideDist = (sign(rd) * (vec3(vMapPos) - pVoxel) + (sign(rd) * 0.5 + 0.5)) * vDeltaDist;
            vec3 vMask = vec3(0);

            for (int j = 0; j < 512; j++) {
                if ((vMapPos >> BIT_SHIFT) != cMapPos) break;
                ivec3 local = vMapPos & BIT_MASK;
                int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
                uint mat = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;
                // Игнорируем воду для теней (полупрозрачная)
                if (mat != 0u && mat != 4u) return true; // HIT!

                vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                vSideDist += vMask * vDeltaDist; vMapPos += ivec3(vMask) * vStepDir;
            }
        }
        cMask = (cSideDist.x < cSideDist.y) ? ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        cSideDist += cMask * cDeltaDist; cMapPos += ivec3(cMask) * cStepDir;
        tCurrent = dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

float CalculateHardShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    vec3 shadowOrigin = hitPos + normal * SHADOW_BIAS;
    // Проверяем динамику
    float tDyn = 50.0; int id = -1; vec3 n = vec3(0);
    if (TraceDynamicRay(shadowOrigin, sunDir, 50.0, tDyn, id, n)) return 0.0;

    // Проверяем статику (оптимизированно)
    if (CheckShadowHit(shadowOrigin, sunDir, 200.0)) return 0.0;

    return 1.0;
}

float CalculateSoftShadow(vec3 hitPos, vec3 normal, vec3 sunDir) {
    vec3 shadowOrigin = hitPos + normal * SHADOW_BIAS;
    vec3 voxelRo = shadowOrigin * VOXELS_PER_METER;
    vec3 up = abs(sunDir.y) < 0.999 ? vec3(0, 1, 0) : vec3(0, 0, 1);
    vec3 right = normalize(cross(sunDir, up));
    up = cross(right, sunDir);
    float totalVisibility = 0.0;
    float diskRadius = SOFT_SHADOW_RADIUS;
    float noiseVal = random(gl_FragCoord.xy);
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * VOXEL_RESOLUTION;
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * VOXEL_RESOLUTION;
    int samples = uSoftShadowSamples;
    if (samples <= 0) samples = 1;
    for (int k = 0; k < samples; k++) {
        float angle = (float(k) + noiseVal) * 2.39996;
        float r = sqrt(float(k) + 0.5) / sqrt(float(samples));
        vec2 offset = vec2(cos(angle), sin(angle)) * r * diskRadius;
        vec3 offsetDir = right * offset.x + up * offset.y;
        vec3 shadowDir = normalize(sunDir + offsetDir);
        bool hit = false;
        float hitDistMeters = 0.0;
        float tDyn = 100000.0; int idDyn = -1; vec3 nDyn = vec3(0);
        if (TraceDynamicRay(shadowOrigin, shadowDir, 50.0, tDyn, idDyn, nDyn)) {
            hit = true; hitDistMeters = tDyn;
        }
        if (!hit) {
            ivec3 sMapPos = ivec3(floor(voxelRo));
            ivec3 sStepDir = ivec3(sign(shadowDir));
            vec3 sDeltaDist = abs(1.0 / shadowDir);
            vec3 sSideDist = (sign(shadowDir) * (vec3(sMapPos) - voxelRo) + (0.5 + 0.5 * sign(shadowDir))) * sDeltaDist;
            ivec3 sCachedChunkCoord = ivec3(-999999); int sCachedChunkIdx = -1;
            for (int step = 0; step < SOFT_SHADOW_STEPS; step++) {
                if (any(lessThan(sMapPos, bMin)) || any(greaterThanEqual(sMapPos, bMax))) break;
                ivec3 chunkCoord = sMapPos >> BIT_SHIFT;
                if (chunkCoord != sCachedChunkCoord) { sCachedChunkCoord = chunkCoord; sCachedChunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r; }
                if (sCachedChunkIdx != -1) {
                    ivec3 local = sMapPos & BIT_MASK; int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
                    uint sMat = (packedVoxels[sCachedChunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;
                    if (sMat != 0u && sMat != 4u) { hit = true; float hitDistVoxel = distance(voxelRo, vec3(sMapPos) + 0.5); hitDistMeters = hitDistVoxel / VOXELS_PER_METER; break; }
                }
                vec3 sMask = (sSideDist.x < sSideDist.y) ? ((sSideDist.x < sSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((sSideDist.y < sSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                sSideDist += sMask * sDeltaDist; sMapPos += ivec3(sMask) * sStepDir;
            }
        }
        if (!hit) totalVisibility += 1.0;
        else totalVisibility += smoothstep(2.0, 25.0, hitDistMeters) * 0.9;
    }
    return smoothstep(0.0, 1.0, totalVisibility / float(samples));
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