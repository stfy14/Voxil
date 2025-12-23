#version 450 core
out vec4 FragColor;
in vec2 uv;

uniform vec3 uCamPos;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uRenderDistance;

uniform int uBoundMinX;
uniform int uBoundMinY;
uniform int uBoundMinZ;
uniform int uBoundMaxX;
uniform int uBoundMaxY;
uniform int uBoundMaxZ;
uniform int uObjectCount;

const int CHUNK_SIZE = 16;
const ivec3 PAGE_TABLE_SIZE = ivec3(512, 16, 512);

layout(binding = 0, r32i) uniform iimage3D uPageTable;
layout(std430, binding = 1) buffer VoxelSSBO { uint packedVoxels[]; };
layout(binding = 1) uniform isampler3D uObjectGrid;

struct DynamicObject {
    mat4 model;
    vec4 color;
    vec4 boxMin;
    vec4 boxMax;
};
layout(std430, binding = 2) buffer DynObjects { DynamicObject dynObjects[]; };

uniform vec3 uGridOrigin;
uniform float uGridStep;
uniform int uGridSize;

vec3 GetColor(uint id) {
    if (id == 1u) return vec3(0.55, 0.27, 0.07);
    if (id == 2u) return vec3(0.5, 0.5, 0.5);
    if (id == 3u) return vec3(0.4, 0.26, 0.13);
    if (id == 4u) return vec3(0.2, 0.4, 0.8);
    return vec3(1, 0, 1);
}

vec2 IntersectAABB(vec3 ro, vec3 rd, vec3 bmin, vec3 bmax, out vec3 outNormal) {
    vec3 invDir = 1.0 / rd;
    vec3 t0 = (bmin - ro) * invDir;
    vec3 t1 = (bmax - ro) * invDir;
    
    vec3 tmin_vec = min(t0, t1);
    vec3 tmax_vec = max(t0, t1);

    float tmin = max(max(tmin_vec.x, tmin_vec.y), tmin_vec.z);
    float tmax = min(min(tmax_vec.x, tmax_vec.y), tmax_vec.z);

    if (tmin > tmax || tmax < 0.0) {
        outNormal = vec3(0.0);
        return vec2(-1.0);
    }
    
    vec3 n = step(tmin_vec.yzx, tmin_vec.xyz) * step(tmin_vec.zxy, tmin_vec.xyz);
    outNormal = -n * sign(rd);
    
    return vec2(tmin, tmax);
}

float CheckDynamicHit(int objIndex, vec3 worldRo, vec3 worldRd, out uint outMat, out vec3 outLocalNormal) {
    DynamicObject obj = dynObjects[objIndex];
    mat4 invModel = inverse(obj.model);
    vec3 localRo = (invModel * vec4(worldRo, 1.0)).xyz;
    vec3 localRd = (invModel * vec4(worldRd, 0.0)).xyz;

    vec2 t = IntersectAABB(localRo, localRd, obj.boxMin.xyz, obj.boxMax.xyz, outLocalNormal);
    float tNear = t.x;

    if (tNear >= 0.0) {
        outMat = 2u; 
        return tNear;
    }

    outMat = 0u;
    return -1.0;
}

