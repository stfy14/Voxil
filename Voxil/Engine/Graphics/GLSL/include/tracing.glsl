// --- START OF FILE tracing.glsl.txt ---
layout(std430, binding = 5) buffer MaskSSBO { uvec2 packedMasks[]; };
layout(std430, binding = 7) buffer DynSVOBuffer { uvec4 svoNodes[]; };

/*__BANKS_INJECTION__*/

#define BLOCK_SIZE 4
#define BLOCKS_PER_AXIS (VOXEL_RESOLUTION / BLOCK_SIZE) 
#define VOXELS_IN_UINT 4
#define SVO_STACK_SIZE 24

uint GetVoxelData(uint chunkSlot, int voxelIdx) {
    uint bank = chunkSlot / uint(CHUNKS_PER_BANK);
    uint localSlot = chunkSlot % uint(CHUNKS_PER_BANK);
    uint chunkSizeUint = (uint(VOXEL_RESOLUTION)*uint(VOXEL_RESOLUTION)*uint(VOXEL_RESOLUTION)) / uint(VOXELS_IN_UINT);
    uint offset = localSlot * chunkSizeUint + (uint(voxelIdx) >> 2u);
    uint rawVal = GetVoxelFromBank(bank, offset);
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

// === TraceSVO (Dynamic Objects) ===
uint TraceSVO(vec3 localRo, vec3 localRd, uint svoOffset, uint gridSize, float voxelSize, inout float tHit, out vec3 outNormal, inout int steps) {
    outNormal = vec3(0.0);
    if (gridSize == 0u || voxelSize == 0.0) return 0u;

    vec3 safeRd = localRd;
    if (abs(safeRd.x) < 1e-6) safeRd.x = (safeRd.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.y) < 1e-6) safeRd.y = (safeRd.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.z) < 1e-6) safeRd.z = (safeRd.z < 0.0) ? -1e-6 : 1e-6;
    vec3 invRd = 1.0 / safeRd;

    float objSize = float(gridSize) * voxelSize;
    vec2 tRoot = IntersectAABB(localRo, invRd, vec3(0.0), vec3(objSize));
    if (tRoot.x > tRoot.y || tRoot.y < 0.0 || tRoot.x >= tHit) return 0u;

    uint stkNode[SVO_STACK_SIZE];
    uint stkPacked[SVO_STACK_SIZE];
    int top = 0;
    stkNode[0] = svoOffset;
    stkPacked[0] = 0u;

    float bestT = tHit;
    uint bestMat = 0u;
    vec3 bestN = vec3(0.0);

    uint nx, ny, nz, depth;
    uint halfVoxels;
    float nSize, halfSize;
    uint nextDepth;

    while (top >= 0) {
        steps++;
        uint nodeIdx = stkNode[top];
        uint packedData = stkPacked[top--];
        nx = packedData & 0xFFu;
        ny = (packedData >> 8u) & 0xFFu;
        nz = (packedData >> 16u) & 0xFFu;
        depth = (packedData >> 24u) & 0x0Fu;
        nSize = float(gridSize >> depth) * voxelSize;
        vec3 nMin = vec3(float(nx), float(ny), float(nz)) * voxelSize;
        vec2 tBox = IntersectAABB(localRo, invRd, nMin, nMin + nSize);
        if (tBox.x > tBox.y || tBox.y < 0.0 || tBox.x >= bestT) continue;

        uvec4 node = svoNodes[nodeIdx];
        uint childMask = node.x;
        uint childOffset = node.y;
        uint material = node.z;

        if (childMask == 0u) {
            if (material != 0u) {
                float t = max(tBox.x, 0.0);
                if (t < bestT) {
                    bestT = t;
                    bestMat = material;
                    vec3 tNears = min((nMin - localRo) * invRd, (nMin + nSize - localRo) * invRd);
                    float eps = 1e-4 * (1.0 + abs(tBox.x));
                    if (abs(tNears.x - tBox.x) < eps) bestN = vec3(-sign(safeRd.x), 0.0, 0.0);
                    else if (abs(tNears.y - tBox.x) < eps) bestN = vec3(0.0, -sign(safeRd.y), 0.0);
                    else bestN = vec3(0.0, 0.0, -sign(safeRd.z));
                }
            }
            continue;
        }

        halfVoxels = gridSize >> (depth + 1u);
        halfSize = nSize * 0.5;
        nextDepth = depth + 1u;

        for (int i = 7; i >= 0; i--) {
            if ((childMask & (1u << i)) == 0u) continue;
            if (top + 1 >= SVO_STACK_SIZE) break;
            uint cNx = nx + ((uint(i) & 1u) != 0u ? halfVoxels : 0u);
            uint cNy = ny + ((uint(i) & 2u) != 0u ? halfVoxels : 0u);
            uint cNz = nz + ((uint(i) & 4u) != 0u ? halfVoxels : 0u);
            vec3 cMin = vec3(float(cNx), float(cNy), float(cNz)) * voxelSize;
            if (IntersectAABB(localRo, invRd, cMin, cMin + halfSize).x >= bestT) continue;
            uint localIdx = uint(bitCount(childMask & ((1u << uint(i)) - 1u)));
            stkNode[++top] = svoOffset + childOffset + localIdx;
            stkPacked[top] = cNx | (cNy << 8u) | (cNz << 16u) | (nextDepth << 24u);
        }
    }
    if (bestMat != 0u) { tHit = bestT; outNormal = bestN; }
    return bestMat;
}

