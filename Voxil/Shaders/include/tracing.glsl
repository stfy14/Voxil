layout(std430, binding = 5) buffer MaskSSBO { uvec2 packedMasks[]; };
layout(std430, binding = 7) buffer DynSVOBuffer { uvec4 svoNodes[]; };

// ВСТАВКА КОДА БАНКОВ (БЛОЧНЫЙ КОММЕНТАРИЙ)
/*__BANKS_INJECTION__*/

#define BLOCK_SIZE 4
#define BLOCKS_PER_AXIS (VOXEL_RESOLUTION / BLOCK_SIZE) 
#define VOXELS_IN_UINT 4
#define SVO_STACK_SIZE 24  // <-- ИДЕАЛЬНЫЙ РАЗМЕР. Решает проблему регистрового давления GPU!

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

uint TraceSVO(
    vec3  localRo,
    vec3  localRd,
    uint  svoOffset,
    uint  gridSize,
    float voxelSize,
inout float tHit,
out   vec3  outNormal,
inout int   steps)
{
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

    uint stkNode  [SVO_STACK_SIZE];
    uint stkPacked[SVO_STACK_SIZE];
    int  top = 0;

    stkNode  [0] = svoOffset;
    stkPacked[0] = 0u;

    float bestT   = tHit;
    uint  bestMat = 0u;
    vec3  bestN   = vec3(0.0);

    uint nx, ny, nz, depth;
    uint halfVoxels;
    float nSize, halfSize;
    uint nextDepth;

    while (top >= 0)
    {
        steps++;

        uint nodeIdx = stkNode[top];
        uint packedData = stkPacked[top];
        top--;

        nx    =  packedData         & 0xFFu;
        ny    = (packedData >>  8u) & 0xFFu;
        nz    = (packedData >> 16u) & 0xFFu;
        depth = (packedData >> 24u) & 0x0Fu;

        nSize = float(gridSize >> depth) * voxelSize;
        vec3  nMin  = vec3(float(nx), float(ny), float(nz)) * voxelSize;
        vec3  nMax  = nMin + nSize;

        vec2 tBox = IntersectAABB(localRo, invRd, nMin, nMax);
        if (tBox.x > tBox.y || tBox.y < 0.0 || tBox.x >= bestT) continue;

        uvec4 node        = svoNodes[nodeIdx];
        uint  childMask   = node.x;
        uint  childOffset = node.y;
        uint  material    = node.z;

        if (childMask == 0u)
        {
            if (material != 0u)
            {
                float t = max(tBox.x, 0.0);
                if (t < bestT)
                {
                    bestT   = t;
                    bestMat = material;

                    vec3 tNears = min((nMin - localRo) * invRd, (nMax - localRo) * invRd);
                    float eps = 1e-4 * (1.0 + abs(tBox.x));
                    if      (abs(tNears.x - tBox.x) < eps) bestN = vec3(-sign(safeRd.x), 0.0, 0.0);
                    else if (abs(tNears.y - tBox.x) < eps) bestN = vec3(0.0, -sign(safeRd.y), 0.0);
                    else                                   bestN = vec3(0.0, 0.0, -sign(safeRd.z));
                }
            }
            continue;
        }

        halfVoxels = gridSize >> (depth + 1u);
        halfSize   = nSize * 0.5;
        nextDepth  = depth + 1u;

        for (int i = 7; i >= 0; i--)
        {
            if ((childMask & (1u << i)) == 0u) continue;
            if (top + 1 >= SVO_STACK_SIZE) break;

            uint cNx = nx + ((uint(i) & 1u) != 0u ? halfVoxels : 0u);
            uint cNy = ny + ((uint(i) & 2u) != 0u ? halfVoxels : 0u);
            uint cNz = nz + ((uint(i) & 4u) != 0u ? halfVoxels : 0u);

            vec3 cMin = vec3(float(cNx), float(cNy), float(cNz)) * voxelSize;
            vec2 ct   = IntersectAABB(localRo, invRd, cMin, cMin + halfSize);

            if (ct.x > ct.y || ct.y < 0.0 || ct.x >= bestT) continue;

            uint localIdx = uint(bitCount(childMask & ((1u << uint(i)) - 1u)));

            top++;
            stkNode  [top] = childOffset + localIdx;
            stkPacked[top] = cNx | (cNy << 8u) | (cNz << 16u) | (nextDepth << 24u);
        }
    }

    if (bestMat != 0u) { tHit = bestT; outNormal = bestN; }
    return bestMat;
}

