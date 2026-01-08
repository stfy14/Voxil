// --- START OF FILE include/tracing.glsl ---

// Трассировка динамических объектов (без изменений)
bool TraceDynamicRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout int outObjID, inout vec3 outLocalNormal) {
    tHit = maxDist;
    bool hitAny = false;
    vec3 gridSpaceRo = (ro - uGridOrigin) / uGridStep;
    vec3 t0 = -gridSpaceRo / rd;
    vec3 t1 = (vec3(uGridSize) - gridSpaceRo) / rd;
    vec3 tmin = min(t0, t1), tmax = max(t0, t1);
    float tEnter = max(max(tmin.x, tmin.y), tmin.z);
    float tExit = min(min(tmax.x, tmax.y), tmax.z);

    if (tExit < max(0.0, tEnter) || tEnter > maxDist) return false;

    float tStart = max(0.0, tEnter);
    vec3 currPos = gridSpaceRo + rd * (tStart * uGridStep + 0.001);
    ivec3 mapPos = ivec3(floor(currPos));
    ivec3 stepDir = ivec3(sign(rd));
    vec3 deltaDist = abs(uGridStep / rd);
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

                vec3 invDir = 1.0 / localRd;
                vec3 t0_obj = (obj.boxMin.xyz - localRo) * invDir;
                vec3 t1_obj = (obj.boxMax.xyz - localRo) * invDir;
                vec3 tmin_obj = min(t0_obj, t1_obj);
                vec3 tmax_obj = max(t0_obj, t1_obj);
                float tNear = max(max(tmin_obj.x, tmin_obj.y), tmin_obj.z);
                float tFar = min(min(tmax_obj.x, tmax_obj.y), tmax_obj.z);

                if (tNear < tFar && tFar > 0.0 && tNear < tHit) {
                    tHit = tNear;
                    outObjID = int(objID) - 1;
                    vec3 n = step(tmin_obj.yzx, tmin_obj.xyz) * step(tmin_obj.zxy, tmin_obj.xyz);
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

// Трассировка статики с FACE SNAPPING (Решает проблему смещения вокселей)
bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID, inout vec3 normal) {
    ivec3 cMapPos = ivec3(floor(ro / float(CHUNK_SIZE)));
    vec3 cDeltaDist = abs(1.0 / rd) * float(CHUNK_SIZE);
    ivec3 cStepDir = ivec3(sign(rd));

    vec3 relPos = ro - (vec3(cMapPos) * float(CHUNK_SIZE));
    vec3 cSideDist;
    cSideDist.x = (rd.x > 0.0) ? (float(CHUNK_SIZE) - relPos.x) : relPos.x;
    cSideDist.y = (rd.y > 0.0) ? (float(CHUNK_SIZE) - relPos.y) : relPos.y;
    cSideDist.z = (rd.z > 0.0) ? (float(CHUNK_SIZE) - relPos.z) : relPos.z;
    cSideDist *= abs(1.0 / rd);

    vec3 cMask = vec3(0);
    float tCurrent = 0.0;

    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        if (tCurrent > maxDist) break;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        uint chunkIdx = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkIdx != 0xFFFFFFFFu) {
            // 1. Вычисляем "наивную" точку входа
            vec3 chunkOrigin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 entryPointGlobal = ro + rd * (tCurrent + 1e-5);
            vec3 entryPointLocal = entryPointGlobal - chunkOrigin;

            // 2. === FACE SNAPPING FIX ===
            // Если мы пришли в чанк из другого чанка (tCurrent > 0), 
            // мы ТОЧНО знаем, что находимся на границе.
            // Принудительно выставляем координату в 0.0 или CHUNK_SIZE, 
            // чтобы убить погрешность float.
            if (tCurrent > 0.0) {
                if (cMask.x > 0.5) entryPointLocal.x = (rd.x > 0.0) ? 0.0 : float(CHUNK_SIZE);
                if (cMask.y > 0.5) entryPointLocal.y = (rd.y > 0.0) ? 0.0 : float(CHUNK_SIZE);
                if (cMask.z > 0.5) entryPointLocal.z = (rd.z > 0.0) ? 0.0 : float(CHUNK_SIZE);
            }

            // Защита от NaN/Infinity при 4.0f
            entryPointLocal = clamp(entryPointLocal, 0.0, float(CHUNK_SIZE) - 0.001);

            // Переход в воксельные координаты
            vec3 pVoxel = entryPointLocal * VOXELS_PER_METER;
            ivec3 vMapPos = ivec3(floor(pVoxel));

            vec3 vDeltaDist = abs(1.0 / rd); // (1.0 / rd) * 1.0 (размер вокселя в grid space = 1)
            ivec3 vStepDir = cStepDir;

            vec3 vSideDist;
            vSideDist.x = (rd.x > 0.0) ? (float(vMapPos.x + 1) - pVoxel.x) : (pVoxel.x - float(vMapPos.x));
            vSideDist.y = (rd.y > 0.0) ? (float(vMapPos.y + 1) - pVoxel.y) : (pVoxel.y - float(vMapPos.y));
            vSideDist.z = (rd.z > 0.0) ? (float(vMapPos.z + 1) - pVoxel.z) : (pVoxel.z - float(vMapPos.z));
            vSideDist *= vDeltaDist;

            vec3 vMask = vec3(0);

            // Внутренний цикл
            for (int j = 0; j < VOXEL_RESOLUTION * 3; j++) {
                if (any(lessThan(vMapPos, ivec3(0))) || any(greaterThanEqual(vMapPos, ivec3(VOXEL_RESOLUTION)))) break;

                int idx = vMapPos.x + VOXEL_RESOLUTION * (vMapPos.y + VOXEL_RESOLUTION * vMapPos.z);
                uint mat = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;

                if (mat != 0u) {
                    float tRelVoxel = dot(vMask, vSideDist - vDeltaDist);
                    if (length(vMask) < 0.5) tRelVoxel = 0.0; // Hit first voxel

                    tHit = tCurrent + (tRelVoxel / VOXELS_PER_METER);
                    matID = mat;

                    if (length(vMask) < 0.5) {
                        if (tCurrent > 0.0) normal = -vec3(cStepDir) * cMask;
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

        tCurrent = dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

// Трассировка рефракции (оставляем старую, если работает, или меняем на TraceStaticRay при багах)
bool TraceRefractionRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    vec3 uVoxelRo = ro * VOXELS_PER_METER;
    ivec3 uMapPos = ivec3(floor(uVoxelRo));
    ivec3 uStepDir = ivec3(sign(rd));
    vec3 uDeltaDist = abs(1.0 / rd);

    vec3 vSideDist;
    vSideDist.x = (rd.x > 0.0) ? (float(uMapPos.x + 1) - uVoxelRo.x) : (uVoxelRo.x - float(uMapPos.x));
    vSideDist.y = (rd.y > 0.0) ? (float(uMapPos.y + 1) - uVoxelRo.y) : (uVoxelRo.y - float(uMapPos.y));
    vSideDist.z = (rd.z > 0.0) ? (float(uMapPos.z + 1) - uVoxelRo.z) : (uVoxelRo.z - float(uMapPos.z));
    vSideDist *= uDeltaDist;

    for (int k = 0; k < 300; k++) {
        vec3 uMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        float tVoxel = dot(uMask, vSideDist);
        float tMeters = tVoxel / VOXELS_PER_METER;

        if (tMeters > maxDist) return false;

        vSideDist += uMask * uDeltaDist;
        uMapPos += ivec3(uMask) * uStepDir;

        ivec3 chunkCoord = uMapPos >> BIT_SHIFT;
        uint chunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkIdx != 0xFFFFFFFFu) {
            ivec3 local = uMapPos & BIT_MASK;
            int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
            uint m = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;
            if (m != 0u && m != 4u) {
                tHit = tMeters;
                matID = m;
                return true;
            }
        }
    }
    return false;
}