// === TraceDynamicRay ===
bool TraceDynamicRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout int outObjID, inout vec3 outLocalNormal, out uint outMatID, inout int steps) {
    tHit = maxDist;
    outMatID = 0u;
    bool hitAny = false;

    vec3 safeRd = rd;
    if (abs(safeRd.x) < 1e-6) safeRd.x = (safeRd.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.y) < 1e-6) safeRd.y = (safeRd.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.z) < 1e-6) safeRd.z = (safeRd.z < 0.0) ? -1e-6 : 1e-6;
    vec3 invRd = 1.0 / safeRd;

    vec3 gridMin = uGridOrigin;
    vec3 gridMax = uGridOrigin + vec3(uGridSize) * uGridStep;
    vec2 tGrid = IntersectAABB(ro, invRd, gridMin, gridMax);
    if (tGrid.x > tGrid.y || tGrid.y < 0.0 || tGrid.x > maxDist) return false;

    float tCurrent = max(0.0, tGrid.x) + 0.001;
    float tDdaStart = tCurrent;

    vec3 rayOriginWorld = ro + safeRd * tCurrent;
    rayOriginWorld = clamp(rayOriginWorld, gridMin + 0.001, gridMax - 0.001);

    vec3 rayPosGrid = (rayOriginWorld - uGridOrigin) / uGridStep;
    ivec3 mapPos = ivec3(floor(rayPosGrid));
    vec3 deltaDist = abs(invRd) * uGridStep;
    ivec3 stepDir = ivec3(sign(safeRd));
    vec3 sideDist = (sign(safeRd) * (vec3(mapPos) - rayPosGrid) + (sign(safeRd) * 0.5 + 0.5)) * deltaDist;

    vec3 mask;
    for (int safetyLoop = 0; safetyLoop < 512; safetyLoop++) {
        steps++;
        if (any(lessThan(mapPos, ivec3(0))) || any(greaterThanEqual(mapPos, ivec3(uGridSize)))) break;
        if (tCurrent > maxDist || tCurrent > tGrid.y) break;

        int nodeIndex = texelFetch(uObjectGridHead, mapPos, 0).r;

        while (nodeIndex > 0) {
            steps++;
            int bufferIdx = nodeIndex - 1;
            uint objID = listNodes[bufferIdx].objectID;
            nodeIndex = listNodes[bufferIdx].nextNode;

            if (objID > 0u) {
                DynamicObject obj = dynObjects[int(objID) - 1];
                vec3 localRo = (obj.invModel * vec4(ro, 1.0)).xyz;
                vec3 localRd = (obj.invModel * vec4(safeRd, 0.0)).xyz;

                if (obj.gridSize == 0u || obj.svoOffset == 0xFFFFFFFFu) continue;

                float svoT = tHit;
                vec3 svoNormal;
                uint hitMat = TraceSVO(localRo, localRd, obj.svoOffset, obj.gridSize, obj.voxelSize, svoT, svoNormal, steps);

                if (hitMat != 0u) {
                    tHit = svoT;
                    outObjID = int(objID) - 1;
                    outLocalNormal = normalize((transpose(obj.invModel) * vec4(svoNormal, 0.0)).xyz);
                    outMatID = hitMat;
                    hitAny = true;
                }
            }
        }
        
        if (sideDist.x < sideDist.y && sideDist.x < sideDist.z) { mask = vec3(1,0,0); sideDist.x += deltaDist.x; mapPos.x += stepDir.x; }
        else if (sideDist.y < sideDist.z) { mask = vec3(0,1,0); sideDist.y += deltaDist.y; mapPos.y += stepDir.y; }
        else { mask = vec3(0,0,1); sideDist.z += deltaDist.z; mapPos.z += stepDir.z; }
        tCurrent = tDdaStart + dot(mask, sideDist - deltaDist);
    }
    return hitAny;
}

