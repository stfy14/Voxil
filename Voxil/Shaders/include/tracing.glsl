// --- START OF FILE include/tracing.glsl ---

struct HitResult {
    bool isHit;
    float t;
    vec3 normal;
    uint materialID;
    bool isDynamic;
    int objID;
};

// Хелпер для пересечения с AABB
// Возвращает vec2(tNear, tFar) и записывает нормаль входа в outNormal
vec2 IntersectAABB_Obj(vec3 ro, vec3 rd, vec3 bmin, vec3 bmax, out vec3 outNormal) {
    vec3 invDir = 1.0 / rd;
    vec3 t0 = (bmin - ro) * invDir, t1 = (bmax - ro) * invDir;
    vec3 tmin_vec = min(t0, t1), tmax_vec = max(t0, t1);

    // tNear - это максимум из минимумов
    float tNear = max(max(tmin_vec.x, tmin_vec.y), tmin_vec.z);
    // tFar - это минимум из максимумов
    float tFar = min(min(tmax_vec.x, tmax_vec.y), tmax_vec.z);

    // Расчет нормали в точке входа (tNear)
    // Step возвращает 1.0 если a < b.
    // Логика: определяем, какая ось дала tNear.
    vec3 n = step(tmin_vec.yzx, tmin_vec.xyz) * step(tmin_vec.zxy, tmin_vec.xyz);
    outNormal = -n * sign(rd);

    return vec2(tNear, tFar);
}

// --- СТАТИЧЕСКАЯ ТРАССИРОВКА (NORMAL BIAS FIX + OPTIMIZED) ---
bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID, inout vec3 normal) {
    // Предрасчёт для оптимизации (избегаем повторных вычислений)
    vec3 invRd = 1.0 / rd;
    vec3 rdSign = sign(rd);
    ivec3 cStepDir = ivec3(rdSign);
    vec3 absInvRd = abs(invRd);

    ivec3 cMapPos = ivec3(floor(ro / float(CHUNK_SIZE)));
    // DDA для чанков (внешний цикл)
    vec3 cDeltaDist = absInvRd * float(CHUNK_SIZE);
    vec3 cSideDist = (rdSign * (vec3(cMapPos) * float(CHUNK_SIZE) - ro) + (rdSign * 0.5 + 0.5) * float(CHUNK_SIZE)) * absInvRd;
    vec3 cMask = vec3(0);
    float tCurrent = 0.0;

    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        if (tCurrent > maxDist) return false;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) return false;

        int chunkIdx = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkIdx != -1) {
            // 1. Считаем границы чанка
            vec3 chunkMin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 chunkMax = chunkMin + float(CHUNK_SIZE);

            // 2. Точное пересечение с AABB чанка (используем предрасчитанные значения)
            vec3 t0 = (chunkMin - ro) * invRd;
            vec3 t1 = (chunkMax - ro) * invRd;
            vec3 tmin_vec = min(t0, t1);
            vec3 tmax_vec = max(t0, t1);
            float tNear = max(max(tmin_vec.x, tmin_vec.y), tmin_vec.z);
            float tFar = min(min(tmax_vec.x, tmax_vec.y), tmax_vec.z);

            // Расчет нормали входа
            vec3 n = step(tmin_vec.yzx, tmin_vec.xyz) * step(tmin_vec.zxy, tmin_vec.xyz);
            vec3 boxNormal = -n * rdSign;

            // tNear может быть отрицательным, если мы внутри чанка.
            // Но нам важно, есть ли валидный отрезок пересечения.
            if (tFar >= tNear && tFar > 0.0) {
                float tStart = max(0.0, tNear);
                float tEnd = tFar;

                // 3. Вычисляем точку старта
                vec3 pEntry = ro + rd * tStart;

                // === ГЛАВНЫЙ ФИКС ШВОВ (NORMAL BIAS) ===
                // Если мы вошли снаружи (tNear >= 0), сдвигаем точку внутрь 
                // строго ПРОТИВ нормали грани. Это надежнее, чем двигать по лучу.
                if (tNear >= 0.0) {
                    pEntry -= boxNormal * 0.0005;
                }
                // Если мы внутри (tNear < 0), pEntry = ro, ничего не сдвигаем.

                // 4. Получаем координату вокселя
                ivec3 vMapPos = ivec3(floor(pEntry * VOXELS_PER_METER));

                // 5. HARD CLAMP (Защита от вылета за границы чанка из-за float error)
                ivec3 chunkBaseVox = cMapPos * VOXEL_RESOLUTION;
                vMapPos = clamp(vMapPos, chunkBaseVox, chunkBaseVox + ivec3(VOXEL_RESOLUTION - 1));

                // 6. DDA Внутри вокселей (используем предрасчитанные значения)
                vec3 vDeltaDist = absInvRd;
                ivec3 vStepDir = cStepDir;
                // Считаем DDA от скорректированной точки входа pEntry (или ro)
                vec3 vSideDist = (rdSign * (vec3(vMapPos) - pEntry * VOXELS_PER_METER) + (rdSign * 0.5 + 0.5)) * vDeltaDist;

                // Добавляем tStart, чтобы перевести локальное время вокселей в глобальное t
                vSideDist += tStart;

                vec3 vMask = vec3(0);

                // Лимит шагов внутри чанка (корень из 3 * 128 ~ 220, берем с запасом 256)
                for (int j = 0; j < 256; j++) {
                    float tRel = dot(vMask, vSideDist - vDeltaDist);
                    if (length(vMask) < 0.5) tRel = tStart; // Первый шаг

                    // Если вышли за пределы чанка - прерываем
                    if (tRel > tEnd) break;

                    // Доп. проверка на индекс (на всякий случай)
                    if ((vMapPos >> BIT_SHIFT) != cMapPos) break;

                    ivec3 local = vMapPos & BIT_MASK;
                    int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
                    uint mat = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;

                    if (mat != 0u) {
                        tHit = tRel;
                        matID = mat;
                        // Нормаль:
                        // Если попали в самом начале (первый воксель) И мы пришли снаружи чанка -> нормаль чанка
                        if (length(vMask) < 0.5 && tNear >= 0.0) {
                            normal = boxNormal;
                        } else {
                            normal = -vec3(vStepDir) * vMask;
                            if (length(vMask) < 0.5) normal = -vec3(vStepDir); // Fallback для старта внутри
                        }
                        return true;
                    }

                    vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                    vSideDist += vMask * vDeltaDist; vMapPos += ivec3(vMask) * vStepDir;
                }
            }
        }

        cMask = (cSideDist.x < cSideDist.y) ? ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        cSideDist += cMask * cDeltaDist; cMapPos += ivec3(cMask) * cStepDir;
        tCurrent = dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

