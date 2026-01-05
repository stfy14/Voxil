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

// Главная функция трассировки статики
bool TraceStaticRay(vec3 ro, vec3 rd, float maxDist, inout float tHit, inout uint matID, inout vec3 normal) {
    ivec3 mapPos = ivec3(floor(ro)), stepDir = ivec3(sign(rd));
    vec3 deltaDist = abs(1.0 / rd);
    vec3 sideDist = (sign(rd) * (vec3(mapPos) - ro) + (0.5 + 0.5 * sign(rd))) * deltaDist;
    vec3 mask = vec3(0);
    ivec3 cachedChunkCoord = ivec3(-999999); int cachedChunkIdx = -1;
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * 16;
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * 16;

    // --- ИСПОЛЬЗУЕМ ДИНАМИЧЕСКИЙ ЛИМИТ ---
    for (int i = 0; i < uMaxRaySteps; i++) {
        mask = (sideDist.x < sideDist.y) ?
        ((sideDist.x < sideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
        ((sideDist.y < sideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
        sideDist += mask * deltaDist; mapPos += ivec3(mask) * stepDir;
        float dist = dot(mask, sideDist - deltaDist);

        // Дополнительная защита break'ом всё равно полезна
        if (dist > maxDist) break;

        if (any(lessThan(mapPos, bMin)) || any(greaterThanEqual(mapPos, bMax))) break;

        ivec3 chunkCoord = mapPos >> 4;
        if (chunkCoord != cachedChunkCoord) {
            cachedChunkCoord = chunkCoord;
            cachedChunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
        }
        if (cachedChunkIdx != -1) {
            ivec3 local = mapPos & 15;
            int voxelLinear = local.x + 16 * (local.y + 16 * local.z);
            uint staticMat = (packedVoxels[cachedChunkIdx + (voxelLinear >> 2)] >> ((voxelLinear & 3) * 8)) & 0xFFu;
            if (staticMat != 0u) {
                tHit = dist;
                matID = staticMat;
                normal = -vec3(stepDir) * mask;
                return true;
            }
        }
    }
    return false;
}