bool TraceDynamicRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout int outObjID, inout vec3 outLocalNormal) {
    int dummy = 0; uint dummyMat = 0u;
    return TraceDynamicRay(ro, rd, maxDist, tHit, outObjID, outLocalNormal, dummyMat, dummy);
}

// === TraceStaticRay ===
bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, float tStart, inout float tHit, inout uint matID, inout vec3 normal, inout int steps) {
    vec3 safeRd = rd;
    if (abs(safeRd.x) < 1e-6) safeRd.x = (safeRd.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.y) < 1e-6) safeRd.y = (safeRd.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.z) < 1e-6) safeRd.z = (safeRd.z < 0.0) ? -1e-6 : 1e-6;
    vec3 invRd = 1.0 / safeRd;

    vec3 worldMin = vec3(uBoundMinX, uBoundMinY, uBoundMinZ) * float(CHUNK_SIZE);
    vec3 worldMax = vec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * float(CHUNK_SIZE);

    float tCurrent = max(0.0, tStart);
    vec2 tWorld = IntersectAABB(ro, invRd, worldMin, worldMax);
    if (tWorld.x > tWorld.y || tWorld.y < 0.0) return false;
    if (tWorld.x > tCurrent) tCurrent = tWorld.x + 0.001;

    float tDdaStart = tCurrent;
    vec3 rayOrigin = ro + safeRd * tCurrent;
    rayOrigin = clamp(rayOrigin, worldMin + 0.001, worldMax - 0.001);

    vec3 rayPosChunk = rayOrigin / float(CHUNK_SIZE);
    ivec3 cMapPos = ivec3(floor(rayPosChunk));
    vec3 cDeltaDist = abs(invRd) * float(CHUNK_SIZE);
    ivec3 cStepDir = ivec3(sign(safeRd));
    vec3 cSideDist = (sign(safeRd) * (vec3(cMapPos) - rayPosChunk) + (sign(safeRd) * 0.5 + 0.5)) * cDeltaDist;
    vec3 cMask = vec3(0);
    
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        steps++;
        if (tCurrent > maxDist || tCurrent > tWorld.y) break;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkSlot = texelFetch(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1), 0).r;

        if (chunkSlot != 0xFFFFFFFFu) {
            if ((chunkSlot & 0x80000000u) != 0u) {
                // Идеальное пересечение с AABB целого чанка! Никаких погрешностей
                if (length(cMask) < 0.5) tHit = tCurrent;
                else {
                    vec3 hitMin = vec3(cMapPos) * float(CHUNK_SIZE);
                    vec3 tEntry = min((hitMin - ro) * invRd, (hitMin + float(CHUNK_SIZE) - ro) * invRd);
                    tHit = max(max(tEntry.x, tEntry.y), tEntry.z);
                }
                matID = chunkSlot & 0x7FFFFFFFu;
                normal = length(cMask) > 0.5 ? -vec3(cStepDir) * cMask : -normalize(safeRd);
                return true;
            }

            #ifdef ENABLE_LOD
            float distToChunk = distance(uCamPos, (vec3(cMapPos) + 0.5) * float(CHUNK_SIZE));
            bool isLodChunk = (distToChunk > uLodDistance);
            #else
            bool isLodChunk = false;
            #endif

            uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 pLocal = (ro + safeRd * tCurrent) - chunkOrigin;
            pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);

            if (isLodChunk) {
                vec3 rayPosBlock = pLocal / float(BLOCK_SIZE);
                ivec3 blockPos = ivec3(floor(rayPosBlock));
                blockPos = clamp(blockPos, ivec3(0), ivec3(BLOCKS_PER_AXIS - 1));

                vec3 bDeltaDist = abs(invRd) * float(BLOCK_SIZE);
                ivec3 bStepDir = cStepDir;
                vec3 bSideDist = (sign(safeRd) * (vec3(blockPos) - rayPosBlock) + (sign(safeRd) * 0.5 + 0.5)) * bDeltaDist;
                vec3 bMask = cMask;

                for (int bStep = 0; bStep < 64; bStep++) {
                    if (any(lessThan(blockPos, ivec3(0))) || any(greaterThanEqual(blockPos, ivec3(BLOCKS_PER_AXIS)))) break;
                    uint bIdx = uint(blockPos.x + BLOCKS_PER_AXIS * (blockPos.y + BLOCKS_PER_AXIS * blockPos.z));
                    uvec2 maskVal = packedMasks[maskBaseOffset + bIdx];
                    
                    if (maskVal.x != 0u || maskVal.y != 0u) {
                        uint bitIdx = (maskVal.x != 0u) ? uint(findLSB(maskVal.x)) : 32u + uint(findLSB(maskVal.y));
                        ivec3 blockOrigin = blockPos * BLOCK_SIZE;
                        int idx = (blockOrigin.x + (int(bitIdx) & 3)) + VOXEL_RESOLUTION * ((blockOrigin.y + ((int(bitIdx) >> 2) & 3)) + VOXEL_RESOLUTION * (blockOrigin.z + (int(bitIdx) >> 4)));
                        uint lodMatID = GetVoxelData(chunkSlot, idx);
                        
                        if (lodMatID != 0u) {
                            if (length(bMask) < 0.5) tHit = tCurrent;
                            else {
                                vec3 hitMin = chunkOrigin + vec3(blockOrigin);
                                vec3 tEntry = min((hitMin - ro) * invRd, (hitMin + float(BLOCK_SIZE) - ro) * invRd);
                                tHit = max(max(tEntry.x, tEntry.y), tEntry.z);
                            }
                            matID = lodMatID;
                            normal = length(bMask) > 0.5 ? -vec3(cStepDir) * bMask : -normalize(safeRd);
                            return true;
                        }
                    }
                    if (bSideDist.x < bSideDist.y && bSideDist.x < bSideDist.z) { bMask = vec3(1,0,0); bSideDist.x += bDeltaDist.x; blockPos.x += bStepDir.x; }
                    else if (bSideDist.y < bSideDist.z) { bMask = vec3(0,1,0); bSideDist.y += bDeltaDist.y; blockPos.y += bStepDir.y; }
                    else { bMask = vec3(0,0,1); bSideDist.z += bDeltaDist.z; blockPos.z += bStepDir.z; }
                }
            } else {
                vec3 rayPosVoxel = pLocal * VOXELS_PER_METER;
                ivec3 vMapPos = ivec3(floor(rayPosVoxel));

                vec3 vDeltaDist = abs(invRd) / VOXELS_PER_METER;
                ivec3 vStepDir = cStepDir;
                vec3 vSideDist = (sign(safeRd) * (vec3(vMapPos) - rayPosVoxel) + (sign(safeRd) * 0.5 + 0.5)) * vDeltaDist;
                vec3 vMask = cMask;

                for (int s = 0; s < 512; s++) {
                    if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                    ivec3 bMapPos = vMapPos >> 2;
                    uint bIdx = uint(bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z));
                    uvec2 maskVal = packedMasks[maskBaseOffset + bIdx];

                    if (maskVal.x == 0u && maskVal.y == 0u) {
                        for(int j=0; j<64; j++) {
                            if (vSideDist.x < vSideDist.y && vSideDist.x < vSideDist.z) { 
                                vMask = vec3(1,0,0); vSideDist.x += vDeltaDist.x; vMapPos.x += vStepDir.x; 
                            }
                            else if (vSideDist.y < vSideDist.z) { 
                                vMask = vec3(0,1,0); vSideDist.y += vDeltaDist.y; vMapPos.y += vStepDir.y; 
                            }
                            else { 
                                vMask = vec3(0,0,1); vSideDist.z += vDeltaDist.z; vMapPos.z += vStepDir.z; 
                            }
                            if ((vMapPos >> 2) != bMapPos) break;
                            if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                        }
                    } else {
                        int lx = vMapPos.x & 3, ly = vMapPos.y & 3, lz = vMapPos.z & 3;
                        int bitIdx = lx | (ly << 2) | (lz << 4);
                        bool hasVoxel = (bitIdx < 32) ? ((maskVal.x & (1u << bitIdx)) != 0u) : ((maskVal.y & (1u << (bitIdx - 32))) != 0u);
                        
                        if (hasVoxel) {
                            int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                            uint m = GetVoxelData(chunkSlot, idx);
                            if (m != 0u) {
                                // Идеальное вычисление AABB 1x1 вокселя
                                if (length(vMask) < 0.5) tHit = tCurrent;
                                else {
                                    vec3 hitMin = chunkOrigin + vec3(vMapPos) / VOXELS_PER_METER;
                                    vec3 tEntry = min((hitMin - ro) * invRd, (hitMin + (1.0/VOXELS_PER_METER) - ro) * invRd);
                                    tHit = max(max(tEntry.x, tEntry.y), tEntry.z);
                                }
                                matID = m; 
                                normal = length(vMask) > 0.5 ? -vec3(vStepDir) * vMask : -normalize(safeRd);
                                return true;
                            }
                        }
                        if (vSideDist.x < vSideDist.y && vSideDist.x < vSideDist.z) { vMask = vec3(1,0,0); vSideDist.x += vDeltaDist.x; vMapPos.x += vStepDir.x; }
                        else if (vSideDist.y < vSideDist.z) { vMask = vec3(0,1,0); vSideDist.y += vDeltaDist.y; vMapPos.y += vStepDir.y; }
                        else { vMask = vec3(0,0,1); vSideDist.z += vDeltaDist.z; vMapPos.z += vStepDir.z; }
                    }
                }
            }
        }
        if (cSideDist.x < cSideDist.y && cSideDist.x < cSideDist.z) { cMask = vec3(1,0,0); cSideDist.x += cDeltaDist.x; cMapPos.x += cStepDir.x; }
        else if (cSideDist.y < cSideDist.z) { cMask = vec3(0,1,0); cSideDist.y += cDeltaDist.y; cMapPos.y += cStepDir.y; }
        else { cMask = vec3(0,0,1); cSideDist.z += cDeltaDist.z; cMapPos.z += cStepDir.z; }
        tCurrent = tDdaStart + dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, float tStart, inout float tHit, inout uint matID, inout vec3 normal) {
    int dummy = 0; return TraceStaticRay(ro, rd, maxDist, tStart, tHit, matID, normal, dummy);
}