// --- РЕФРАКЦИЯ (Та же логика фикса швов + оптимизация) ---
bool TraceRefractionRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID) {
    // Предрасчёт
    vec3 invRd = 1.0 / rd;
    vec3 rdSign = sign(rd);
    ivec3 cStepDir = ivec3(rdSign);
    vec3 absInvRd = abs(invRd);

    ivec3 cMapPos = ivec3(floor(ro / float(CHUNK_SIZE)));
    vec3 cDeltaDist = absInvRd * float(CHUNK_SIZE);
    vec3 cSideDist = (rdSign * (vec3(cMapPos) * float(CHUNK_SIZE) - ro) + (rdSign * 0.5 + 0.5) * float(CHUNK_SIZE)) * absInvRd;
    vec3 cMask = vec3(0);
    float tCurrent = 0.0;

    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < 128; i++) {
        if (tCurrent > maxDist) return false;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) return false;

        int chunkIdx = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;
        if (chunkIdx != -1) {
            vec3 chunkMin = vec3(cMapPos) * float(CHUNK_SIZE);
            vec3 chunkMax = chunkMin + float(CHUNK_SIZE);

            // Inline AABB с предрасчитанными значениями
            vec3 t0 = (chunkMin - ro) * invRd;
            vec3 t1 = (chunkMax - ro) * invRd;
            vec3 tmin_vec = min(t0, t1);
            vec3 tmax_vec = max(t0, t1);
            float tNear = max(max(tmin_vec.x, tmin_vec.y), tmin_vec.z);
            float tFar = min(min(tmax_vec.x, tmax_vec.y), tmax_vec.z);

            vec3 n = step(tmin_vec.yzx, tmin_vec.xyz) * step(tmin_vec.zxy, tmin_vec.xyz);
            vec3 boxNormal = -n * rdSign;

            if (tFar >= tNear && tFar > 0.0) {
                float tStart = max(0.0, tNear);
                float tEnd = tFar;
                vec3 pEntry = ro + rd * tStart;

                if (tNear >= 0.0) pEntry -= boxNormal * 0.0005;

                ivec3 vMapPos = ivec3(floor(pEntry * VOXELS_PER_METER));
                ivec3 chunkBaseVox = cMapPos * VOXEL_RESOLUTION;
                vMapPos = clamp(vMapPos, chunkBaseVox, chunkBaseVox + ivec3(VOXEL_RESOLUTION - 1));

                vec3 vDeltaDist = absInvRd;
                ivec3 vStepDir = cStepDir;
                vec3 vSideDist = (rdSign * (vec3(vMapPos) - pEntry * VOXELS_PER_METER) + (rdSign * 0.5 + 0.5)) * vDeltaDist;
                vSideDist += tStart;

                vec3 vMask = vec3(0);

                for (int j = 0; j < 256; j++) {
                    float tRel = dot(vMask, vSideDist - vDeltaDist);
                    if (length(vMask) < 0.5) tRel = tStart;

                    if (tRel > tEnd) break;
                    if ((vMapPos >> BIT_SHIFT) != cMapPos) break;

                    ivec3 local = vMapPos & BIT_MASK;
                    int idx = local.x + VOXEL_RESOLUTION * (local.y + VOXEL_RESOLUTION * local.z);
                    uint mat = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;

                    if (mat != 0u && mat != 4u) {
                        tHit = tRel;
                        matID = mat;
                        return true;
                    }

                    vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                    vSideDist += vMask * vDeltaDist; vMapPos += ivec3(vMask) * vStepDir;
                }
            }
        }
        cMask = (cSideDist.x < cSideDist.y) ? ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        cSideDist += cMask * cDeltaDist; cMapPos += ivec3(cMask) * cStepDir;
        tCurrent = dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}

