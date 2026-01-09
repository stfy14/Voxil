// --- START OF FILE include/tracing.glsl ---

// Маски (Binding 5)
layout(std430, binding = 5) buffer MaskSSBO { uvec2 packedMasks[]; };

// Динамические банки вокселей (Binding 10+)
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

#define BLOCK_SIZE 4
#define BLOCKS_PER_AXIS (VOXEL_RESOLUTION / BLOCK_SIZE) 
#define VOXELS_IN_UINT 4 

// === ЧТЕНИЕ ВОКСЕЛЯ ===
uint GetVoxelData(uint chunkSlot, int voxelIdx) {
    uint bank = chunkSlot % uint(VOXEL_BANKS);
    uint localSlot = chunkSlot / uint(VOXEL_BANKS);

    uint chunkSizeUint = (uint(VOXEL_RESOLUTION)*uint(VOXEL_RESOLUTION)*uint(VOXEL_RESOLUTION)) / uint(VOXELS_IN_UINT);
    uint offset = localSlot * chunkSizeUint + (uint(voxelIdx) >> 2u);

    uint rawVal = 0u;
    #if VOXEL_BANKS == 1
        rawVal = b0[offset];
    #else
        switch(bank) {
        case 0u: rawVal = b0[offset]; break;
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
        }
    #endif
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

// === ДИНАМИКА ===
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

    for (int i = 0; i < 128; i++) {
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
                    tHit = tBox.x;
                    outObjID = int(objID) - 1;
                    vec3 tM = (obj.boxMin.xyz - localRo) * lInvRd;
                    vec3 tMx = (obj.boxMax.xyz - localRo) * lInvRd;
                    vec3 tm = min(tM, tMx);
                    vec3 n = step(tm.yzx, tm.xyz) * step(tm.zxy, tm.xyz);
                    outLocalNormal = -n * sign(localRd);
                    hitAny = true;
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

// === 4. СТАТИКА (Рендер) ===
bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, float tStart, inout float tHit, inout uint matID, inout vec3 normal, inout int steps) {
    float tCurrent = max(0.0, tStart);
    vec3 rayOrigin = ro + rd * (tCurrent + 0.001);
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
            uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 pLocal = clamp((ro + rd * tCurrent) - chunkOrigin, 0.0, float(CHUNK_SIZE) - 0.0001);
            vec3 pVoxel = pLocal * VOXELS_PER_METER;
            ivec3 vMapPos = ivec3(floor(pVoxel));
            vec3 vDeltaDist = abs(1.0 / rd);
            vec3 vSideDist;
            vSideDist.x = (rd.x > 0.0) ? (float(vMapPos.x + 1) - pVoxel.x) : (pVoxel.x - float(vMapPos.x));
            vSideDist.y = (rd.y > 0.0) ? (float(vMapPos.y + 1) - pVoxel.y) : (pVoxel.y - float(vMapPos.y));
            vSideDist.z = (rd.z > 0.0) ? (float(vMapPos.z + 1) - pVoxel.z) : (pVoxel.z - float(vMapPos.z));
            vSideDist *= vDeltaDist;
            vec3 vMask = vec3(0);

            for (int s = 0; s < 512; s++) {
                steps++;
                if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                ivec3 bMapPos = vMapPos / BLOCK_SIZE;
                int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);
                uvec2 maskVal = packedMasks[maskBaseOffset + blockIdx];

                if (maskVal.x == 0u && maskVal.y == 0u) {
                    for(int k=0; k<64; k++) {
                        vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                        vSideDist += vMask * vDeltaDist;
                        vMapPos += ivec3(vMask) * cStepDir;
                        if (vMapPos / BLOCK_SIZE != bMapPos) break;
                        if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                    }
                }
                else {
                    for(int k=0; k<64; k++) {
                        int lx = vMapPos.x % BLOCK_SIZE;
                        int ly = vMapPos.y % BLOCK_SIZE;
                        int lz = vMapPos.z % BLOCK_SIZE;
                        int bitIdx = lx + BLOCK_SIZE * (ly + BLOCK_SIZE * lz);
                        bool hasVoxel = (bitIdx < 32) ? ((maskVal.x & (1u << bitIdx)) != 0u) : ((maskVal.y & (1u << (bitIdx - 32))) != 0u);
                        if (hasVoxel) {
                            int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                            uint mat = GetVoxelData(chunkSlot, idx);
                            if (mat != 0u) {
                                float tRel = (length(vMask) > 0.5) ? dot(vMask, vSideDist - vDeltaDist) : 0.0;
                                tHit = tCurrent + (tRel / VOXELS_PER_METER);
                                matID = mat;
                                if (length(vMask) < 0.5) { if (length(cMask) > 0.5) normal = -vec3(cStepDir)*cMask; else normal = -vec3(cStepDir); }
                                else { normal = -vec3(cStepDir) * vMask; }
                                return true;
                            }
                        }
                        vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                        vSideDist += vMask * vDeltaDist;
                        vMapPos += ivec3(vMask) * cStepDir;
                        if (vMapPos / BLOCK_SIZE != bMapPos) break;
                        if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                    }
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
    int d = 0; return TraceStaticRay(ro, rd, maxDist, tStart, tHit, matID, normal, d);
}

// === 5. ТЕНИ / ВОДА ===
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
            uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 pLocal = clamp((pStart + rd * tCurrentRel) - chunkOrigin, 0.0, float(CHUNK_SIZE) - 0.0001);
            vec3 pVoxel = pLocal * VOXELS_PER_METER;
            ivec3 vMapPos = ivec3(floor(pVoxel));
            vec3 vDeltaDist = abs(1.0 / rd);
            vec3 vSideDist;
            vSideDist.x = (rd.x > 0.0) ? (float(vMapPos.x + 1) - pVoxel.x) : (pVoxel.x - float(vMapPos.x));
            vSideDist.y = (rd.y > 0.0) ? (float(vMapPos.y + 1) - pVoxel.y) : (pVoxel.y - float(vMapPos.y));
            vSideDist.z = (rd.z > 0.0) ? (float(vMapPos.z + 1) - pVoxel.z) : (pVoxel.z - float(vMapPos.z));
            vSideDist *= vDeltaDist;
            vec3 vMask = vec3(0);

            for(int sanity = 0; sanity < 512; sanity++) {
                if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                ivec3 bMapPos = vMapPos / BLOCK_SIZE;
                int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);
                uvec2 maskVal = packedMasks[maskBaseOffset + blockIdx];

                if (maskVal.x == 0u && maskVal.y == 0u) {
                    for(int s=0; s<64; s++) {
                        vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                        vSideDist += vMask * vDeltaDist;
                        vMapPos += ivec3(vMask) * cStepDir;
                        if (vMapPos / BLOCK_SIZE != bMapPos) break;
                        if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                    }
                }
                else {
                    for(int s=0; s<64; s++) {
                        int lx = vMapPos.x % BLOCK_SIZE;
                        int ly = vMapPos.y % BLOCK_SIZE;
                        int lz = vMapPos.z % BLOCK_SIZE;
                        int bitIdx = lx + BLOCK_SIZE * (ly + BLOCK_SIZE * lz);
                        bool hasVoxel = (bitIdx < 32) ? ((maskVal.x & (1u << bitIdx)) != 0u) : ((maskVal.y & (1u << (bitIdx - 32))) != 0u);
                        if (hasVoxel) {
                            int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                            // ИСПОЛЬЗУЕМ GetVoxelData
                            uint m = GetVoxelData(chunkSlot, idx);
                            // ID 4 = Вода
                            if (m != 0u && m != 4u) {
                                float tRelVoxel = (length(vMask) > 0.5) ? dot(vMask, vSideDist - vDeltaDist) : 0.0;
                                tHit = tCurrentRel + (tRelVoxel / VOXELS_PER_METER);
                                matID = m;
                                return true;
                            }
                        }
                        vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                        vSideDist += vMask * vDeltaDist;
                        vMapPos += ivec3(vMask) * cStepDir;
                        if (vMapPos / BLOCK_SIZE != bMapPos) break;
                        if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                    }
                }
                if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
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