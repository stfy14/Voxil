vec2 IntersectAABB_Obj(vec3 ro, vec3 rd, vec3 bmin, vec3 bmax, out vec3 outNormal) {
    vec3 invDir = 1.0 / rd;
    vec3 t0 = (bmin - ro) * invDir, t1 = (bmax - ro) * invDir;
    vec3 tmin_vec = min(t0, t1), tmax_vec = max(t0, t1);
    float tNear = max(max(tmin_vec.x, tmin_vec.y), tmin_vec.z);
    float tFar = min(min(tmax_vec.x, tmax_vec.y), tmax_vec.z);
    if (tNear > tFar || tFar < 0.0) { outNormal = vec3(0.0); return vec2(-1.0); }
    vec3 n = step(tmin_vec.yzx, tmin_vec.xyz) * step(tmin_vec.zxy, tmin_vec.xyz);
    outNormal = -n * sign(rd);
    return vec2(tNear, tFar);
}
float CheckDynamicHitByID(int objectId, vec3 worldRo, vec3 worldRd, out uint outMat, out vec3 outLocalNormal) {
    if (objectId < 0 || objectId >= uObjectCount) return -1.0;
    DynamicObject obj = dynObjects[objectId];
    vec3 localRo = (obj.invModel * vec4(worldRo, 1.0)).xyz;
    vec3 localRd = (obj.invModel * vec4(worldRd, 0.0)).xyz;
    vec3 localNormal;
    vec2 t = IntersectAABB_Obj(localRo, localRd, obj.boxMin.xyz, obj.boxMax.xyz, localNormal);
    if (t.y < 0.0) return -1.0;
    outMat = 2u; outLocalNormal = localNormal;
    return max(0.0, t.x);
}
bool IsSolid(ivec3 pos) {
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * 16;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * 16;
    if (any(lessThan(pos, boundMin)) || any(greaterThanEqual(pos, boundMax))) return false;
    ivec3 chunkCoord = pos >> 4;
    int chunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
    if (chunkIdx == -1) return false;
    ivec3 local = pos & 15;
    int idx = local.x + 16 * (local.y + 16 * local.z);
    return ((packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu) != 0u;
}
// === OPTIMIZATION: HIERARCHICAL DDA (HDDA) ===
// Основная функция трассировки теперь использует двухуровневый подход.
bool TraceStaticRay(
    vec3 ro,
    vec3 rd,
    float maxDist,
inout float tHit,
inout uint matID,
inout vec3 normal
) {
    // --- 1. MACRO SETUP (Chunks) ---
    ivec3 cMapPos = ivec3(floor(ro / 16.0));
    vec3 cDeltaDist = abs(1.0 / rd) * 16.0;
    ivec3 cStepDir = ivec3(sign(rd));

    vec3 chunkOrigin = vec3(cMapPos) * 16.0;
    vec3 relPos = ro - chunkOrigin;

    vec3 cSideDist;
    cSideDist.x = (rd.x > 0.0) ? (16.0 - relPos.x) : relPos.x;
    cSideDist.y = (rd.y > 0.0) ? (16.0 - relPos.y) : relPos.y;
    cSideDist.z = (rd.z > 0.0) ? (16.0 - relPos.z) : relPos.z;
    cSideDist *= abs(1.0 / rd);

    // Маска показывает, какую ось мы пересекли последней (для нормали и входа)
    vec3 cMask = vec3(0);

    // tCurrent - дистанция до входа в ТЕКУЩИЙ рассматриваемый чанк
    float tCurrent = 0.0;

    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < uMaxRaySteps; i++) {
        // Проверки выхода
        if (tCurrent > maxDist) break;
        if (any(lessThan(cMapPos, bMin)) || any(greaterThanEqual(cMapPos, bMax))) break;

        int chunkIdx = imageLoad(uPageTable, cMapPos & (PAGE_TABLE_SIZE - 1)).r;

        if (chunkIdx != -1) {
            // === 2. MICRO SETUP (Voxels) ===
            // Мы попали в непустой чанк.

            // Вычисляем точную точку входа на основе Макро-DDA
            // Это намного точнее и быстрее, чем IntersectAABB
            vec3 pEntry = ro + rd * (tCurrent + 0.0001); // Чуть-чуть внутрь, чтобы floor сработал

            // Базовая позиция вокселя
            ivec3 vMapPos = ivec3(floor(pEntry));

            // --- FACE SNAPPING (Исправление стен и щелей) ---
            // Если мы пришли из другого чанка (tCurrent > 0), мы знаем, что стоим ровно на границе.
            // Принудительно ставим координату вокселя в 0 или 15 в зависимости от того, откуда пришли.
            // Это устраняет любые ошибки float-округления.
            if (tCurrent > 0.0) {
                if (cMask.x > 0.5) vMapPos.x = (rd.x > 0.0) ? (cMapPos.x * 16) : (cMapPos.x * 16 + 15);
                if (cMask.y > 0.5) vMapPos.y = (rd.y > 0.0) ? (cMapPos.y * 16) : (cMapPos.y * 16 + 15);
                if (cMask.z > 0.5) vMapPos.z = (rd.z > 0.0) ? (cMapPos.z * 16) : (cMapPos.z * 16 + 15);
            }

            // Инициализируем Микро-DDA
            vec3 vDeltaDist = abs(1.0 / rd);
            ivec3 vStepDir = cStepDir;

            vec3 vSideDist;
            // Считаем sideDist глобально от ro, чтобы сохранить точность на больших дистанциях
            vSideDist.x = (rd.x > 0.0) ? (float(vMapPos.x + 1) - ro.x) : (ro.x - float(vMapPos.x));
            vSideDist.y = (rd.y > 0.0) ? (float(vMapPos.y + 1) - ro.y) : (ro.y - float(vMapPos.y));
            vSideDist.z = (rd.z > 0.0) ? (float(vMapPos.z + 1) - ro.z) : (ro.z - float(vMapPos.z));
            vSideDist *= vDeltaDist;

            vec3 vMask = vec3(0);

            int safetyLoopCount = 0; // Счетчик безопасности

            // Микро-цикл (ходим пока внутри чанка)
            while (true) {
                // 1. Safety Break: Если мы сделали больше шагов, чем ширина чанка (по диагонали макс ~30), 
                // значит мы застряли. Выходим принудительно.
                safetyLoopCount++;
                if (safetyLoopCount > 64) break;
                // Дистанция до пересечения следующего вокселя
                float tVoxel = dot(vMask, vSideDist - vDeltaDist);
                // Коррекция для первого шага (когда vMask пустой)
                if (vMask.x + vMask.y + vMask.z == 0.0) tVoxel = tCurrent;

                if (tVoxel > maxDist) return false;

                // Проверка вокселя
                ivec3 local = vMapPos & 15;
                int idx = local.x + 16 * (local.y + 16 * local.z);
                uint mat = (packedVoxels[chunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;

                if (mat != 0u) {
                    // HIT!
                    tHit = tVoxel;
                    matID = mat;

                    // Нормаль
                    // Если это самый первый шаг в чанк, нормаль берем из шага ЧАНКА (cMask)
                    // Иначе берем из шага ВОКСЕЛЯ (vMask)
                    // (length(vMask) < 0.5 означает, что мы еще не сделали ни одного шага в микро-цикле)
                    if (length(vMask) < 0.5) {
                        if (tCurrent == 0.0) normal = -vec3(vStepDir); // Мы заспавнились внутри блока
                        else normal = -vec3(cStepDir) * cMask;         // Мы уперлись в стену чанка
                    } else {
                        normal = -vec3(vStepDir) * vMask;
                    }
                    return true;
                }

                // Шаг Микро-DDA
                vMask = (vSideDist.x < vSideDist.y) ?
                ((vSideDist.x < vSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
                ((vSideDist.y < vSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));

                vSideDist += vMask * vDeltaDist;
                vMapPos += ivec3(vMask) * vStepDir;

                // Если вышли за пределы текущего чанка - прерываем микро-цикл
                // Битовая магия: (vMapPos >> 4) эквивалентно floor(vMapPos / 16)
                if ((vMapPos.x >> 4) != cMapPos.x ||
                    (vMapPos.y >> 4) != cMapPos.y ||
                    (vMapPos.z >> 4) != cMapPos.z) {
                    break;
                }
            }
        }

        // --- 3. MACRO STEP ---
        cMask = (cSideDist.x < cSideDist.y) ?
        ((cSideDist.x < cSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
        ((cSideDist.y < cSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));

        cSideDist += cMask * cDeltaDist;
        cMapPos += ivec3(cMask) * cStepDir;

        // Обновляем tCurrent для следующего чанка
        tCurrent = dot(cMask, cSideDist - cDeltaDist);
    }

    return false;
}