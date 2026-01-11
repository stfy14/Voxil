// --- START OF FILE include/tracing.glsl ---

layout(std430, binding = 5) buffer MaskSSBO { uvec2 packedMasks[]; };

#if VOXEL_BANKS >= 1
layout(std430, binding = 10) buffer VoxelSSBO0 { uint b0[]; };
#endif
#if VOXEL_BANKS >= 2
layout(std430, binding = 11) buffer VoxelSSBO1 { uint b1[]; };
#endif
#if VOXEL_BANKS >= 3
layout(std430, binding = 12) buffer VoxelSSBO2 { uint b2[]; };
#endif
#if VOXEL_BANKS >= 4
layout(std430, binding = 13) buffer VoxelSSBO3 { uint b3[]; };
#endif
#if VOXEL_BANKS >= 5
layout(std430, binding = 14) buffer VoxelSSBO4 { uint b4[]; };
#endif
#if VOXEL_BANKS >= 6
layout(std430, binding = 15) buffer VoxelSSBO5 { uint b5[]; };
#endif
#if VOXEL_BANKS >= 7
layout(std430, binding = 16) buffer VoxelSSBO6 { uint b6[]; };
#endif
#if VOXEL_BANKS >= 8
layout(std430, binding = 17) buffer VoxelSSBO7 { uint b7[]; };
#endif
#if VOXEL_BANKS >= 9
layout(std430, binding = 18) buffer VoxelSSBO8 { uint b8[]; };
#endif
#if VOXEL_BANKS >= 10
layout(std430, binding = 19) buffer VoxelSSBO9 { uint b9[]; };
#endif
#if VOXEL_BANKS >= 11
layout(std430, binding = 20) buffer VoxelSSBO10 { uint b10[]; };
#endif
#if VOXEL_BANKS >= 12
layout(std430, binding = 21) buffer VoxelSSBO11 { uint b11[]; };
#endif
#if VOXEL_BANKS >= 13
layout(std430, binding = 22) buffer VoxelSSBO12 { uint b12[]; };
#endif

#define BLOCK_SIZE 4
#define BLOCKS_PER_AXIS (VOXEL_RESOLUTION / BLOCK_SIZE) 
#define VOXELS_IN_UINT 4

uint GetVoxelData(uint chunkSlot, int voxelIdx) {
    uint bank = chunkSlot / uint(CHUNKS_PER_BANK);
    uint localSlot = chunkSlot % uint(CHUNKS_PER_BANK);
    uint chunkSizeUint = (uint(VOXEL_RESOLUTION)*uint(VOXEL_RESOLUTION)*uint(VOXEL_RESOLUTION)) / uint(VOXELS_IN_UINT);
    uint offset = localSlot * chunkSizeUint + (uint(voxelIdx) >> 2u);
    uint rawVal = 0u;

    switch(bank) {
            #if VOXEL_BANKS >= 1
        case 0u: rawVal = b0[offset]; break;
            #endif
        #if VOXEL_BANKS >= 2
        case 1u: rawVal = b1[offset]; break;
            #endif
        #if VOXEL_BANKS >= 3
        case 2u: rawVal = b2[offset]; break;
            #endif
        #if VOXEL_BANKS >= 4
        case 3u: rawVal = b3[offset]; break;
            #endif
        #if VOXEL_BANKS >= 5
        case 4u: rawVal = b4[offset]; break;
            #endif
        #if VOXEL_BANKS >= 6
        case 5u: rawVal = b5[offset]; break;
            #endif
        #if VOXEL_BANKS >= 7
        case 6u: rawVal = b6[offset]; break;
            #endif
        #if VOXEL_BANKS >= 8
        case 7u: rawVal = b7[offset]; break;
            #endif
        #if VOXEL_BANKS >= 9
        case 8u: rawVal = b8[offset]; break;
            #endif
        #if VOXEL_BANKS >= 10
        case 9u: rawVal = b9[offset]; break;
            #endif
        #if VOXEL_BANKS >= 11
        case 10u: rawVal = b10[offset]; break;
            #endif
        #if VOXEL_BANKS >= 12
        case 11u: rawVal = b11[offset]; break;
            #endif
        #if VOXEL_BANKS >= 13
        case 12u: rawVal = b12[offset]; break;
            #endif
    }

    uint shift = (uint(voxelIdx) & 3u) * 8u;
    return (rawVal >> shift) & 0xFFu;
}

