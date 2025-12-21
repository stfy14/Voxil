#version 450 core
out vec4 FragColor;
in vec2 uv;

uniform vec3 uCamPos;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uRenderDistance;

uniform int uBoundMinX, uBoundMinY, uBoundMinZ;
uniform int uBoundMaxX, uBoundMaxY, uBoundMaxZ;

const int CHUNK_SIZE = 16;
const ivec3 PAGE_TABLE_SIZE = ivec3(512, 16, 512);

layout(binding = 0, r32i) uniform iimage3D uPageTable;

layout(std430, binding = 1) buffer VoxelSSBO {
    uint packedVoxels[];
};

struct DynamicObject {
    mat4 invModel;
    vec4 color;
    vec4 boxMin;
    vec4 boxMax;
};
layout(std430, binding = 2) buffer DynObjects {
    DynamicObject dynObjects[];
};
uniform int uDynamicObjectCount;

struct Ray { vec3 org; vec3 dir; vec3 invDir; };

bool intersectBox(Ray r, vec3 boxMin, vec3 boxMax, out float tNear, out float tFar) {
    vec3 tMin = (boxMin - r.org) * r.invDir;
    vec3 tMax = (boxMax - r.org) * r.invDir;
    vec3 t1 = min(tMin, tMax);
    vec3 t2 = max(tMin, tMax);
    tNear = max(max(t1.x, t1.y), t1.z);
    tFar = min(min(t2.x, t2.y), t2.z);
    return tFar >= tNear && tFar > 0.0;
}

vec3 getMaterialColor(uint mat) {
    if (mat == 1) return vec3(0.55, 0.27, 0.07); 
    if (mat == 2) return vec3(0.5, 0.5, 0.5);    
    if (mat == 3) return vec3(0.4, 0.26, 0.13);  
    if (mat == 4) return vec3(0.2, 0.4, 0.8);    
    return vec3(1.0, 0.0, 1.0);
}

float traceDynamic(Ray ray, float maxDist, out vec3 outNormal, out vec3 outColor) {
    float closestT = maxDist;
    bool hitAny = false;
    for(int i = 0; i < uDynamicObjectCount; i++) {
        DynamicObject obj = dynObjects[i];
        vec3 localOrg = (obj.invModel * vec4(ray.org, 1.0)).xyz;
        vec3 localDir = (obj.invModel * vec4(ray.dir, 0.0)).xyz;
        float tNear, tFar;

        if (intersectBox(Ray(localOrg, localDir, 1.0/(localDir+vec3(1e-6))), obj.boxMin.xyz, obj.boxMax.xyz, tNear, tFar)) {
            if (tNear < closestT && tFar > 0.0) {
                closestT = max(0.0, tNear);
                hitAny = true;
                outColor = obj.color.rgb;

                vec3 hp = localOrg + localDir * closestT;
                vec3 c = (obj.boxMin.xyz + obj.boxMax.xyz)*0.5;
                vec3 hs = (obj.boxMax.xyz - obj.boxMin.xyz)*0.5;
                vec3 d = abs(hp - c) - hs;
                vec3 n = vec3(0);
                if (d.x > d.y && d.x > d.z) n.x = sign(hp.x - c.x);
                else if (d.y > d.z) n.y = sign(hp.y - c.y);
                else n.z = sign(hp.z - c.z);

                outNormal = normalize((transpose(obj.invModel) * vec4(n, 0.0)).xyz);
            }
        }
    }
    return hitAny ? closestT : -1.0;
}