bool TraceDynamicRay(
    vec3 ro, vec3 rd, float maxDist,
    inout float tHit, inout int outObjID,
    inout vec3 outLocalNormal,
    out uint outMatID,       // ← НОВОЕ
    inout int steps)
{
    tHit = maxDist;
    outMatID = 0u;
    bool hitAny = false;

    // Универсальная защита
    vec3 safeRd = rd;
    if (abs(safeRd.x) < 1e-6) safeRd.x = (safeRd.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.y) < 1e-6) safeRd.y = (safeRd.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.z) < 1e-6) safeRd.z = (safeRd.z < 0.0) ? -1e-6 : 1e-6;

    vec3 gridMin = uGridOrigin;
    vec3 gridMax = uGridOrigin + vec3(uGridSize) * uGridStep;

    vec3 invRd = 1.0 / safeRd;
    vec3 t0 = (gridMin - ro) * invRd;
    vec3 t1 = (gridMax - ro) * invRd;

    vec3 tsmaller = min(t0, t1);
    vec3 tbigger  = max(t0, t1);

    float tEnter = max(max(tsmaller.x, tsmaller.y), tsmaller.z);
    float tExit  = min(min(tbigger.x, tbigger.y), tbigger.z);

    if (tExit < max(0.0, tEnter) || tEnter > maxDist) return false;

    float tCurrent = max(0.0, tEnter) + 0.001;
    float tDdaStart = tCurrent;

    vec3 rayOriginWorld = ro + safeRd * tCurrent;
    rayOriginWorld = clamp(rayOriginWorld, gridMin + 0.001, gridMax - 0.001);

    ivec3 mapPos = ivec3(floor((rayOriginWorld - uGridOrigin) / uGridStep));

    vec3 deltaDist = abs(1.0 / safeRd) * uGridStep;
    ivec3 stepDir = ivec3(sign(safeRd));
    vec3 relPos = rayOriginWorld - (uGridOrigin + vec3(mapPos) * uGridStep);

    vec3 sideDist;
    sideDist.x = (safeRd.x > 0.0) ? (uGridStep - relPos.x) : relPos.x;
    sideDist.y = (safeRd.y > 0.0) ? (uGridStep - relPos.y) : relPos.y;
    sideDist.z = (safeRd.z > 0.0) ? (uGridStep - relPos.z) : relPos.z;
    sideDist *= abs(1.0 / safeRd);

    vec3 mask = vec3(0);

    for (int safetyLoop = 0; safetyLoop < 512; safetyLoop++) {
        steps++;

        if (any(lessThan(mapPos, ivec3(0))) || any(greaterThanEqual(mapPos, ivec3(uGridSize)))) break;
        if (tCurrent > maxDist) break;

        int nodeIndex = imageLoad(uObjectGridHead, mapPos).r;

        while (nodeIndex > 0) {
            steps++;
            int bufferIdx = nodeIndex - 1;
            uint objID = listNodes[bufferIdx].objectID;
            nodeIndex = listNodes[bufferIdx].nextNode;

            if (objID > 0u) {
                DynamicObject obj = dynObjects[int(objID) - 1];

                vec3 localRo = (obj.invModel * vec4(ro, 1.0)).xyz;
                vec3 localRd = (obj.invModel * vec4(safeRd, 0.0)).xyz;

                vec3 sLocalRd = localRd;
                if (abs(sLocalRd.x) < 1e-6) sLocalRd.x = (sLocalRd.x < 0.0) ? -1e-6 : 1e-6;
                if (abs(sLocalRd.y) < 1e-6) sLocalRd.y = (sLocalRd.y < 0.0) ? -1e-6 : 1e-6;
                if (abs(sLocalRd.z) < 1e-6) sLocalRd.z = (sLocalRd.z < 0.0) ? -1e-6 : 1e-6;

                vec3 lInvRd = 1.0 / sLocalRd;
                vec2 tBox = IntersectAABB(localRo, lInvRd, obj.boxMin.xyz, obj.boxMax.xyz);

                if (tBox.x <= tBox.y && tBox.y > 0.0 && tBox.x < tHit)
                {
                    if (obj.gridSize == 0u || obj.voxelSize == 0.0) continue;

                    float svoT = tHit;
                    vec3  svoNormal;

                    if (obj.gridSize == 0u) continue; // SVO ещё не готово, пропускаем
                    if (obj.svoOffset == 0xFFFFFFFFu) continue; // Sentinel = не инициализировано

                    uint hitMat = TraceSVO(
                        localRo, sLocalRd,
                        obj.svoOffset,
                        obj.gridSize,
                        obj.voxelSize,
                        svoT, svoNormal, steps);

                    if (hitMat != 0u)
                    {
                        tHit = svoT;
                        outObjID = int(objID) - 1;
                        outLocalNormal = normalize((transpose(obj.invModel) * vec4(svoNormal, 0.0)).xyz);
                        outMatID = hitMat;   // ← НОВОЕ: сохраняем материал
                        hitAny = true;
                    }
                }
            }
        }

        mask = (sideDist.x < sideDist.y) ? ((sideDist.x < sideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((sideDist.y < sideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        sideDist += mask * deltaDist;
        mapPos += ivec3(mask) * stepDir;
        tCurrent = tDdaStart + dot(mask, sideDist - deltaDist);
    }
    return hitAny;
}

// Обновляем перегрузку без шагов
bool TraceDynamicRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout int outObjID, inout vec3 outLocalNormal) {
    int dummy = 0; uint dummyMat = 0u;
    return TraceDynamicRay(ro, rd, maxDist, tHit, outObjID, outLocalNormal, dummyMat, dummy);
}

// === TraceStaticRay ===
bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, float tStart, inout float tHit, inout uint matID, inout vec3 normal, inout int steps) {
    // УНИВЕРСАЛЬНАЯ ЗАЩИТА (Спасает от зависаний)
    vec3 safeRd = rd;
    if (abs(safeRd.x) < 1e-6) safeRd.x = (safeRd.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.y) < 1e-6) safeRd.y = (safeRd.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.z) < 1e-6) safeRd.z = (safeRd.z < 0.0) ? -1e-6 : 1e-6;

    vec3 worldMin = vec3(uBoundMinX, uBoundMinY, uBoundMinZ) * float(CHUNK_SIZE);
    vec3 worldMax = vec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * float(CHUNK_SIZE);

    float tCurrent = max(0.0, tStart);

    vec3 invRd = 1.0 / safeRd;
    vec3 t0s = (worldMin - ro) * invRd;
    vec3 t1s = (worldMax - ro) * invRd;

    vec3 tsmaller = min(t0s, t1s);
    vec3 tbigger  = max(t0s, t1s);

    float tMin = max(max(tsmaller.x, tsmaller.y), tsmaller.z);
    float tMax = min(min(tbigger.x,  tbigger.y),  tbigger.z);

    if (tMin > tMax || tMax < 0.0) return false;

    if (tMin > tCurrent) {
        tCurrent = tMin + 0.001;
    }

    float tDdaStart = tCurrent;

    vec3 rayOrigin = ro + safeRd * tCurrent;
    vec3 clampMin = worldMin + 0.001;
    vec3 clampMax = worldMax - 0.001;
    rayOrigin = clamp(rayOrigin, clampMin, clampMax);

    ivec3 cMapPos = ivec3(floor(rayOrigin / float(CHUNK_SIZE)));

    vec3 cDeltaDist = abs(1.0 / safeRd) * float(CHUNK_SIZE);
    ivec3 cStepDir = ivec3(sign(safeRd));
    vec3 relPos = rayOrigin - (vec3(cMapPos) * float(CHUNK_SIZE));
    vec3 cSideDist;
    cSideDist.x = (safeRd.x > 0.0) ? (float(CHUNK_SIZE) - relPos.x) : relPos.x;
    cSideDist.y = (safeRd.y > 0.0) ? (float(CHUNK_SIZE) - relPos.y) : relPos.y;
    cSideDist.z = (safeRd.z > 0.0) ? (float(CHUNK_SIZE) - relPos.z) : relPos.z;
    cSideDist *= abs(1.0 / safeRd);
    vec3 cMask = vec3(0);
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        steps++;
        if (tCurrent > maxDist) break;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkSlot = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkSlot != 0xFFFFFFFFu) {

            if ((chunkSlot & 0x80000000u) != 0u) {
                tHit = tCurrent;
                matID = chunkSlot & 0x7FFFFFFFu;
                if (length(cMask) > 0.5) normal = -vec3(cStepDir) * cMask;
                else normal = -vec3(cStepDir);
                return true;
            }

            #ifdef ENABLE_LOD
                vec3 chunkCenter = (vec3(cMapPos) + 0.5) * float(CHUNK_SIZE);
            float distToChunk = distance(uCamPos, chunkCenter);
            bool isLodChunk = (distToChunk > uLodDistance);
            #else
                bool isLodChunk = false;
            #endif

            uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);

            bool processedAsLod = false;

            #ifdef ENABLE_LOD
            if (isLodChunk) {
                processedAsLod = true;

                vec3 pLocal = (ro + safeRd * tCurrent) - chunkOrigin;
                if (length(cMask) > 0.5) {
                    if (cMask.x > 0.5) pLocal.x = (safeRd.x > 0.0) ? 0.0 : float(CHUNK_SIZE);
                    if (cMask.y > 0.5) pLocal.y = (safeRd.y > 0.0) ? 0.0 : float(CHUNK_SIZE);
                    if (cMask.z > 0.5) pLocal.z = (safeRd.z > 0.0) ? 0.0 : float(CHUNK_SIZE);
                }
                pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);

                ivec3 blockPos = ivec3(floor(pLocal / float(BLOCK_SIZE)));
                blockPos = clamp(blockPos, ivec3(0), ivec3(BLOCKS_PER_AXIS - 1));

                vec3 bDeltaDist = abs(1.0 / safeRd) * float(BLOCK_SIZE);
                ivec3 bStepDir = cStepDir;
                vec3 blockRelPos = pLocal - (vec3(blockPos) * float(BLOCK_SIZE));
                vec3 bSideDist;
                bSideDist.x = (safeRd.x > 0.0) ? (float(BLOCK_SIZE) - blockRelPos.x) : blockRelPos.x;
                bSideDist.y = (safeRd.y > 0.0) ? (float(BLOCK_SIZE) - blockRelPos.y) : blockRelPos.y;
                bSideDist.z = (safeRd.z > 0.0) ? (float(BLOCK_SIZE) - blockRelPos.z) : blockRelPos.z;
                bSideDist *= abs(1.0 / safeRd);
                vec3 bMask = cMask;

                for (int bStep = 0; bStep < 64; bStep++) {
                    if (any(lessThan(blockPos, ivec3(0))) || any(greaterThanEqual(blockPos, ivec3(BLOCKS_PER_AXIS)))) break;

                    int blockIdx = blockPos.x + BLOCKS_PER_AXIS * (blockPos.y + BLOCKS_PER_AXIS * blockPos.z);
                    uvec2 maskVal = packedMasks[maskBaseOffset + uint(blockIdx)];

                    if (maskVal.x != 0u || maskVal.y != 0u) {
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
                            vec3 blockMinWorld = chunkOrigin + vec3(blockOrigin);
                            vec3 blockMaxWorld = blockMinWorld + vec3(BLOCK_SIZE);

                            vec3 blockInvRd = 1.0 / safeRd;
                            vec3 bt0 = (blockMinWorld - ro) * blockInvRd;
                            vec3 bt1 = (blockMaxWorld - ro) * blockInvRd;
                            vec3 btNear = min(bt0, bt1);
                            vec3 btFar = max(bt0, bt1);
                            float blockTNear = max(max(btNear.x, btNear.y), btNear.z);
                            float blockTFar = min(min(btFar.x, btFar.y), btFar.z);

                            if (blockTNear <= blockTFar && blockTFar > 0.0) {
                                tHit = max(tCurrent, blockTNear);
                                matID = lodMatID;

                                vec3 tNearComponents = btNear;
                                normal = vec3(0.0);
                                float epsilon = 0.001;
                                if (abs(tNearComponents.x - blockTNear) < epsilon) normal.x = -sign(safeRd.x);
                                else if (abs(tNearComponents.y - blockTNear) < epsilon) normal.y = -sign(safeRd.y);
                                else if (abs(tNearComponents.z - blockTNear) < epsilon) normal.z = -sign(safeRd.z);

                                if (length(normal) < 0.5) normal = -normalize(safeRd);
                                return true;
                            }
                        }
                    }

                    bMask = (bSideDist.x < bSideDist.y)
                    ? ((bSideDist.x < bSideDist.z) ? vec3(1,0,0) : vec3(0,0,1))
                    : ((bSideDist.y < bSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                    bSideDist += bMask * bDeltaDist;
                    blockPos += ivec3(bMask) * bStepDir;
                }
            }
            #endif

            if (!processedAsLod) {

                vec3 pLocal = (ro + safeRd * tCurrent) - chunkOrigin;
                if (length(cMask) > 0.5) {
                    if (cMask.x > 0.5) pLocal.x = (safeRd.x > 0.0) ? 0.0 : float(CHUNK_SIZE);
                    if (cMask.y > 0.5) pLocal.y = (safeRd.y > 0.0) ? 0.0 : float(CHUNK_SIZE);
                    if (cMask.z > 0.5) pLocal.z = (safeRd.z > 0.0) ? 0.0 : float(CHUNK_SIZE);
                }
                pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);
                vec3 pVoxel = pLocal * VOXELS_PER_METER;
                ivec3 vMapPos = ivec3(floor(pVoxel));
                if (length(cMask) > 0.5) {
                    if (cMask.x > 0.5 && safeRd.x < 0.0) vMapPos.x = VOXEL_RESOLUTION - 1;
                    if (cMask.y > 0.5 && safeRd.y < 0.0) vMapPos.y = VOXEL_RESOLUTION - 1;
                    if (cMask.z > 0.5 && safeRd.z < 0.0) vMapPos.z = VOXEL_RESOLUTION - 1;
                }

                vec3 vDeltaDist = abs(1.0 / safeRd);
                ivec3 vStepDir = cStepDir;
                vec3 vSideDist;
                vSideDist.x = (safeRd.x > 0.0) ? (float(vMapPos.x + 1) - pVoxel.x) : (pVoxel.x - float(vMapPos.x));
                vSideDist.y = (safeRd.y > 0.0) ? (float(vMapPos.y + 1) - pVoxel.y) : (pVoxel.y - float(vMapPos.y));
                vSideDist.z = (safeRd.z > 0.0) ? (float(vMapPos.z + 1) - pVoxel.z) : (pVoxel.z - float(vMapPos.z));
                vSideDist *= vDeltaDist;
                vec3 vMask = cMask;

                bool exitChunk = false;

                for (int sanityChunk = 0; sanityChunk < 512; sanityChunk++) {
                    if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) { exitChunk = true; break; }

                    ivec3 bMapPos = vMapPos / BLOCK_SIZE;
                    int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);
                    uvec2 maskVal = packedMasks[maskBaseOffset + uint(blockIdx)];

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

        tCurrent = tDdaStart + dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, float tStart, inout float tHit, inout uint matID, inout vec3 normal) {
    int dummy = 0; return TraceStaticRay(ro, rd, maxDist, tStart, tHit, matID, normal, dummy);
}