vec2 IntersectAABB(vec3 ro, vec3 invRd, vec3 boxMin, vec3 boxMax) {
    vec3 tMin = (boxMin - ro) * invRd;
    vec3 tMax = (boxMax - ro) * invRd;
    vec3 t1 = min(tMin, tMax);
    vec3 t2 = max(tMin, tMax);
    float tNear = max(max(t1.x, t1.y), t1.z);
    float tFar = min(min(t2.x, t2.y), t2.z);
    return vec2(tNear, tFar);
}

bool TraceDynamicRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout int outObjID, inout vec3 outLocalNormal, inout int steps) {
    tHit = maxDist;
    bool hitAny = false;
    vec3 gridSpaceRo = (ro - uGridOrigin) / uGridStep;
    vec3 invRd = 1.0 / rd;
    vec3 t0 = -gridSpaceRo * invRd;
    vec3 t1 = (vec3(uGridSize) - gridSpaceRo) * invRd;
    vec3 tmin = min(t0, t1), tmax = max(t0, t1);
    float tEnter = max(max(tmin.x, tmin.y), tmin.z);
    float tExit = min(min(tmax.x, tmax.y), tmax.z);
    if (tExit < max(0.0, tEnter) || tEnter > maxDist) return false;
    float tStart = max(0.0, tEnter);
    vec3 currPos = gridSpaceRo + rd * (tStart * uGridStep + 0.001);
    ivec3 mapPos = ivec3(floor(currPos));
    ivec3 stepDir = ivec3(sign(rd));
    vec3 deltaDist = abs(uGridStep * invRd);
    vec3 sideDist = (sign(rd) * (vec3(mapPos) - currPos) + (0.5 + 0.5 * sign(rd))) * deltaDist;

    // === ОПТИМИЗАЦИЯ: "Мертвые" лучи ===
    // Заменяем for на while, чтобы избежать unrolling-а
    int safetyLoop = 0;
    while (safetyLoop < 512) {
        safetyLoop++;
        steps++;

        if (any(lessThan(mapPos, ivec3(0))) || any(greaterThanEqual(mapPos, ivec3(uGridSize)))) break;
        int nodeIndex = imageLoad(uObjectGridHead, mapPos).r;
        while (nodeIndex > 0) {
            steps++;
            int bufferIdx = nodeIndex - 1;
            uint objID = listNodes[bufferIdx].objectID;
            nodeIndex = listNodes[bufferIdx].nextNode;
            if (objID > 0) {
                DynamicObject obj = dynObjects[int(objID) - 1];
                vec3 localRo = (obj.invModel * vec4(ro, 1.0)).xyz;
                vec3 localRd = (obj.invModel * vec4(rd, 0.0)).xyz;
                vec3 lInvRd = 1.0 / localRd;
                vec2 tBox = IntersectAABB(localRo, lInvRd, obj.boxMin.xyz, obj.boxMax.xyz);
                if (tBox.x < tBox.y && tBox.y > 0.0 && tBox.x < tHit) {
                    tHit = tBox.x; outObjID = int(objID) - 1;
                    vec3 tM = (obj.boxMin.xyz - localRo) * lInvRd; vec3 tMx = (obj.boxMax.xyz - localRo) * lInvRd;
                    vec3 tm = min(tM, tMx); vec3 n = step(tm.yzx, tm.xyz) * step(tm.zxy, tm.xyz);
                    outLocalNormal = -n * sign(localRd); hitAny = true;
                }
            }
        }
        vec3 mask = (sideDist.x < sideDist.y) ? ((sideDist.x < sideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((sideDist.y < sideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        if(dot(mask, sideDist) > maxDist) break;
        sideDist += mask * deltaDist;
        mapPos += ivec3(mask) * stepDir;
    }
    return hitAny;
}
bool TraceDynamicRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout int outObjID, inout vec3 outLocalNormal) {
    int dummy = 0; return TraceDynamicRay(ro, rd, maxDist, tHit, outObjID, outLocalNormal, dummy);
}

// === TraceStaticRay ===
bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, float tStart, inout float tHit, inout uint matID, inout vec3 normal, inout int steps) {
    // 1. Определяем границы мира в метрах
    vec3 worldMin = vec3(uBoundMinX, uBoundMinY, uBoundMinZ) * float(CHUNK_SIZE);
    vec3 worldMax = vec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * float(CHUNK_SIZE);

    float tCurrent = max(0.0, tStart);

    // 2. RAY-AABB INTERSECTION (Алгоритм "Slab Method")
    vec3 invRd = 1.0 / rd;
    vec3 t0s = (worldMin - ro) * invRd;
    vec3 t1s = (worldMax - ro) * invRd;

    vec3 tsmaller = min(t0s, t1s);
    vec3 tbigger  = max(t0s, t1s);

    float tMin = max(max(tsmaller.x, tsmaller.y), tsmaller.z);
    float tMax = min(min(tbigger.x,  tbigger.y),  tbigger.z);

    if (tMin > tMax || tMax < 0.0) return false;

    // 3. ТЕЛЕПОРТАЦИЯ ЛУЧА
    if (tMin > tCurrent) {
        tCurrent = tMin + 0.001;
    }

    // 4. CLAMP
    vec3 rayOrigin = ro + rd * tCurrent;
    vec3 clampMin = worldMin + 0.001;
    vec3 clampMax = worldMax - 0.001;
    rayOrigin = clamp(rayOrigin, clampMin, clampMax);

    // Инициализация координат для DDA
    ivec3 cMapPos = ivec3(floor(rayOrigin / float(CHUNK_SIZE)));

    vec3 cDeltaDist = abs(1.0 / rd) * float(CHUNK_SIZE);
    ivec3 cStepDir = ivec3(sign(rd));
    vec3 relPos = rayOrigin - (vec3(cMapPos) * float(CHUNK_SIZE));
    vec3 cSideDist;
    cSideDist.x = (rd.x > 0.0) ? (float(CHUNK_SIZE) - relPos.x) : relPos.x;
    cSideDist.y = (rd.y > 0.0) ? (float(CHUNK_SIZE) - relPos.y) : relPos.y;
    cSideDist.z = (rd.z > 0.0) ? (float(CHUNK_SIZE) - relPos.z) : relPos.z;
    cSideDist *= abs(1.0 / rd);
    vec3 cMask = vec3(0);
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        steps++;
        if (tCurrent > maxDist) break;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkSlot = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkSlot != 0xFFFFFFFFu) {

            // === SOLID CHUNK CHECK ===
            if ((chunkSlot & 0x80000000u) != 0u) {
                tHit = tCurrent;
                matID = chunkSlot & 0x7FFFFFFFu;
                if (length(cMask) > 0.5) normal = -vec3(cStepDir) * cMask;
                else normal = -vec3(cStepDir);
                return true;
            }
            // =========================

            #ifdef ENABLE_LOD
                vec3 chunkCenter = (vec3(cMapPos) + 0.5) * float(CHUNK_SIZE);
            float distToChunk = distance(uCamPos, chunkCenter);
            bool isLodChunk = (distToChunk > uLodDistance);
            #else
                bool isLodChunk = false;
            #endif

            uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);

            #ifdef ENABLE_LOD
            if (isLodChunk) {
                // ===== LOD MODE: Трассируем БЛОКИ 4x4x4 =====

                vec3 pLocal = (ro + rd * tCurrent) - chunkOrigin;
                if (length(cMask) > 0.5) {
                    if (cMask.x > 0.5) pLocal.x = (rd.x > 0.0) ? 0.0 : float(CHUNK_SIZE);
                    if (cMask.y > 0.5) pLocal.y = (rd.y > 0.0) ? 0.0 : float(CHUNK_SIZE);
                    if (cMask.z > 0.5) pLocal.z = (rd.z > 0.0) ? 0.0 : float(CHUNK_SIZE);
                }
                pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);

                // Позиция в блочной сетке (не воксельной!)
                ivec3 blockPos = ivec3(floor(pLocal / float(BLOCK_SIZE)));
                blockPos = clamp(blockPos, ivec3(0), ivec3(BLOCKS_PER_AXIS - 1));

                // DDA по БЛОКАМ 4x4x4
                vec3 bDeltaDist = abs(1.0 / rd) * float(BLOCK_SIZE);
                ivec3 bStepDir = cStepDir;
                vec3 blockRelPos = pLocal - (vec3(blockPos) * float(BLOCK_SIZE));
                vec3 bSideDist;
                bSideDist.x = (rd.x > 0.0) ? (float(BLOCK_SIZE) - blockRelPos.x) : blockRelPos.x;
                bSideDist.y = (rd.y > 0.0) ? (float(BLOCK_SIZE) - blockRelPos.y) : blockRelPos.y;
                bSideDist.z = (rd.z > 0.0) ? (float(BLOCK_SIZE) - blockRelPos.z) : blockRelPos.z;
                bSideDist *= abs(1.0 / rd);
                vec3 bMask = cMask;

                for (int bStep = 0; bStep < 64; bStep++) {
                    if (any(lessThan(blockPos, ivec3(0))) || any(greaterThanEqual(blockPos, ivec3(BLOCKS_PER_AXIS)))) break;

                    int blockIdx = blockPos.x + BLOCKS_PER_AXIS * (blockPos.y + BLOCKS_PER_AXIS * blockPos.z);
                    uvec2 maskVal = packedMasks[maskBaseOffset + blockIdx];

                    // Если блок не пустой
                    if (maskVal.x != 0u || maskVal.y != 0u) {
                        // Читаем ПЕРВЫЙ непустой воксель блока для цвета
                        ivec3 blockOrigin = blockPos * BLOCK_SIZE;
                        uint lodMatID = 0u;

                        for (int bz = 0; bz < BLOCK_SIZE && lodMatID == 0u; bz++) {
                            for (int by = 0; by < BLOCK_SIZE && lodMatID == 0u; by++) {
                                for (int bx = 0; bx < BLOCK_SIZE && lodMatID == 0u; bx++) {
                                    ivec3 voxelPos = blockOrigin + ivec3(bx, by, bz);
                                    int idx = voxelPos.x + VOXEL_RESOLUTION * (voxelPos.y + VOXEL_RESOLUTION * voxelPos.z);
                                    lodMatID = GetVoxelData(chunkSlot, idx);
                                }
                            }
                        }

                        if (lodMatID != 0u) {
                            // Рисуем весь блок 4x4x4
                            vec3 blockMinWorld = chunkOrigin + vec3(blockOrigin);
                            vec3 blockMaxWorld = blockMinWorld + vec3(BLOCK_SIZE);

                            vec3 blockInvRd = 1.0 / rd;
                            vec3 bt0 = (blockMinWorld - ro) * blockInvRd;
                            vec3 bt1 = (blockMaxWorld - ro) * blockInvRd;
                            vec3 btNear = min(bt0, bt1);
                            vec3 btFar = max(bt0, bt1);
                            float blockTNear = max(max(btNear.x, btNear.y), btNear.z);
                            float blockTFar = min(min(btFar.x, btFar.y), btFar.z);

                            if (blockTNear <= blockTFar && blockTFar > 0.0) {
                                tHit = max(tCurrent, blockTNear);
                                matID = lodMatID;

                                // Нормаль блока по грани входа
                                vec3 hitPos = ro + rd * blockTNear;
                                vec3 localHitInBlock = hitPos - blockMinWorld;

                                // ИСПРАВЛЕНИЕ: Точное определение грани через Ray-AABB
                                // Определяем, какая компонента tNear дала минимум
                                vec3 tNearComponents = btNear;
                                normal = vec3(0.0);

                                // Находим грань с минимальным t (через которую вошли)
                                float epsilon = 0.001;
                                if (abs(tNearComponents.x - blockTNear) < epsilon) {
                                    normal.x = -sign(rd.x);
                                } else if (abs(tNearComponents.y - blockTNear) < epsilon) {
                                    normal.y = -sign(rd.y);
                                } else if (abs(tNearComponents.z - blockTNear) < epsilon) {
                                    normal.z = -sign(rd.z);
                                }

                                // Fallback на случай ошибки округления
                                if (length(normal) < 0.5) {
                                    normal = -normalize(rd);
                                }

                                return true;
                            }
                        }
                    }

                    // Шаг к следующему блоку
                    bMask = (bSideDist.x < bSideDist.y)
                    ? ((bSideDist.x < bSideDist.z) ? vec3(1,0,0) : vec3(0,0,1))
                    : ((bSideDist.y < bSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                    bSideDist += bMask * bDeltaDist;
                    blockPos += ivec3(bMask) * bStepDir;
                }

            } else {
                #endif
                // ===== NORMAL MODE: Трассируем воксели 1x1x1 =====

                vec3 pLocal = (ro + rd * tCurrent) - chunkOrigin;
                if (length(cMask) > 0.5) {
                    if (cMask.x > 0.5) pLocal.x = (rd.x > 0.0) ? 0.0 : float(CHUNK_SIZE);
                    if (cMask.y > 0.5) pLocal.y = (rd.y > 0.0) ? 0.0 : float(CHUNK_SIZE);
                    if (cMask.z > 0.5) pLocal.z = (rd.z > 0.0) ? 0.0 : float(CHUNK_SIZE);
                }
                pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);
                vec3 pVoxel = pLocal * VOXELS_PER_METER;
                ivec3 vMapPos = ivec3(floor(pVoxel));
                if (length(cMask) > 0.5) {
                    if (cMask.x > 0.5 && rd.x < 0.0) vMapPos.x = VOXEL_RESOLUTION - 1;
                    if (cMask.y > 0.5 && rd.y < 0.0) vMapPos.y = VOXEL_RESOLUTION - 1;
                    if (cMask.z > 0.5 && rd.z < 0.0) vMapPos.z = VOXEL_RESOLUTION - 1;
                }

                vec3 vDeltaDist = abs(1.0 / rd);
                ivec3 vStepDir = cStepDir;
                vec3 vSideDist;
                vSideDist.x = (rd.x > 0.0) ? (float(vMapPos.x + 1) - pVoxel.x) : (pVoxel.x - float(vMapPos.x));
                vSideDist.y = (rd.y > 0.0) ? (float(vMapPos.y + 1) - pVoxel.y) : (pVoxel.y - float(vMapPos.y));
                vSideDist.z = (rd.z > 0.0) ? (float(vMapPos.z + 1) - pVoxel.z) : (pVoxel.z - float(vMapPos.z));
                vSideDist *= vDeltaDist;
                vec3 vMask = cMask;

                bool exitChunk = false;

                for (int sanityChunk = 0; sanityChunk < 512; sanityChunk++) {
                    if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) { exitChunk = true; break; }

                    ivec3 bMapPos = vMapPos / BLOCK_SIZE;
                    int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);
                    uvec2 maskVal = packedMasks[maskBaseOffset + blockIdx];

                    if (maskVal.x == 0u && maskVal.y == 0u) {
                        for(int s=0; s<64; s++) {
                            vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                            vSideDist += vMask * vDeltaDist;
                            vMapPos += ivec3(vMask) * vStepDir;
                            if (vMapPos / BLOCK_SIZE != bMapPos) break;
                            if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) { exitChunk = true; break; }
                        }
                    } else {
                        for(int s=0; s<64; s++) {
                            int lx = vMapPos.x % BLOCK_SIZE; int ly = vMapPos.y % BLOCK_SIZE; int lz = vMapPos.z % BLOCK_SIZE;
                            int bitIdx = lx + BLOCK_SIZE * (ly + BLOCK_SIZE * lz);
                            bool hasVoxel = (bitIdx < 32) ? ((maskVal.x & (1u << bitIdx)) != 0u) : ((maskVal.y & (1u << (bitIdx - 32))) != 0u);

                            if (hasVoxel) {
                                int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                                uint m = GetVoxelData(chunkSlot, idx);
                                if (m != 0u) {
                                    float tRelVoxel = (length(vMask) > 0.5) ? dot(vMask, vSideDist - vDeltaDist) : 0.0;
                                    tHit = tCurrent + (tRelVoxel / VOXELS_PER_METER);
                                    matID = m;
                                    normal = -vec3(vStepDir) * vMask;
                                    return true;
                                }
                            }
                            vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                            vSideDist += vMask * vDeltaDist;
                            vMapPos += ivec3(vMask) * vStepDir;
                            if (vMapPos / BLOCK_SIZE != bMapPos) break;
                            if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) { exitChunk = true; break; }
                        }
                    }
                    if (exitChunk) break;
                }
            }
        }
        cMask = (cSideDist.x < cSideDist.y) ? ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        cSideDist += cMask * cDeltaDist;
        cMapPos += ivec3(cMask) * cStepDir;
        tCurrent = tStart + dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, float tStart, inout float tHit, inout uint matID, inout vec3 normal) {
    int dummy = 0; return TraceStaticRay(ro, rd, maxDist, tStart, tHit, matID, normal, dummy);
}

