#version 450 core
out vec4 FragColor;
in vec2 uv;

uniform vec3 uCamPos;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uRenderDistance;
uniform vec3 uSunDir;

uniform int uBoundMinX;
uniform int uBoundMinY;
uniform int uBoundMinZ;
uniform int uBoundMaxX;
uniform int uBoundMaxY;
uniform int uBoundMaxZ;
uniform int uObjectCount;

uniform vec3 uGridOrigin;
uniform float uGridStep;
uniform int uGridSize;
layout(binding = 1) uniform isampler3D uObjectGrid;

const int CHUNK_SIZE = 16;
const ivec3 PAGE_TABLE_SIZE = ivec3(512, 16, 512);

layout(binding = 0, r32i) uniform iimage3D uPageTable;
layout(std430, binding = 1) buffer VoxelSSBO { uint packedVoxels[]; };
struct DynamicObject {
    mat4 model;
    vec4 color;
    vec4 boxMin;
    vec4 boxMax;
};
layout(std430, binding = 2) buffer DynObjects { DynamicObject dynObjects[]; };

// --- ЦВЕТА (Оставляем как есть, они вам нравятся) ---
vec3 GetColor(uint id) {
    if (id == 1u) return vec3(0.55, 0.27, 0.07); // Земля
    if (id == 2u) return vec3(0.6, 0.6, 0.65);   // Камень
    if (id == 3u) return vec3(0.35, 0.2, 0.1);   // Дерево
    if (id == 4u) return vec3(0.2, 0.4, 0.8);    // Вода
    return vec3(1.0, 0.0, 1.0);
}

// --- NOISE ---
float IGN(vec2 p) {
    vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
    return fract(magic.z * fract(dot(p, magic.xy)));
}

vec2 randomInCircle(vec2 screenPos) {
    float noise = IGN(screenPos);
    float r = sqrt(noise);
    float a = noise * 6.28318;
    return vec2(cos(a), sin(a)) * r;
}

// --- VOXEL LOOKUP ---
bool IsSolid(ivec3 pos) {
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * 16;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * 16;
    if (any(lessThan(pos, boundMin)) || any(greaterThanEqual(pos, boundMax))) return false;

    ivec3 chunkCoord = pos >> 4;
    int chunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
    if (chunkIdx == -1) return false;

    ivec3 local = pos & 15;
    int voxelLinear = local.x + 16 * (local.y + 16 * local.z);
    uint packedVal = packedVoxels[chunkIdx + (voxelLinear >> 2)];
    uint mat = (packedVal >> ((voxelLinear & 3) * 8)) & 0xFFu;
    return (mat != 0u);
}

// --- AO LOGIC ---
float GetCornerOcclusion(ivec3 pos, ivec3 side1, ivec3 side2) {
    ivec3 corner = side1 + side2;
    bool s1 = IsSolid(pos + side1);
    bool s2 = IsSolid(pos + side2);
    bool c  = IsSolid(pos + corner);

    if (s1 && s2) return 3.0;
    float occlusion = 0.0;
    if (s1) occlusion += 1.0;
    if (s2) occlusion += 1.0;
    if (c)  occlusion += 1.0;
    return occlusion;
}

float CalculateAO(vec3 hitPos, vec3 normal) {
    ivec3 ipos = ivec3(floor(hitPos + normal * 0.01));
    ivec3 n = ivec3(normal);
    vec3 localPos = hitPos - vec3(ipos);
    ivec3 t, b;
    vec2 uvSurf;

    if (abs(n.y) > 0.5) { t = ivec3(1, 0, 0); b = ivec3(0, 0, 1); uvSurf = localPos.xz; }
    else if (abs(n.x) > 0.5) { t = ivec3(0, 0, 1); b = ivec3(0, 1, 0); uvSurf = localPos.zy; }
    else { t = ivec3(1, 0, 0); b = ivec3(0, 1, 0); uvSurf = localPos.xy; }

    uvSurf = fract(uvSurf);
    float occ00 = GetCornerOcclusion(ipos, -t, -b);
    float occ10 = GetCornerOcclusion(ipos,  t, -b);
    float occ01 = GetCornerOcclusion(ipos, -t,  b);
    float occ11 = GetCornerOcclusion(ipos,  t,  b);

    vec2 smoothUV = uvSurf * uvSurf * (3.0 - 2.0 * uvSurf);
    float occBottom = mix(occ00, occ10, smoothUV.x);
    float occTop    = mix(occ01, occ11, smoothUV.x);
    float finalOcc  = mix(occBottom, occTop, smoothUV.y);

    // Немного ослабил степень, чтобы без гаммы тени не были черными дырами
    float ao = pow(0.8, finalOcc);

    // Дизеринг оставляем - он убирает полосы
    float noise = IGN(gl_FragCoord.xy);
    ao += (noise - 0.5) * (1.0 / 64.0);

    return clamp(ao, 0.0, 1.0);
}