// === TraceShadowRay ===
bool TraceShadowRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    vec3 safeRd = rd;
    if (abs(safeRd.x) < 1e-6) safeRd.x = (safeRd.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.y) < 1e-6) safeRd.y = (safeRd.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.z) < 1e-6) safeRd.z = (safeRd.z < 0.0) ? -1e-6 : 1e-6;
    vec3 invRd = 1.0 / safeRd;

    vec3 pStart = ro + safeRd * 0.001; // Защита от самопересечения
    
    vec3 rayPosChunk = pStart / float(CHUNK_SIZE);
    ivec3 cMapPos = ivec3(floor(rayPosChunk));
    vec3 cDeltaDist = abs(invRd) * float(CHUNK_SIZE);
    ivec3 cStepDir = ivec3(sign(safeRd));
    vec3 cSideDist = (sign(safeRd) * (vec3(cMapPos) - rayPosChunk) + (sign(safeRd) * 0.5 + 0.5)) * cDeltaDist;
    vec3 cMask = vec3(0);
    
    float tCurrentRel = 0.0;
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        if (tCurrentRel > maxDist) return false;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkSlot = texelFetch(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1), 0).r;

        if (chunkSlot != 0xFFFFFFFFu) {
            if ((chunkSlot & 0x80000000u) != 0u) {
                tHit = tCurrentRel; matID = chunkSlot & 0x7FFFFFFFu; return true;
            }

            #ifdef ENABLE_LOD
            bool isLodChunk = distance(uCamPos, (vec3(cMapPos) + 0.5) * float(CHUNK_SIZE)) > uLodDistance;
            #else
            bool isLodChunk = false;
            #endif

            uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 pLocal = (pStart + safeRd * tCurrentRel) - chunkOrigin;
            pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);

            if (isLodChunk) {
                vec3 rayPosBlock = pLocal / float(BLOCK_SIZE);
                ivec3 blockPos = ivec3(floor(rayPosBlock));
                blockPos = clamp(blockPos, ivec3(0), ivec3(BLOCKS_PER_AXIS - 1));
                vec3 bDeltaDist = abs(invRd) * float(BLOCK_SIZE);
                ivec3 bStepDir = cStepDir;
                vec3 bSideDist = (sign(safeRd) * (vec3(blockPos) - rayPosBlock) + (sign(safeRd) * 0.5 + 0.5)) * bDeltaDist;
                
                for (int bStep = 0; bStep < 64; bStep++) {
                    if (any(lessThan(blockPos, ivec3(0))) || any(greaterThanEqual(blockPos, ivec3(BLOCKS_PER_AXIS)))) break;
                    uint bIdx = uint(blockPos.x + BLOCKS_PER_AXIS * (blockPos.y + BLOCKS_PER_AXIS * blockPos.z));
                    uvec2 maskVal = packedMasks[maskBaseOffset + bIdx];
                    
                    if (maskVal.x != 0u || maskVal.y != 0u) {
                        tHit = tCurrentRel; matID = 1u; return true;
                    }
                    if (bSideDist.x < bSideDist.y && bSideDist.x < bSideDist.z) { bSideDist.x += bDeltaDist.x; blockPos.x += bStepDir.x; }
                    else if (bSideDist.y < bSideDist.z) { bSideDist.y += bDeltaDist.y; blockPos.y += bStepDir.y; }
                    else { bSideDist.z += bDeltaDist.z; blockPos.z += bStepDir.z; }
                }
            } else {
                vec3 rayPosVoxel = pLocal * VOXELS_PER_METER;
                ivec3 vMapPos = ivec3(floor(rayPosVoxel));
                vec3 vDeltaDist = abs(invRd) / VOXELS_PER_METER;
                ivec3 vStepDir = cStepDir;
                vec3 vSideDist = (sign(safeRd) * (vec3(vMapPos) - rayPosVoxel) + (sign(safeRd) * 0.5 + 0.5)) * vDeltaDist;
                vec3 vMask = cMask;

                for(int sanity = 0; sanity < 512; sanity++) {
                    if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                    ivec3 bMapPos = vMapPos >> 2;
                    uint bIdx = uint(bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z));
                    uvec2 maskVal = packedMasks[maskBaseOffset + bIdx];
                    
                    if (maskVal.x == 0u && maskVal.y == 0u) {
                        for(int s=0; s<64; s++) {
                            if (vSideDist.x < vSideDist.y && vSideDist.x < vSideDist.z) { 
                                vMask = vec3(1,0,0); vSideDist.x += vDeltaDist.x; vMapPos.x += vStepDir.x; 
                            }
                            else if (vSideDist.y < vSideDist.z) { 
                                vMask = vec3(0,1,0); vSideDist.y += vDeltaDist.y; vMapPos.y += vStepDir.y; 
                            }
                            else { 
                                vMask = vec3(0,0,1); vSideDist.z += vDeltaDist.z; vMapPos.z += vStepDir.z; 
                            }
                            if ((vMapPos >> 2) != bMapPos) break;
                            if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                        }
                    } else {
                        int lx = vMapPos.x & 3, ly = vMapPos.y & 3, lz = vMapPos.z & 3;
                        int bitIdx = lx | (ly << 2) | (lz << 4);
                        bool hasVoxel = (bitIdx < 32) ? ((maskVal.x & (1u << bitIdx)) != 0u) : ((maskVal.y & (1u << (bitIdx - 32))) != 0u);
                        
                        if (hasVoxel) {
                            int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                            uint m = GetVoxelData(chunkSlot, idx);
                            if (m != 0u) {
                                // Идеальное вычисление AABB 1x1 вокселя для теней
                                if (length(vMask) < 0.5) tHit = tCurrentRel;
                                else {
                                    vec3 hitMin = chunkOrigin + vec3(vMapPos) / VOXELS_PER_METER;
                                    vec3 tEntry = min((hitMin - pStart) * invRd, (hitMin + (1.0/VOXELS_PER_METER) - pStart) * invRd);
                                    tHit = max(max(tEntry.x, tEntry.y), tEntry.z);
                                }
                                matID = m;
                                return true;
                            }
                        }
                        if (vSideDist.x < vSideDist.y && vSideDist.x < vSideDist.z) { vMask = vec3(1,0,0); vSideDist.x += vDeltaDist.x; vMapPos.x += vStepDir.x; }
                        else if (vSideDist.y < vSideDist.z) { vMask = vec3(0,1,0); vSideDist.y += vDeltaDist.y; vMapPos.y += vStepDir.y; }
                        else { vMask = vec3(0,0,1); vSideDist.z += vDeltaDist.z; vMapPos.z += vStepDir.z; }
                    }
                }
            }
        }
        if (cSideDist.x < cSideDist.y && cSideDist.x < cSideDist.z) { cMask = vec3(1,0,0); cSideDist.x += cDeltaDist.x; cMapPos.x += cStepDir.x; }
        else if (cSideDist.y < cSideDist.z) { cMask = vec3(0,1,0); cSideDist.y += cDeltaDist.y; cMapPos.y += cStepDir.y; }
        else { cMask = vec3(0,0,1); cSideDist.z += cDeltaDist.z; cMapPos.z += cStepDir.z; }
        tCurrentRel = dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

bool TraceRefractionRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    return TraceShadowRay(ro, rd, maxDist, tHit, matID);
}