// === TraceShadowRay ===
bool TraceShadowRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    // УНИВЕРСАЛЬНАЯ ЗАЩИТА ТЕНЕВЫХ ЛУЧЕЙ ОТ ЗАВИСАНИЯ (Именно из-за них падало ФПС)
    vec3 safeRd = rd;
    if (abs(safeRd.x) < 1e-6) safeRd.x = (safeRd.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.y) < 1e-6) safeRd.y = (safeRd.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(safeRd.z) < 1e-6) safeRd.z = (safeRd.z < 0.0) ? -1e-6 : 1e-6;

    vec3 pStart = ro + safeRd * 0.001;
    ivec3 cMapPos = ivec3(floor(pStart / float(CHUNK_SIZE)));
    vec3 cDeltaDist = abs(1.0 / safeRd) * float(CHUNK_SIZE);
    ivec3 cStepDir = ivec3(sign(safeRd));
    vec3 relPos = pStart - (vec3(cMapPos) * float(CHUNK_SIZE));
    vec3 cSideDist;
    cSideDist.x = (safeRd.x > 0.0) ? (float(CHUNK_SIZE) - relPos.x) : relPos.x;
    cSideDist.y = (safeRd.y > 0.0) ? (float(CHUNK_SIZE) - relPos.y) : relPos.y;
    cSideDist.z = (safeRd.z > 0.0) ? (float(CHUNK_SIZE) - relPos.z) : relPos.z;
    cSideDist *= abs(1.0 / safeRd);
    vec3 cMask = vec3(0);
    float tCurrentRel = 0.0;
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        if (tCurrentRel > maxDist) return false;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkSlot = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkSlot != 0xFFFFFFFFu) {

            if ((chunkSlot & 0x80000000u) != 0u) {
                tHit = tCurrentRel;
                matID = chunkSlot & 0x7FFFFFFFu;
                return true;
            }

            #ifdef ENABLE_LOD
                vec3 chunkCenter = (vec3(cMapPos) + 0.5) * float(CHUNK_SIZE);
            float distToChunk = distance(uCamPos, chunkCenter);
            bool isLodChunk = (distToChunk > uLodDistance);
            #else
                bool isLodChunk = false;
            #endif

            uint maskBaseOffset = chunkSlot * (uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS)*uint(BLOCKS_PER_AXIS));
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 pLocal = (pStart + safeRd * tCurrentRel) - chunkOrigin;

            if (length(cMask) > 0.5) {
                if (cMask.x > 0.5) pLocal.x = (safeRd.x > 0.0) ? 0.0 : float(CHUNK_SIZE);
                if (cMask.y > 0.5) pLocal.y = (safeRd.y > 0.0) ? 0.0 : float(CHUNK_SIZE);
                if (cMask.z > 0.5) pLocal.z = (safeRd.z > 0.0) ? 0.0 : float(CHUNK_SIZE);
            }
            pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);
            vec3 pVoxel = pLocal * VOXELS_PER_METER;
            ivec3 vMapPos = ivec3(floor(pVoxel));
            if (length(cMask) > 0.5) {
                if (cMask.x > 0.5 && safeRd.x < 0.0) vMapPos.x = VOXEL_RESOLUTION - 1;
                if (cMask.y > 0.5 && safeRd.y < 0.0) vMapPos.y = VOXEL_RESOLUTION - 1;
                if (cMask.z > 0.5 && safeRd.z < 0.0) vMapPos.z = VOXEL_RESOLUTION - 1;
            }

            vec3 vDeltaDist = abs(1.0 / safeRd);
            ivec3 vStepDir = cStepDir;
            vec3 vSideDist;
            vSideDist.x = (safeRd.x > 0.0) ? (float(vMapPos.x + 1) - pVoxel.x) : (pVoxel.x - float(vMapPos.x));
            vSideDist.y = (safeRd.y > 0.0) ? (float(vMapPos.y + 1) - pVoxel.y) : (pVoxel.y - float(vMapPos.y));
            vSideDist.z = (safeRd.z > 0.0) ? (float(vMapPos.z + 1) - pVoxel.z) : (pVoxel.z - float(vMapPos.z));
            vSideDist *= vDeltaDist;
            vec3 vMask = vec3(0);

            if (length(cMask) > 0.5) vMask = cMask;

            bool exitChunk = false;

            for(int sanity = 0; sanity < 512; sanity++) {
                if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;
                ivec3 bMapPos = vMapPos / BLOCK_SIZE;
                int blockIdx = bMapPos.x + BLOCKS_PER_AXIS * (bMapPos.y + BLOCKS_PER_AXIS * bMapPos.z);
                uvec2 maskVal = packedMasks[maskBaseOffset + uint(blockIdx)];

                if (maskVal.x == 0u && maskVal.y == 0u) {
                    for(int s=0; s<64; s++) {
                        vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                        vSideDist += vMask * vDeltaDist;
                        vMapPos += ivec3(vMask) * vStepDir;
                        if (vMapPos / BLOCK_SIZE != bMapPos) break;
                        if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) { exitChunk = true; break; }
                    }
                } else {
                    bool processedAsLodShadow = false;
                    #ifdef ENABLE_LOD
                        if (isLodChunk) {
                        processedAsLodShadow = true;

                        ivec3 blockOrigin = bMapPos * BLOCK_SIZE;
                        vec3 blockMinWorld = chunkOrigin + vec3(blockOrigin);
                        vec3 blockMaxWorld = blockMinWorld + vec3(BLOCK_SIZE);

                        vec3 invRd = 1.0 / safeRd;
                        vec3 t0 = (blockMinWorld - pStart) * invRd;
                        vec3 t1 = (blockMaxWorld - pStart) * invRd;
                        vec3 tNearVec = min(t0, t1);
                        vec3 tFarVec = max(t0, t1);

                        float tNear = max(max(tNearVec.x, tNearVec.y), tNearVec.z);
                        float tFar = min(min(tFarVec.x, tFarVec.y), tFarVec.z);

                        if (tNear <= tFar && tFar > 0.0 && tNear < maxDist) {
                            tHit = tCurrentRel + tNear;
                            matID = 1u;
                            return true;
                        }

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
                    }
                    #endif

                    if (!processedAsLodShadow) {
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