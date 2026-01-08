// --- START OF FILE include/tracing.glsl ---

// Хелпер: Пересечение с AABB
vec2 IntersectAABB(vec3 ro, vec3 invRd, vec3 boxMin, vec3 boxMax) {
    vec3 tMin = (boxMin - ro) * invRd;
    vec3 tMax = (boxMax - ro) * invRd;
    vec3 t1 = min(tMin, tMax);
    vec3 t2 = max(tMin, tMax);
    float tNear = max(max(t1.x, t1.y), t1.z);
    float tFar = min(min(t2.x, t2.y), t2.z);
    return vec2(tNear, tFar);
}

// Трассировка динамики
bool TraceDynamicRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout int outObjID, inout vec3 outLocalNormal) {
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
        if (any(lessThan(mapPos, ivec3(0))) || any(greaterThanEqual(mapPos, ivec3(uGridSize)))) break;
        int nodeIndex = imageLoad(uObjectGridHead, mapPos).r;
        while (nodeIndex > 0) {
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

// =============================================================================
// NESTED DDA (STATIC) - Stops at ANY solid voxel (including Water)
// =============================================================================
bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, float tStart, inout float tHit, inout uint matID, inout vec3 normal) {
    float tCurrent = max(0.0, tStart);
    vec3 rayOrigin = ro + rd * (tCurrent + 0.001);

    // --- OUTER DDA SETUP (Chunks) ---
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
        if (tCurrent > maxDist) break;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkIdx = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkIdx != 0xFFFFFFFFu) {
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 pLocal = (ro + rd * tCurrent) - chunkOrigin;

            // Коррекция входа в чанк (Snap to face)
            if (length(cMask) > 0.5) {
                if (cMask.x > 0.5) pLocal.x = (rd.x > 0.0) ? 0.0 : float(CHUNK_SIZE);
                if (cMask.y > 0.5) pLocal.y = (rd.y > 0.0) ? 0.0 : float(CHUNK_SIZE);
                if (cMask.z > 0.5) pLocal.z = (rd.z > 0.0) ? 0.0 : float(CHUNK_SIZE);
            }
            pLocal = clamp(pLocal, 0.0, float(CHUNK_SIZE) - 0.0001);

            vec3 pVoxel = pLocal * VOXELS_PER_METER;
            ivec3 vMapPos = ivec3(floor(pVoxel));

            // Коррекция входа в воксельную сетку
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

            for (int j = 0; j < VOXEL_RESOLUTION * 3; j++) {
                if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;

                int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                uint mat = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;

                if (mat != 0u) {
                    float tRelVoxel = 0.0;
                    if (length(vMask) > 0.5) {
                        tRelVoxel = dot(vMask, vSideDist - vDeltaDist);
                    }
                    tHit = tCurrent + (tRelVoxel / VOXELS_PER_METER);
                    matID = mat;

                    if (length(vMask) < 0.5) {
                        if (length(cMask) > 0.5) normal = -vec3(cStepDir) * cMask;
                        else normal = -vec3(vStepDir);
                    } else {
                        normal = -vec3(vStepDir) * vMask;
                    }
                    return true;
                }

                vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                vSideDist += vMask * vDeltaDist;
                vMapPos += ivec3(vMask) * vStepDir;
            }
        }

        cMask = (cSideDist.x < cSideDist.y) ? ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        cSideDist += cMask * cDeltaDist;
        cMapPos += ivec3(cMask) * cStepDir;
        tCurrent = tStart + dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

// =============================================================================
// SHADOW / REFRACTION TRACING (Optimized: Skips Water)
// =============================================================================
// Это общий метод для теней и преломления.
// Он игнорирует блок воды (ID 4), пропуская свет сквозь него.
// Использует пропуск чанков (Chunk Jumping).

bool TraceShadowRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    // Сдвиг для избежания самопересечения
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
        // Проверка границ мира, чтобы не читать мусор
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkIdx = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkIdx != 0xFFFFFFFFu) {
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

            for (int j = 0; j < VOXEL_RESOLUTION * 2; j++) {
                if (((vMapPos.x | vMapPos.y | vMapPos.z) & ~BIT_MASK) != 0) break;

                int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                uint m = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;

                // ВАЖНО: Игнорируем воду (4u) для теней и преломления
                if (m != 0u && m != 4u) {
                    float tRelVoxel = (length(vMask) > 0.5) ? dot(vMask, vSideDist - vDeltaDist) : 0.0;
                    tHit = tCurrentRel + (tRelVoxel / VOXELS_PER_METER);
                    matID = m;
                    return true;
                }

                vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                vSideDist += vMask * vDeltaDist;
                vMapPos += ivec3(vMask) * vStepDir;
            }
        }

        cMask = (cSideDist.x < cSideDist.y) ? ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        cSideDist += cMask * cDeltaDist;
        cMapPos += ivec3(cMask) * cStepDir;
        tCurrentRel = dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

// Алиас для обратной совместимости / читаемости
bool TraceRefractionRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    return TraceShadowRay(ro, rd, maxDist, tHit, matID);
}