// --- TRACE DYNAMIC (Оставляем как есть, тут всё ок) ---
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
    vec3 currPos = gridSpaceRo + rd * (tStart + 0.001);
    ivec3 mapPos = ivec3(floor(currPos));
    ivec3 stepDir = ivec3(sign(rd));
    vec3 deltaDist = abs(1.0 / rd);
    vec3 sideDist = (sign(rd) * (vec3(mapPos) - currPos) + (0.5 + 0.5 * sign(rd))) * deltaDist;
    vec3 mask;

    for (int i = 0; i < 128; i++) {
        if (any(lessThan(mapPos, ivec3(0))) || any(greaterThanEqual(mapPos, ivec3(uGridSize)))) break;

        int nodeIndex = imageLoad(uObjectGridHead, mapPos).r;
        while (nodeIndex > 0) {
            int bufferIdx = nodeIndex - 1;
            uint objID = listNodes[bufferIdx].objectID;
            int nextNode = listNodes[bufferIdx].nextNode;
            if (objID > 0) {
                uint dynMat; vec3 localN;
                DynamicObject obj = dynObjects[int(objID) - 1];
                vec3 localRo = (obj.invModel * vec4(ro, 1.0)).xyz;
                vec3 localRd = (obj.invModel * vec4(rd, 0.0)).xyz;
                vec2 tBox = IntersectAABB_Obj(localRo, localRd, obj.boxMin.xyz, obj.boxMax.xyz, localN);

                if (tBox.x >= 0.0 && tBox.x < tHit) {
                    tHit = tBox.x;
                    outObjID = int(objID) - 1;
                    outLocalNormal = localN;
                    hitAny = true;
                }
            }
            nodeIndex = nextNode;
        }

        mask = (sideDist.x < sideDist.y) ? ((sideDist.x < sideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((sideDist.y < sideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        if ((dot(mask, sideDist - deltaDist) + tStart) * uGridStep > maxDist) break;
        sideDist += mask * deltaDist;
        mapPos += ivec3(mask) * stepDir;
    }
    return hitAny;
}

// --- TRACE WORLD (Unified) ---
HitResult TraceWorld(vec3 ro, vec3 rd, float maxDist) {
    HitResult res;
    res.isHit = false; res.t = maxDist; res.materialID = 0u; res.isDynamic = false; res.objID = -1; res.normal = vec3(0,1,0);

    float tStatic = maxDist; uint matStatic = 0u; vec3 normStatic = vec3(0);
    bool hitStatic = TraceStaticRay(ro, rd, maxDist, tStatic, matStatic, normStatic);

    float tDyn = maxDist; int idDyn = -1; vec3 normDyn = vec3(0);
    bool hitDyn = false;
    if (uObjectCount > 0) hitDyn = TraceDynamicRay(ro, rd, maxDist, tDyn, idDyn, normDyn);

    if (!hitStatic && !hitDyn) return res;

    if (hitStatic && hitDyn) {
        if (tDyn < tStatic) { res.isHit=true; res.t=tDyn; res.isDynamic=true; res.objID=idDyn; res.normal=normDyn; }
        else { res.isHit=true; res.t=tStatic; res.isDynamic=false; res.materialID=matStatic; res.normal=normStatic; }
    } else if (hitStatic) { res.isHit=true; res.t=tStatic; res.isDynamic=false; res.materialID=matStatic; res.normal=normStatic; }
    else { res.isHit=true; res.t=tDyn; res.isDynamic=true; res.objID=idDyn; res.normal=normDyn; }
    return res;
}