// --- INTERSECTION ---
vec2 IntersectAABB_Obj(vec3 ro, vec3 rd, vec3 bmin, vec3 bmax, out vec3 outNormal) {
    vec3 invDir = 1.0 / rd;
    vec3 t0 = (bmin - ro) * invDir;
    vec3 t1 = (bmax - ro) * invDir;
    vec3 tmin_vec = min(t0, t1);
    vec3 tmax_vec = max(t0, t1);
    float tNear = max(max(tmin_vec.x, tmin_vec.y), tmin_vec.z);
    float tFar = min(min(tmax_vec.x, tmax_vec.y), tmax_vec.z);
    if (tNear > tFar || tFar < 0.0) { outNormal = vec3(0.0); return vec2(-1.0); }
    vec3 n = step(tmin_vec.yzx, tmin_vec.xyz) * step(tmin_vec.zxy, tmin_vec.xyz);
    outNormal = -n * sign(rd);
    return vec2(tNear, tFar);
}

float CheckDynamicHit(int objIndex, vec3 worldRo, vec3 worldRd, out uint outMat, out vec3 outLocalNormal) {
    DynamicObject obj = dynObjects[objIndex];
    mat4 invModel = inverse(obj.model);
    vec3 localRo = (invModel * vec4(worldRo, 1.0)).xyz;
    vec3 localRd = (invModel * vec4(worldRd, 0.0)).xyz;
    vec2 t = IntersectAABB_Obj(localRo, localRd, obj.boxMin.xyz, obj.boxMax.xyz, outLocalNormal);
    if (t.x >= 0.0) { outMat = 2u; return t.x; }
    outMat = 0u; return -1.0;
}