void main() {
    vec2 pos = uv * 2.0 - 1.0;
    vec4 target = inverse(uProjection) * vec4(pos, 1.0, 1.0);
    vec3 rayDir = normalize((inverse(uView) * vec4(target.xyz, 0.0)).xyz);

    Ray ray;
    ray.org = uCamPos;
    ray.dir = rayDir;

    if (abs(ray.dir.x) < 1e-5) ray.dir.x = 1e-5;
    if (abs(ray.dir.y) < 1e-5) ray.dir.y = 1e-5;
    if (abs(ray.dir.z) < 1e-5) ray.dir.z = 1e-5;
    ray.invDir = 1.0 / ray.dir;

    vec3 finalColor = vec3(0.53, 0.81, 0.92); 
    vec3 finalNormal = vec3(0,1,0);
    float finalT = uRenderDistance;
    bool hit = false;

    vec3 worldMin = vec3(uBoundMinX, uBoundMinY, uBoundMinZ) * float(CHUNK_SIZE);
    vec3 worldMax = vec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * float(CHUNK_SIZE);

    float tBoxNear, tBoxFar;
    bool hitWorld = intersectBox(ray, worldMin, worldMax, tBoxNear, tBoxFar);

    float tStart = 0.0;

    if (hitWorld) {
        tStart = max(0.0, tBoxNear);

        if (tStart > uRenderDistance) hitWorld = false;
    }

    if (hitWorld) {
        vec3 startPos = ray.org + ray.dir * (tStart + 0.001);

        ivec3 mapPos = ivec3(floor(startPos));
        vec3 deltaDist = abs(1.0 / rayDir);
        ivec3 stepDir = ivec3(sign(rayDir));

        vec3 sideDist;
        if (rayDir.x < 0) sideDist.x = (startPos.x - float(mapPos.x)) * deltaDist.x;
        else              sideDist.x = (float(mapPos.x) + 1.0 - startPos.x) * deltaDist.x;
        if (rayDir.y < 0) sideDist.y = (startPos.y - float(mapPos.y)) * deltaDist.y;
        else              sideDist.y = (float(mapPos.y) + 1.0 - startPos.y) * deltaDist.y;
        if (rayDir.z < 0) sideDist.z = (startPos.z - float(mapPos.z)) * deltaDist.z;
        else              sideDist.z = (float(mapPos.z) + 1.0 - startPos.z) * deltaDist.z;

        vec3 mask = vec3(0);

        ivec3 boundMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ);
        ivec3 boundMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ);

        int currentChunkOffset = -2;
        ivec3 currentChunkCoord = ivec3(-999999);

        float maxTrace = uRenderDistance - tStart;

        for (int i = 0; i < 2000; i++) { 

                                         ivec3 chunkCoord = mapPos >> 4;

                                         if (chunkCoord != currentChunkCoord) {
                                             currentChunkCoord = chunkCoord;
                                             if (any(lessThan(chunkCoord, boundMin)) || any(greaterThanEqual(chunkCoord, boundMax))) {
                                                 break;
                                             }
                                             ivec3 pageCoord = chunkCoord & (PAGE_TABLE_SIZE - 1);
                                             currentChunkOffset = imageLoad(uPageTable, pageCoord).r;
                                         }

                                         if (currentChunkOffset != -1) {
                                             ivec3 local = mapPos & 15;
                                             int voxelIdxLinear = local.x + 16 * (local.y + 16 * local.z);
                                             int uintIdx = currentChunkOffset + (voxelIdxLinear >> 2); 
                                             uint packedVal = packedVoxels[uintIdx];
                                             int byteShift = (voxelIdxLinear & 3) * 8;
                                             uint mat = (packedVal >> byteShift) & 0xFFu;
                                             if (mat > 0u) {
                                                 hit = true;
                                                 float voxelT;
                                                 if (mask.x > 0.5) voxelT = sideDist.x - deltaDist.x;
                                                 else if (mask.y > 0.5) voxelT = sideDist.y - deltaDist.y;
                                                 else voxelT = sideDist.z - deltaDist.z;

                                                 finalT = tStart + voxelT;

                                                 finalNormal = -vec3(stepDir) * mask;
                                                 finalColor = getMaterialColor(mat);
                                                 break;
                                             }
                                         }

                                         mask = step(sideDist.xyz, sideDist.yzx) * step(sideDist.xyz, sideDist.zxy);
                                         sideDist += mask * deltaDist;
                                         mapPos += ivec3(mask) * stepDir;

                                         if ((sideDist.x > maxTrace) && (sideDist.y > maxTrace) && (sideDist.z > maxTrace)) break;
        }
    }

    vec3 dn, dc;
    float td = traceDynamic(ray, finalT, dn, dc);
    if (td > 0.0 && td < finalT) {
        finalT = td;
        finalNormal = dn;
        finalColor = dc;
        hit = true;
    }

    if (hit) {
        vec3 light = normalize(vec3(0.5, 1.0, 0.3));
        float diff = max(dot(finalNormal, light), 0.2);

        float fogStart = uRenderDistance * 0.6;
        float fogEnd = uRenderDistance;
        float fog = smoothstep(fogStart, fogEnd, finalT);

        vec3 col = finalColor * diff;
        finalColor = mix(col, vec3(0.53, 0.81, 0.92), fog);

        FragColor = vec4(finalColor, 1.0);

        vec4 clip = uProjection * uView * vec4(ray.org + ray.dir * finalT, 1.0);
        gl_FragDepth = (clip.z / clip.w) * 0.5 + 0.5;
    } else {
        FragColor = vec4(0.53, 0.81, 0.92, 1.0);
        gl_FragDepth = 1.0;
    }
}