// === TraceShadowRay ===
bool TraceShadowRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    vec3 pStart = ro + rd * 0.001;
    ivec3 cMapPos = ivec3(floor(pStart / float(CHUNK_SIZE)));
    vec3 cDeltaDist = abs(1.0 / rd) * float(CHUNK_SIZE);
    ivec3 cStepDir = ivec3(sign(rd));
    vec3 relPos = pStart - (vec3(cMapPos) * float(CHUNK_SIZE));
    vec3 cSideDist;
    cSideDist.x = (rd.x > 0.0) ? (float(CHUNK_SIZE) - relPos.x) : relPos.x;
    cSideDist.y = (rd.y > 0.0) ? (float(CHUNK_SIZE) - relPos.y) : relPos.y;
    cSideDist.z = (rd.z > 0.0) ? (float(CHUNK_SIZE) - relPos.z) : relPos.z;
    cSideDist *= abs(1.0 / rd);
    vec3 cMask = vec3(0);
    float tCurrentRel = 0.0;
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        if (tCurrentRel > maxDist) return false;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkSlot = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkSlot != 0xFFFFFFFFu) {

            // === SOLID CHUNK CHECK ===
            if ((chunkSlot & 0x80000000u) != 0u) {
                tHit = tCurrentRel;
                matID = chunkSlot & 0x7FFFFFFFu;
                return true;
            }
            // =========================

            #ifdef ENABLE_LOD
                vec3 chunkCenter = (vec3(cMapPos) + 0.5) * float(CHUNK_SIZE);
            float distToChunk = distance(uCamPos, chunkCenter);
            bool isLodChunk = (distToChunk > uLodDistance);
            #else
                bool isLodChunk = false;
            #endif

            uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 pLocal = (pStart + rd * tCurrentRel) - chunkOrigin;

            if (length(cMask) > 0.5) {
                if (cMask.x > 0.5) pLocal.x = (rd.x > 0.0) ? 0.0 : float(CHUNK_SIZE);
                if (cMask.y > 0.5) pLocal.y = (rd.y > 0.0) ? 0.0 : float(CHUNK_SIZE);
                if (cMask.z > 0.5) pLocal.z = (rd.z > 0.0) ? 0.0 : float(CHUNK_SIZE);
            }
            pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);
            vec3 pVoxel = pLocal * VOXELS_PER_METER;
            ivec3 vMapPos = ivec3(floor(pVoxel));
            if (length(cMask) > 0.5) {
                if (cMask.x > 0.5 && rd.x < 0.0) vMapPos.x = VOXEL_RESOLUTION - 1;
                if (cMask.y > 0.5 && rd.y < 0.0) vMapPos.y = VOXEL_RESOLUTION - 1;
                if (cMask.z > 0.5 && rd.z < 0.0) vMapPos.z = VOXEL_RESOLUTION - 1;
            }

            vec3 vDeltaDist = abs(1.0 / rd);
            ivec3 vStepDir = cStepDir;
            vec3 vSideDist;
            vSideDist.x = (rd.x > 0.0) ? (float(vMapPos.x + 1) - pVoxel.x) : (pVoxel.x - float(vMapPos.x));
            vSideDist.y = (rd.y > 0.0) ? (float(vMapPos.y + 1) - pVoxel.y) : (pVoxel.y - float(vMapPos.y));
            vSideDist.z = (rd.z > 0.0) ? (float(vMapPos.z + 1) - pVoxel.z) : (pVoxel.z - float(vMapPos.z));
            vSideDist *= vDeltaDist;
            vec3 vMask = vec3(0);

            if (length(cMask) > 0.5) vMask = cMask;

            bool exitChunk = false;

            for(int sanity = 0; sanity < 512; sanity++) {
                if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                ivec3 bMapPos = vMapPos / BLOCK_SIZE;
                int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);
                uvec2 maskVal = packedMasks[maskBaseOffset + blockIdx];

                if (maskVal.x == 0u && maskVal.y == 0u) {
                    for(int s=0; s<64; s++) {
                        vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                        vSideDist += vMask * vDeltaDist;
                        vMapPos += ivec3(vMask) * vStepDir;
                        if (vMapPos / BLOCK_SIZE != bMapPos) break;
                        if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) { exitChunk = true; break; }
                    }
                } else {
                    #ifdef ENABLE_LOD
                        if (isLodChunk) {
                        // === LOD ТЕНИ: Блок 4x4x4 либо ПОЛНОСТЬЮ непрозрачен, либо пуст ===

                        // Если маска блока не пустая — считаем весь блок непрозрачным
                        // (maskVal уже загружен выше)

                        // Вычисляем границы блока
                        ivec3 blockOrigin = bMapPos * BLOCK_SIZE;
                        vec3 blockMinWorld = chunkOrigin + vec3(blockOrigin);
                        vec3 blockMaxWorld = blockMinWorld + vec3(BLOCK_SIZE);

                        // Ray-AABB intersection для блока
                        vec3 invRd = 1.0 / rd;
                        vec3 t0 = (blockMinWorld - pStart) * invRd;
                        vec3 t1 = (blockMaxWorld - pStart) * invRd;
                        vec3 tNearVec = min(t0, t1);
                        vec3 tFarVec = max(t0, t1);

                        float tNear = max(max(tNearVec.x, tNearVec.y), tNearVec.z);
                        float tFar = min(min(tFarVec.x, tFarVec.y), tFarVec.z);

                        // Если луч попадает в блок — это тень
                        if (tNear <= tFar && tFar > 0.0 && tNear < maxDist) {
                            tHit = tCurrentRel + tNear;
                            matID = 1u;
                            return true;
                        }

                        // Пропускаем блок
                        for(int s=0; s<64; s++) {
                            vMask = (vSideDist.x < vSideDist.y)
                            ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1))
                            : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                            vSideDist += vMask * vDeltaDist;
                            vMapPos += ivec3(vMask) * vStepDir;

                            if (vMapPos / BLOCK_SIZE != bMapPos) break;
                            if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) {
                                exitChunk = true;
                                break;
                            }
                        }

                        if (exitChunk) break;
                        continue;
                    }
                    #endif

                    for(int s=0; s<64; s++) {
                        int lx = vMapPos.x % BLOCK_SIZE; int ly = vMapPos.y % BLOCK_SIZE; int lz = vMapPos.z % BLOCK_SIZE;
                        int bitIdx = lx + BLOCK_SIZE * (ly + BLOCK_SIZE * lz);
                        bool hasVoxel = (bitIdx < 32) ? ((maskVal.x & (1u << bitIdx)) != 0u) : ((maskVal.y & (1u << (bitIdx - 32))) != 0u);
                        if (hasVoxel) {
                            int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                            uint m = GetVoxelData(chunkSlot, idx);
                            if (m != 0u) {
                                float tRelVoxel = (length(vMask) > 0.5) ? dot(vMask, vSideDist - vDeltaDist) : 0.0;
                                tHit = tCurrentRel + (tRelVoxel / VOXELS_PER_METER);
                                matID = m;
                                return true;
                            }
                        }
                        vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                        vSideDist += vMask * vDeltaDist;
                        vMapPos += ivec3(vMask) * vStepDir;
                        if (vMapPos / BLOCK_SIZE != bMapPos) break;
                        if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) { exitChunk = true; break; }
                    }
                }
                if (exitChunk) break;
            }
        }
        cMask = (cSideDist.x < cSideDist.y) ? ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        cSideDist += cMask * cDeltaDist;
        cMapPos += ivec3(cMask) * cStepDir;
        tCurrentRel = dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

bool TraceRefractionRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    return TraceShadowRay(ro, rd, maxDist, tHit, matID);
}