void main() {
    float dummy = uGridStep + float(uGridSize) + uGridOrigin.x + float(textureSize(uObjectGrid, 0).x);

    vec2 pos = uv * 2.0 - 1.0;
    vec4 target = inverse(uProjection) * vec4(pos, 1.0, 1.0);
    vec3 rayDir = normalize((inverse(uView) * vec4(target.xyz, 0.0)).xyz);
    if (abs(rayDir.x) < 1e-6) rayDir.x = 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = 1e-6;

    float tDynamicHit = 100000.0;
    int bestObjID = -1;
    vec3 bestLocalNormal = vec3(0);

    if (uObjectCount > 0) {
        for (int i = 0; i < uObjectCount; i++) {
            uint dynMat;
            vec3 localN;
            float tHit = CheckDynamicHit(i, uCamPos, rayDir, dynMat, localN);
            if (tHit >= 0.0 && tHit < tDynamicHit) {
                tDynamicHit = tHit;
                bestObjID = i;
                bestLocalNormal = localN;
            }
        }
    }

    float tStaticHit = 100000.0;
    uint staticHitMat = 0u;
    vec3 mask = vec3(0);
    vec3 finalNormal = vec3(0);
    ivec3 mapPos = ivec3(floor(uCamPos));
    vec3 deltaDist = abs(1.0 / rayDir);
    ivec3 stepDir = ivec3(sign(rayDir));
    vec3 sideDist = (sign(rayDir) * (vec3(mapPos) - uCamPos) + (0.5 + 0.5 * sign(rayDir))) * deltaDist;
    ivec3 cachedChunkCoord = ivec3(-999999);
    int cachedChunkIdx = -1;
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * 16;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * 16;

    for (int i = 0; i < 4000; i++) {
        if (any(lessThan(mapPos, boundMin)) || any(greaterThanEqual(mapPos, boundMax))) break;
        ivec3 chunkCoord = mapPos >> 4;
        if (chunkCoord != cachedChunkCoord) {
            cachedChunkCoord = chunkCoord;
            cachedChunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
        }
        if (cachedChunkIdx != -1) {
            ivec3 local = mapPos & 15;
            int voxelLinear = local.x + 16 * (local.y + 16 * local.z);
            uint packedVal = packedVoxels[cachedChunkIdx + (voxelLinear >> 2)];
            uint staticMat = (packedVal >> ((voxelLinear & 3) * 8)) & 0xFFu;
            if (staticMat != 0u) {
                tStaticHit = dot(mask, sideDist - deltaDist);
                staticHitMat = staticMat;
                finalNormal = -sign(rayDir) * mask;
                break;
            }
        }
        if (sideDist.x < sideDist.y) {
            if (sideDist.x < sideDist.z) mask = vec3(1.0, 0.0, 0.0); else mask = vec3(0.0, 0.0, 1.0);
        } else {
            if (sideDist.y < sideDist.z) mask = vec3(0.0, 1.0, 0.0); else mask = vec3(0.0, 0.0, 1.0);
        }
        sideDist += mask * deltaDist;
        mapPos += ivec3(mask) * stepDir;
    }

    float tEnd = min(tStaticHit, tDynamicHit);
    vec3 skyColor = vec3(0.53, 0.81, 0.92);

    if (tEnd > 99000.0) {
        FragColor = vec4(skyColor, 1.0) + vec4(dummy * 0.0000001);
        gl_FragDepth = 1.0;
        return;
    }

    vec3 normal;
    vec3 albedo;
    float ao = 1.0;
    bool isDynamic = tDynamicHit < tStaticHit;
    vec3 hitPos = uCamPos + rayDir * tEnd;

    if (isDynamic) {
        mat3 normalMatrix = transpose(inverse(mat3(dynObjects[bestObjID].model)));
        normal = normalize(normalMatrix * bestLocalNormal);
        albedo = dynObjects[bestObjID].color.rgb;
        ao = 0.8;
    } else {
        normal = finalNormal;
        albedo = GetColor(staticHitMat);
        ao = CalculateAO(hitPos + normal * 0.001, normal);
    }

    vec3 sunDir = normalize(uSunDir);
    float ndotl = max(dot(normal, sunDir), 0.0);
    float shadowFactor = 1.0;

    if (ndotl > 0.0) {
        float sunSize = 0.012;
        vec2 dither = randomInCircle(gl_FragCoord.xy);
        vec3 softSunDir = normalize(sunDir + vec3(dither.x, dither.y, dither.x) * sunSize);
        vec3 shadowOrigin = hitPos + normal * 0.005;
        vec3 shadowDir = softSunDir;
        if (abs(shadowDir.x) < 1e-6) shadowDir.x = 1e-6;
        if (abs(shadowDir.y) < 1e-6) shadowDir.y = 1e-6;
        if (abs(shadowDir.z) < 1e-6) shadowDir.z = 1e-6;

        ivec3 sMapPos = ivec3(floor(shadowOrigin));
        vec3 sDeltaDist = abs(1.0 / shadowDir);
        ivec3 sStepDir = ivec3(sign(shadowDir));
        vec3 sSideDist = (sign(shadowDir) * (vec3(sMapPos) - shadowOrigin) + (0.5 + 0.5 * sign(shadowDir))) * sDeltaDist;
        vec3 sMask = vec3(0);
        ivec3 sCachedChunkCoord = ivec3(-999999);
        int sCachedChunkIdx = -1;

        for (int s = 0; s < 200; s++) {
            if (any(lessThan(sMapPos, boundMin)) || any(greaterThanEqual(sMapPos, boundMax))) break;
            ivec3 chunkCoord = sMapPos >> 4;
            if (chunkCoord != sCachedChunkCoord) {
                sCachedChunkCoord = chunkCoord;
                sCachedChunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
            }
            if (sCachedChunkIdx != -1) {
                ivec3 local = sMapPos & 15;
                int voxelLinear = local.x + 16 * (local.y + 16 * local.z);
                uint packedVal = packedVoxels[sCachedChunkIdx + (voxelLinear >> 2)];
                uint sMat = (packedVal >> ((voxelLinear & 3) * 8)) & 0xFFu;
                if (sMat != 0u) { shadowFactor = 0.0; break; }
            }
            if (sSideDist.x < sSideDist.y) {
                if (sSideDist.x < sSideDist.z) sMask = vec3(1.0, 0.0, 0.0); else sMask = vec3(0.0, 0.0, 1.0);
            } else {
                if (sSideDist.y < sSideDist.z) sMask = vec3(0.0, 1.0, 0.0); else sMask = vec3(0.0, 0.0, 1.0);
            }
            sSideDist += sMask * sDeltaDist;
            sMapPos += ivec3(sMask) * sStepDir;
        }
    } else { shadowFactor = 0.0; }

    // --- НАСТРОЙКА ЯРКОСТИ (БЕЗ ГАММЫ) ---

    // 1. Усиливаем солнце (было 1.0, стало 1.2), чтобы было ярче
    vec3 sunLightColor = vec3(1.2, 1.18, 1.1);
    vec3 direct = albedo * sunLightColor * ndotl * shadowFactor;

    float upFactor = 0.5 + 0.5 * normal.y;

    // 2. Усиливаем Ambient свет (тени станут светлее, вся сцена ярче)
    // Небо более яркое
    vec3 skyAmbient = vec3(0.7, 0.85, 1.0);
    // Земля (отраженный свет) более насыщенная
    vec3 groundAmbient = vec3(0.3, 0.25, 0.2);

    vec3 ambientLight = mix(groundAmbient, skyAmbient, upFactor);

    // 3. Увеличиваем общий множитель Ambient (был 0.5, стал 0.85)
    // Это делает тени светлыми и приятными, компенсируя отсутствие гаммы.
    vec3 ambient = albedo * ambientLight * 0.85 * ao;

    vec3 finalColor = direct + ambient;

    float fogFactor = smoothstep(uRenderDistance * 0.6, uRenderDistance * 0.95, tEnd);
    finalColor = mix(finalColor, skyColor, fogFactor);

    // Гамму УБРАЛИ, цвета вернутся к исходным (оранжевая земля)
    FragColor = vec4(finalColor, 1.0);

    vec4 clipPos = uProjection * uView * vec4(hitPos, 1.0);
    gl_FragDepth = (clipPos.z / clipPos.w) * 0.5 + 0.5;
}