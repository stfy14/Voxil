// --- START OF FILE include/tracing.glsl ---

vec2 IntersectAABB_Obj(vec3 ro, vec3 rd, vec3 bmin, vec3 bmax, out vec3 outNormal) {
    vec3 invDir = 1.0 / rd;
    vec3 t0 = (bmin - ro) * invDir, t1 = (bmax - ro) * invDir;
    vec3 tmin_vec = min(t0, t1), tmax_vec = max(t0, t1);

    float tNear = max(max(tmin_vec.x, tmin_vec.y), tmin_vec.z);
    float tFar = min(min(tmax_vec.x, tmax_vec.y), tmax_vec.z);

    if (tFar < 0.0 || tNear > tFar) { outNormal = vec3(0.0); return vec2(-1.0); }

    vec3 n = step(tmin_vec.yzx, tmin_vec.xyz) * step(tmin_vec.zxy, tmin_vec.xyz);
    outNormal = -n * sign(rd);

    return vec2(tNear, tFar);
}

float CheckDynamicHitByID(int objectId, vec3 worldRo, vec3 worldRd, out uint outMat, out vec3 outLocalNormal) {
    if (objectId < 0 || objectId >= uObjectCount) return -1.0;

    // Получаем данные объекта из SSBO
    DynamicObject obj = dynObjects[objectId];

    // Переводим луч в локальное пространство объекта
    vec3 localRo = (obj.invModel * vec4(worldRo, 1.0)).xyz;
    vec3 localRd = (obj.invModel * vec4(worldRd, 0.0)).xyz;

    vec3 localNormal;
    vec2 t = IntersectAABB_Obj(localRo, localRd, obj.boxMin.xyz, obj.boxMax.xyz, localNormal);

    if (t.y < 0.0) return -1.0;

    outMat = 2u; // Пока хардкодим материал для динамики, или можно передавать цвет
    outLocalNormal = localNormal;

    // Важно: t.x - это дистанция в локальном пространстве (если масштаб 1:1, то совпадает с мировым)
    // Если есть масштабирование, нужно корректировать, но у нас воксели 1:1.
    return max(0.0, t.x);
}

// === НОВАЯ ФУНКЦИЯ: ТРАССИРОВКА ДИНАМИКИ ===
// Возвращает true, если попали. Заполняет tHit, objID, localNormal.
bool TraceDynamicRay(
    vec3 ro,
    vec3 rd,
    float maxDist,
inout float tHit,
inout int outObjID,
inout vec3 outLocalNormal
) {
    tHit = maxDist; // Инициализируем макс дистанцией
    bool hitAny = false;

    vec3 gridSpaceRo = (ro - uGridOrigin) / uGridStep;
    vec3 invDir = 1.0 / rd;

    // Пересечение с Bounding Box всей сетки (чтобы не трассировать пустоту)
    vec3 t0 = -gridSpaceRo * invDir;
    vec3 t1 = (vec3(uGridSize) - gridSpaceRo) * invDir;
    vec3 tmin = min(t0, t1), tmax = max(t0, t1);
    float tEnter = max(max(tmin.x, tmin.y), tmin.z);
    float tExit = min(min(tmax.x, tmax.y), tmax.z);

    // Если луч не пересекает сетку вообще
    if (tExit < tEnter || tExit < 0.0) return false;
    if (tEnter > maxDist) return false;

    float tStart = max(0.0, tEnter);
    vec3 currPos = gridSpaceRo + rd * (tStart + 0.001);

    ivec3 mapPos = ivec3(floor(currPos));
    ivec3 stepDir = ivec3(sign(rd));
    vec3 deltaDist = abs(1.0 / rd);
    vec3 sideDist = (sign(rd) * (vec3(mapPos) - currPos) + (0.5 + 0.5 * sign(rd))) * deltaDist;
    vec3 mask;

    // Шагаем по сетке
    for (int i = 0; i < 128; i++) { // 128 шагов по сетке обычно достаточно
                                    if (any(lessThan(mapPos, ivec3(0))) || any(greaterThanEqual(mapPos, ivec3(uGridSize)))) break;

                                    // 1. Читаем голову списка
                                    int nodeIndex = imageLoad(uObjectGridHead, mapPos).r;

                                    // 2. Бежим по Linked List
                                    while (nodeIndex > 0) {
                                        int bufferIdx = nodeIndex - 1;
                                        uint objID = listNodes[bufferIdx].objectID;
                                        int nextNode = listNodes[bufferIdx].nextNode;

                                        if (objID > 0) {
                                            uint dynMat; vec3 localN;
                                            // Проверяем пересечение с конкретным кубиком
                                            float tBox = CheckDynamicHitByID(int(objID) - 1, ro, rd, dynMat, localN);

                                            if (tBox >= 0.0 && tBox < tHit) {
                                                tHit = tBox;
                                                outObjID = int(objID) - 1;
                                                outLocalNormal = localN;
                                                hitAny = true;
                                            }
                                        }
                                        nodeIndex = nextNode;
                                    }

                                    // Если мы уже нашли пересечение БЛИЖЕ, чем следующая клетка сетки, можно выходить раньше?
                                    // Нет, потому что объект в следующей клетке может быть огромным и перекрывать текущую.
                                    // Но у нас воксели маленькие, так что для оптимизации можно проверять:
                                    // if (hitAny && tHit < (dot(mask, sideDist - deltaDist) + tStart) * uGridStep) return true;
                                    // Пока оставим полный перебор для точности.

                                    mask = (sideDist.x < sideDist.y) ?
                                    ((sideDist.x < sideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
                                    ((sideDist.y < sideDist.z) ? vec3(0,1,0) : vec3(0,0,1));

                                    // Проверка выхода за дистанцию
                                    if ((dot(mask, sideDist - deltaDist) + tStart) * uGridStep > maxDist) break;

                                    sideDist += mask * deltaDist;
                                    mapPos += ivec3(mask) * stepDir;
    }

    return hitAny;
}

bool TraceStaticRay(
    vec3 ro, vec3 rd, float maxDist,
inout float tHit, inout uint matID, inout vec3 normal
) {
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
                // ИСПРАВЛЕНА ОШИБКА COPY-PASTE
                vMask = (vSideDist.x < vSideDist.y) ? ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
                vSideDist += vMask * vDeltaDist; vMapPos += ivec3(vMask) * vStepDir;
            }
        }
        // ИСПРАВЛЕНА ОШИБКА COPY-PASTE
        cMask = (cSideDist.x < cSideDist.y) ? ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) : ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        cSideDist += cMask * cDeltaDist; cMapPos += ivec3(cMask) * cStepDir;
        tCurrent = dot(cMask, cSideDist - cDeltaDist);
    }
    return false;
}