// --- START OF FILE include/tracing.glsl ---

// Трассировка динамических объектов (из старого проекта)
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
                vec3 localN;
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

// Трассировка статики (из старого проекта)
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
        int chunkIdx = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;
        if (chunkIdx != -1) {
            vec3 pEntry = ro + rd * (tCurrent + 1e-5);
            vec3 pVoxel = pEntry * VOXELS_PER_METER;
            ivec3 vMapPos = ivec3(floor(pVoxel));
            if (tCurrent > 0.0) {
                if (cMask.x > 0.5) vMapPos.x = (rd.x > 0.0) ? (cMapPos.x * VOXEL_RESOLUTION) : (cMapPos.x * VOXEL_RESOLUTION + BIT_MASK);
                if (cMask.y > 0.5) vMapPos.y = (rd.y > 0.0) ? (cMapPos.y * VOXEL_RESOLUTION) : (cMapPos.y * VOXEL_RESOLUTION + BIT_MASK);
                if (cMask.z > 0.5) vMapPos.z = (rd.z > 0.0) ? (cMapPos.z * VOXEL_RESOLUTION) : (cMapPos.z * VOXEL_RESOLUTION + BIT_MASK);
            }
            vec3 vDeltaDist = abs(1.0 / rd);
            ivec3 vStepDir = cStepDir;
            vec3 vSideDist;
            vSideDist.x = (rd.x > 0.0) ? (float(vMapPos.x + 1) - pVoxel.x) : (pVoxel.x - float(vMapPos.x));
            vSideDist.y = (rd.y > 0.0) ? (float(vMapPos.y + 1) - pVoxel.y) : (pVoxel.y - float(vMapPos.y));
            vSideDist.z = (rd.z > 0.0) ? (float(vMapPos.z + 1) - pVoxel.z) : (pVoxel.z - float(vMapPos.z));
            vSideDist *= vDeltaDist;
            vec3 vMask = vec3(0);
            for (int j = 0; j < 512; j++) {
                if ((vMapPos.x >> BIT_SHIFT) != cMapPos.x || (vMapPos.y >> BIT_SHIFT) != cMapPos.y || (vMapPos.z >> BIT_SHIFT) != cMapPos.z) break;
                ivec3 local = vMapPos & BIT_MASK;
                int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
                uint mat = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;
                if (mat != 0u) {
                    float tVoxel = dot(vMask, vSideDist - vDeltaDist);
                    if (length(vMask) < 0.5) tVoxel = 0.0;
                    tHit = tCurrent + (tVoxel / VOXELS_PER_METER);
                    matID = mat;
                    if (length(vMask) < 0.5) {
                        if (tCurrent > 0.0) normal = -vec3(cStepDir) * cMask; else normal = -vec3(vStepDir);
                    } else normal = -vec3(vStepDir) * vMask;
                    return true;
                }
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

// --- ТРАССИРОВКА РЕФРАКЦИИ (С ИСПРАВЛЕНИЕМ) ---
bool TraceRefractionRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    vec3 uVoxelRo = ro * VOXELS_PER_METER;
    ivec3 uMapPos = ivec3(floor(uVoxelRo));
    ivec3 uStepDir = ivec3(sign(rd));

    // --- КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ ЗДЕСЬ ---
    // Шаг луча (delta) должен быть отмасштабирован так же, как и в TraceStaticRay.
    // vDeltaDist измеряется в "единицах t на воксель", а не "на метр".
    vec3 uDeltaDist = abs(1.0 / rd); // Это шаг на 1 метр.

    vec3 vSideDist; // Расстояние до следующей грани вокселя
    vSideDist.x = (rd.x > 0.0) ? (float(uMapPos.x + 1) - uVoxelRo.x) : (uVoxelRo.x - float(uMapPos.x));
    vSideDist.y = (rd.y > 0.0) ? (float(uMapPos.y + 1) - uVoxelRo.y) : (uVoxelRo.y - float(uMapPos.y));
    vSideDist.z = (rd.z > 0.0) ? (float(uMapPos.z + 1) - uVoxelRo.z) : (uVoxelRo.z - float(uMapPos.z));
    vSideDist *= uDeltaDist;

    for (int k = 0; k < 300; k++) {
        vec3 uMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));

        // Дистанция в вокселях до пересечения
        float tVoxel = dot(uMask, vSideDist);
        // Дистанция в метрах
        float tMeters = tVoxel / VOXELS_PER_METER;

        if (tMeters > maxDist) return false;

        // Шаг вперед
        vSideDist += uMask * uDeltaDist;
        uMapPos += ivec3(uMask) * uStepDir;

        ivec3 chunkCoord = uMapPos >> BIT_SHIFT;
        int chunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkIdx != -1) {
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