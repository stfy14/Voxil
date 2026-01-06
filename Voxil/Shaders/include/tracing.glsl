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

// Вспомогательная функция (оставляем как есть или добавляем, если нет)
float GetDistToNextChunkBoundary(vec3 p, vec3 rd) {
    vec3 nextChunkPos = floor(p / 16.0) * 16.0;
    if (rd.x > 0.0) nextChunkPos.x += 16.0;
    if (rd.y > 0.0) nextChunkPos.y += 16.0;
    if (rd.z > 0.0) nextChunkPos.z += 16.0;
    vec3 dists = (nextChunkPos - p) / rd;
    if (abs(rd.x) < 1e-6) dists.x = 1e30;
    if (abs(rd.y) < 1e-6) dists.y = 1e30;
    if (abs(rd.z) < 1e-6) dists.z = 1e30;
    return min(min(dists.x, dists.y), dists.z);
}

bool TraceStaticRay(
    vec3 ro,
    vec3 rd,
    float maxDist,
inout float tHit,
inout uint matID,
inout vec3 normal
) {
    // Инициализация DDA
    ivec3 mapPos = ivec3(floor(ro));
    ivec3 stepDir = ivec3(sign(rd));
    vec3 deltaDist = abs(1.0 / rd);

    vec3 sideDist;
    sideDist.x = (rd.x > 0.0 ? (float(mapPos.x + 1) - ro.x) : (ro.x - float(mapPos.x))) * deltaDist.x;
    sideDist.y = (rd.y > 0.0 ? (float(mapPos.y + 1) - ro.y) : (ro.y - float(mapPos.y))) * deltaDist.y;
    sideDist.z = (rd.z > 0.0 ? (float(mapPos.z + 1) - ro.z) : (ro.z - float(mapPos.z))) * deltaDist.z;

    vec3 mask = vec3(0);

    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * 16;
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * 16;

    // Кэширование чанка
    ivec3 cachedChunkCoord = ivec3(-999999);
    int cachedChunkIdx = -1;

    // Основной цикл луча
    for (int i = 0; i < uMaxRaySteps; i++) {
        // Проверка выхода за мир
        if (any(lessThan(mapPos, bMin)) || any(greaterThanEqual(mapPos, bMax))) break;

        // Текущая дистанция (для ограничения дальности)
        // DDA хранит дистанцию до СЛЕДУЮЩЕЙ грани, поэтому вычитаем deltaDist, чтобы получить ТЕКУЩУЮ
        float currentT = dot(mask, sideDist - deltaDist);
        if (currentT > maxDist) break;

        // Определяем текущий чанк
        ivec3 chunkCoord = mapPos >> 4; // Быстрое деление на 16

        // Обновляем кэш страницы, если перешли в новый чанк
        if (chunkCoord != cachedChunkCoord) {
            cachedChunkCoord = chunkCoord;
            cachedChunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
        }

        // === OPTIMIZATION: BLIND DDA (MACRO STEPPING) ===
        if (cachedChunkIdx == -1) {
            // Чанк пустой. Запускаем "слепой" цикл.
            // Мы просто шагаем по сетке, не читая память, пока не выйдем из этого чанка.
            // 32 шага достаточно, чтобы пройти любой чанк (16x16x16) насквозь по диагонали.
            for (int k = 0; k < 32; k++) {
                // Стандартный шаг DDA
                mask = (sideDist.x < sideDist.y) ?
                ((sideDist.x < sideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
                ((sideDist.y < sideDist.z) ? vec3(0,1,0) : vec3(0,0,1));

                sideDist += mask * deltaDist;
                mapPos += ivec3(mask) * stepDir;

                // Быстрая проверка: вышли ли мы из текущего пустого чанка?
                // Используем битовую маску ~15 (это то же самое, что floor(x/16)*16)
                if ((mapPos.x & ~15) != (chunkCoord.x << 4) ||
                (mapPos.y & ~15) != (chunkCoord.y << 4) ||
                (mapPos.z & ~15) != (chunkCoord.z << 4)) {
                    break; // Вышли! Возвращаемся в основной цикл
                }
            }
            // Continue перезапустит основной цикл, обновит chunkCoord и проверит новый чанк
            continue;
        }

        // === MICRO STEPPING (Внутри твердого чанка) ===
        // Если чанк загружен, шагаем и проверяем воксели
        // Лимит 64 на случай застревания, но обычно выход происходит раньше
        for (int k = 0; k < 64; k++) {
            ivec3 local = mapPos & 15;
            int voxelIdx = local.x + 16 * (local.y + 16 * local.z);
            uint mat = (packedVoxels[cachedChunkIdx + (voxelIdx >> 2)] >> ((voxelIdx & 3) * 8)) & 0xFFu;

            if (mat != 0u) {
                // ПОПАДАНИЕ!
                // Нормаль берется из mask. 
                // Если mask пустой (первый шаг луча), берем обратное направление луча.
                if (length(mask) < 0.5) normal = -stepDir;
                else normal = -vec3(stepDir) * mask;

                tHit = dot(mask, sideDist - deltaDist);
                // Фикс для tHit на самом первом шаге
                if (tHit < 0.0001) tHit = 0.0;

                matID = mat;
                return true;
            }

            // Шаг DDA
            mask = (sideDist.x < sideDist.y) ?
            ((sideDist.x < sideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
            ((sideDist.y < sideDist.z) ? vec3(0,1,0) : vec3(0,0,1));

            sideDist += mask * deltaDist;
            mapPos += ivec3(mask) * stepDir;

            // Если вышли из чанка во время обычного шага
            if ((mapPos.x >> 4) != chunkCoord.x ||
            (mapPos.y >> 4) != chunkCoord.y ||
            (mapPos.z >> 4) != chunkCoord.z) {
                break;
            }
        }
    }

    return false;
}