void main() {
    vec2 pos = uv * 2.0 - 1.0;
    vec4 target = inverse(uProjection) * vec4(pos, 1.0, 1.0);
    vec3 rayDir = normalize((inverse(uView) * vec4(target.xyz, 0.0)).xyz);
    
    if (abs(rayDir.x) < 1e-6) rayDir.x = 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = 1e-6;

    float tDynamicHit = uRenderDistance;
    int bestObjID = -1;
    vec3 bestLocalNormal = vec3(0);
    uint bestDynMat = 0u; 

    for (int i = 0; i < uObjectCount; i++) {
        uint dynMat;
        vec3 localN;
        float tHit = CheckDynamicHit(i, uCamPos, rayDir, dynMat, localN);
        if (tHit >= 0.0 && tHit < tDynamicHit) {
            tDynamicHit = tHit;
            bestObjID = i;
            bestLocalNormal = localN;
            bestDynMat = dynMat; 
        }
    }

    float tStaticHit = uRenderDistance;
    uint staticHitMat = 0u; 
    vec3 mask = vec3(0);
    
    ivec3 mapPos = ivec3(floor(uCamPos));
    vec3 deltaDist = abs(1.0 / rayDir);
    ivec3 stepDir = ivec3(sign(rayDir));
    vec3 sideDist;
    sideDist.x = (sign(rayDir.x) * (mapPos.x - uCamPos.x) + (sign(rayDir.x) * 0.5) + 0.5) * deltaDist.x;
    sideDist.y = (sign(rayDir.y) * (mapPos.y - uCamPos.y) + (sign(rayDir.y) * 0.5) + 0.5) * deltaDist.y;
    sideDist.z = (sign(rayDir.z) * (mapPos.z - uCamPos.z) + (sign(rayDir.z) * 0.5) + 0.5) * deltaDist.z;

    int cachedChunkIdx = -1;
    ivec3 currentChunkCoord = ivec3(-99999);
    ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
    ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

    for (int i = 0; i < 2000; i++) {
        float tNextStep = min(sideDist.x, min(sideDist.y, sideDist.z));
        if (tNextStep > tDynamicHit || tNextStep > uRenderDistance) break;

        bool isStaticCoordValid = !any(lessThan(mapPos, boundMin * 16)) && !any(greaterThanEqual(mapPos, boundMax * 16));
        
        if (isStaticCoordValid) {
            ivec3 chunkCoord = mapPos >> 4;
            if (chunkCoord != currentChunkCoord) {
                currentChunkCoord = chunkCoord;
                cachedChunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
            }

            if (cachedChunkIdx != -1) {
                ivec3 local = mapPos & 15;
                int voxelLinear = local.x + 16 * (local.y + 16 * local.z);
                uint packedVal = packedVoxels[cachedChunkIdx + (voxelLinear >> 2)];
                uint staticMat = (packedVal >> ((voxelLinear & 3) * 8)) & 0xFFu;
                if (staticMat != 0u) {
                    tStaticHit = tNextStep;
                    staticHitMat = staticMat; 
                    break; 
                }
            }
        }
        
        mask = step(sideDist.xyz, sideDist.yzx) * step(sideDist.xyz, sideDist.zxy);
        sideDist += mask * deltaDist;
        mapPos += ivec3(mask) * stepDir;
    }

    float tEnd = min(tStaticHit, tDynamicHit);

    if (tEnd < uRenderDistance) {
        vec3 normal;
        vec3 finalColor;

        if (tDynamicHit < tStaticHit) { 
            mat3 normalMatrix = transpose(inverse(mat3(dynObjects[bestObjID].model)));
            normal = normalize(normalMatrix * bestLocalNormal);
            finalColor = GetColor(bestDynMat); 
        } else { 
            if (mask.x > 0.0) normal.x = -stepDir.x;
            else if (mask.y > 0.0) normal.y = -stepDir.y;
            else normal.z = -stepDir.z;
            finalColor = GetColor(staticHitMat); 
        }
        
        vec3 hitPos = uCamPos + rayDir * tEnd;
        vec3 lightDir = normalize(vec3(0.3, 0.9, 0.4));
        float diff = max(dot(normal, lightDir), 0.0);
        float ambient = 0.5;
        vec3 litColor = finalColor * (ambient + diff * 0.5);
        
        float fogFactor = smoothstep(uRenderDistance * 0.7, uRenderDistance, tEnd);
        FragColor = vec4(mix(litColor, vec3(0.53, 0.81, 0.92), fogFactor), 1.0);
        
        vec4 clipPos = uProjection * uView * vec4(hitPos, 1.0);
        gl_FragDepth = clipPos.z / clipPos.w * 0.5 + 0.5;
    } else {
        FragColor = vec4(0.53, 0.81, 0.92, 1.0);
        gl_FragDepth = 1.0;
    }
}