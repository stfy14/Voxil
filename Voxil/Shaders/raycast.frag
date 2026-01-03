#version 450 core
out vec4 FragColor;
in vec2 uv;

uniform vec3 uCamPos;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uRenderDistance;
uniform vec3 uSunDir;

// Границы мира в чанках
uniform int uBoundMinX;
uniform int uBoundMinY;
uniform int uBoundMinZ;
uniform int uBoundMaxX;
uniform int uBoundMaxY;
uniform int uBoundMaxZ;
uniform int uObjectCount;

// Чтобы не было варнингов
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

// --- HELPERS ---
vec3 GetColor(uint id) {
    if (id == 1u) return vec3(0.55, 0.27, 0.07);
    if (id == 2u) return vec3(0.5, 0.5, 0.5);
    if (id == 3u) return vec3(0.4, 0.26, 0.13);
    if (id == 4u) return vec3(0.2, 0.4, 0.8);
    return vec3(1.0, 0.0, 1.0);
}

// Пересечение с AABB для динамики
vec2 IntersectAABB_Obj(vec3 ro, vec3 rd, vec3 bmin, vec3 bmax, out vec3 outNormal) {
    vec3 invDir = 1.0 / rd;
    vec3 t0 = (bmin - ro) * invDir;
    vec3 t1 = (bmax - ro) * invDir;
    vec3 tmin_vec = min(t0, t1);
    vec3 tmax_vec = max(t0, t1);
    float tNear = max(max(tmin_vec.x, tmin_vec.y), tmin_vec.z);
    float tFar = min(min(tmax_vec.x, tmax_vec.y), tmax_vec.z);

    if (tNear > tFar || tFar < 0.0) {
        outNormal = vec3(0.0);
        return vec2(-1.0);
    }
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
    if (t.x >= 0.0) {
        outMat = 2u;
        return t.x;
    }
    outMat = 0u;
    return -1.0;
}

void main() {
    // Force usage (обман компилятора)
    float dummy = uGridStep + float(uGridSize) + uGridOrigin.x + float(textureSize(uObjectGrid, 0).x);

    vec2 pos = uv * 2.0 - 1.0;
    vec4 target = inverse(uProjection) * vec4(pos, 1.0, 1.0);
    vec3 rayDir = normalize((inverse(uView) * vec4(target.xyz, 0.0)).xyz);

    if (abs(rayDir.x) < 1e-6) rayDir.x = 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = 1e-6;

    // --- 1. DYNAMIC OBJECTS ---
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

    // --- 2. STATIC VOXELS (DDA) ---
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

    // Границы мира
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * 16;
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * 16;

    // Увеличим шаги до 5000 для дальних дистанций и сложных углов
    int MAX_STEPS = 5000;

    for (int i = 0; i < MAX_STEPS; i++) {

        // --- ПРОВЕРКА ГРАНИЦ (Integer Check) ---
        if (any(lessThan(mapPos, boundMin)) || any(greaterThanEqual(mapPos, boundMax))) {
            break;
        }

        // --- ПРОВЕРКА ВОКСЕЛЯ ---
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

        // --- ШАГ DDA (STRICT SINGLE AXIS FIX) ---
        // Старый метод (mask = step * step) мог дать две единицы одновременно.
        // Новый метод гарантирует ровно одну ось.
        // Это предотвращает пролет сквозь углы вокселей ("Diagonal Leak").

        if (sideDist.x < sideDist.y) {
            if (sideDist.x < sideDist.z) {
                mask = vec3(1.0, 0.0, 0.0); // X минимальный
            } else {
                mask = vec3(0.0, 0.0, 1.0); // Z минимальный
            }
        } else {
            if (sideDist.y < sideDist.z) {
                mask = vec3(0.0, 1.0, 0.0); // Y минимальный
            } else {
                mask = vec3(0.0, 0.0, 1.0); // Z минимальный
            }
        }

        sideDist += mask * deltaDist;
        mapPos += ivec3(mask) * stepDir;

        // Убрали оптимизацию "if (currDist > tDynamicHit) break;",
        // так как она могла вызывать артефакты при близких значениях.
    }

    float tEnd = min(tStaticHit, tDynamicHit);

    vec3 skyColor = vec3(0.53, 0.81, 0.92);

    // Если ничего не нашли
    if (tEnd > 99000.0) {
        FragColor = vec4(skyColor, 1.0) + vec4(dummy * 0.0000001);
        gl_FragDepth = 1.0;
        return;
    }

    // --- ОТРИСОВКА ---
    vec3 normal;
    vec3 albedo;
    bool isDynamic = tDynamicHit < tStaticHit;

    if (isDynamic) {
        mat3 normalMatrix = transpose(inverse(mat3(dynObjects[bestObjID].model)));
        normal = normalize(normalMatrix * bestLocalNormal);
        albedo = dynObjects[bestObjID].color.rgb;
    } else {
        normal = finalNormal;
        albedo = GetColor(staticHitMat);
    }

    // --- ТЕНИ (SHADOWS) ---
    vec3 hitPos = uCamPos + rayDir * tEnd;
    vec3 sunDir = normalize(uSunDir);
    float ndotl = max(dot(normal, sunDir), 0.0);
    float shadowFactor = 1.0;
    float ambient = 0.4;

    if (ndotl > 0.0) {
        vec3 shadowOrigin = hitPos + normal * 0.005;
        vec3 shadowDir = sunDir;
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

        // Для теней используем ту же Strict логику, чтобы тени не дырявились
        for (int s = 0; s < 250; s++) { // Немного увеличим дальность теней
                                        if (any(lessThan(sMapPos, boundMin)) || any(greaterThanEqual(sMapPos, boundMax))) {
                                            break;
                                        }

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
                                            if (sMat != 0u) {
                                                shadowFactor = ambient;
                                                break;
                                            }
                                        }

                                        // STRICT MASK FOR SHADOWS TOO
                                        if (sSideDist.x < sSideDist.y) {
                                            if (sSideDist.x < sSideDist.z) sMask = vec3(1.0, 0.0, 0.0);
                                            else sMask = vec3(0.0, 0.0, 1.0);
                                        } else {
                                            if (sSideDist.y < sSideDist.z) sMask = vec3(0.0, 1.0, 0.0);
                                            else sMask = vec3(0.0, 0.0, 1.0);
                                        }

                                        sSideDist += sMask * sDeltaDist;
                                        sMapPos += ivec3(sMask) * sStepDir;
        }
    } else {
        shadowFactor = ambient;
    }

    vec3 litColor = albedo * (shadowFactor * ndotl + ambient * 0.5);

    // --- ТУМАН (FOG) ---
    float fogFactor = smoothstep(uRenderDistance * 0.6, uRenderDistance * 0.95, tEnd);
    FragColor = vec4(mix(litColor, skyColor, fogFactor), 1.0);

    vec4 clipPos = uProjection * uView * vec4(hitPos, 1.0);
    gl_FragDepth = (clipPos.z / clipPos.w) * 0.5 + 0.5;
}