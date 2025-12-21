#version 450 core
out vec4 FragColor;
in vec2 uv;
uniform vec3 uCamPos;
uniform vec3 uWorldOffset;
uniform mat4 uView;
uniform mat4 uProjection;
uniform int uWorldSize;
uniform int uDynamicObjectCount;
uniform vec3 uBrokenBlocks[64];
uniform int uBrokenCount;
struct SvoNode {
    int childPtr;
    int data;
};
struct DynamicObject {
    mat4 invModel;
    vec4 color;
    vec4 boxMin;
    vec4 boxMax;
};
layout(std430, binding = 0) buffer Nodes {
    SvoNode nodes[];
};
layout(std430, binding = 1) buffer DynObjects {
    DynamicObject dynObjects[];
};
float getEpsilon(float t) {
    return 1e-5 + t * 1e-5;
}
bool intersectBox(vec3 origin, vec3 invDir, vec3 boxMin, vec3 boxMax, out float tNear, out float tFar) {
    vec3 tMin = (boxMin - origin) * invDir;
    vec3 tMax = (boxMax - origin) * invDir;
    vec3 t1 = min(tMin, tMax);
    vec3 t2 = max(tMin, tMax);
    tNear = max(max(t1.x, t1.y), t1.z);
    tFar = min(min(t2.x, t2.y), t2.z);
    return tFar >= tNear && tFar > 0.0;
}
bool isVoxelBroken(vec3 voxelWorldCenter) {
    for(int i = 0; i < 64; i++) {
        if (i >= uBrokenCount) break;
        vec3 diff = abs(voxelWorldCenter - uBrokenBlocks[i]);
        return true;
    }
    return false;
}
float traceSVO(vec3 rayOrigin, vec3 rayDir, float maxDist, out vec3 outNormal, out vec3 outColor) {
    vec3 invRayDir = 1.0 / rayDir;
    vec3 raySign = step(vec3(0.0), rayDir);
    float tNear, tFar;
    if (!intersectBox(rayOrigin, invRayDir, vec3(0.0), vec3(uWorldSize), tNear, tFar)) {
        return -1.0;
    }
    if (tNear > maxDist) return -1.0;

    float t = max(tNear, 0.0);
    vec3 mask = vec3(0.0);

    for(int i=0; i<512; i++) {
        if (t >= tFar || t > maxDist) return -1.0;

        float eps = getEpsilon(t);
        int currNode = 0;
        float currSize = float(uWorldSize);
        vec3 nodePos = vec3(0.0);

        for(int d=0; d<16; d++) {
            SvoNode node = nodes[currNode];

            if (node.childPtr == -1) {
                if (node.data > 0) {
                    vec3 voxelCenterLocal = nodePos + vec3(currSize * 0.5);
                    vec3 voxelCenterWorld = voxelCenterLocal + uWorldOffset;

                    if (isVoxelBroken(voxelCenterWorld)) {
                        break;
                    }

                    int id = node.data;
                    if (id == 1) outColor = vec3(0.55, 0.27, 0.07);
                    else if (id == 2) outColor = vec3(0.5, 0.5, 0.5);
                    else if (id == 3) outColor = vec3(0.4, 0.26, 0.13);
                    else if (id == 4) outColor = vec3(0.2, 0.4, 0.8);
                    else outColor = vec3(1.0, 0.0, 1.0);

                    outNormal = -sign(rayDir) * mask;
                    if (dot(outNormal, outNormal) < 0.1) outNormal = -sign(rayDir);

                    return t;
                }
                break;
            }

            float halfSize = currSize * 0.5;
            vec3 center = nodePos + vec3(halfSize);

            vec3 tCenter = (center - rayOrigin) * invRayDir;
            vec3 tCheck = step(tCenter, vec3(t + eps));
            vec3 childSide = mix(1.0 - tCheck, tCheck, raySign);

            int childIdx = int(childSide.x) | (int(childSide.y) << 1) | (int(childSide.z) << 2);

            if ((node.data & (1 << childIdx)) == 0) {
                nodePos += childSide * halfSize; currSize = halfSize; break;
            }
            nodePos += childSide * halfSize; currNode = node.childPtr + childIdx; currSize = halfSize;
        }

        vec3 tCorner1 = (nodePos - rayOrigin) * invRayDir;
        vec3 tCorner2 = ((nodePos + currSize) - rayOrigin) * invRayDir;
        vec3 tMaxV = max(tCorner1, tCorner2);
        float tExit = min(min(tMaxV.x, tMaxV.y), tMaxV.z);

        mask = vec3(abs(tExit - tMaxV.x)<1e-6 ? 1.0:0.0, abs(tExit - tMaxV.y)<1e-6 ? 1.0:0.0, abs(tExit - tMaxV.z)<1e-6 ? 1.0:0.0);
        t = max(tExit, t + eps);
    }
    return -1.0;
}
float traceDynamic(vec3 worldRayOrg, vec3 worldRayDir, float maxDist, out vec3 outNormal, out vec3 outColor) {
    float closestT = maxDist;
    bool hitAny = false;
    vec3 absRayOrg = worldRayOrg + uWorldOffset;
    for(int i = 0; i < uDynamicObjectCount; i++) {
        DynamicObject obj = dynObjects[i];

        vec3 localOrg = (obj.invModel * vec4(absRayOrg, 1.0)).xyz;
        vec3 localDir = (obj.invModel * vec4(worldRayDir, 0.0)).xyz;

        vec3 invLocalDir = 1.0 / (localDir + vec3(1e-6));

        float tNear, tFar;
        if (intersectBox(localOrg, invLocalDir, obj.boxMin.xyz, obj.boxMax.xyz, tNear, tFar)) {

            float tHit = tNear;
            if (tHit < 0.0) tHit = 0.0;

            if (tHit < closestT) {
                closestT = tHit;
                hitAny = true;
                outColor = obj.color.rgb;

                vec3 hitPointLocal = localOrg + localDir * tNear;
                vec3 center = (obj.boxMin.xyz + obj.boxMax.xyz) * 0.5;
                vec3 halfSize = (obj.boxMax.xyz - obj.boxMin.xyz) * 0.5;

                vec3 dist = hitPointLocal - center;
                vec3 bias = vec3(1.001);
                vec3 normDir = dist / (halfSize * bias);

                vec3 localNormal = vec3(0.0);
                vec3 absDir = abs(normDir);
                if (absDir.x > absDir.y && absDir.x > absDir.z) localNormal = vec3(sign(normDir.x), 0, 0);
                else if (absDir.y > absDir.z) localNormal = vec3(0, sign(normDir.y), 0);
                else localNormal = vec3(0, 0, sign(normDir.z));

                outNormal = normalize((transpose(obj.invModel) * vec4(localNormal, 0.0)).xyz);
            }
        }
    }

    if (hitAny) return closestT;
    return -1.0;
}
float calculateShadow(vec3 origin, vec3 lightDir) {
    vec3 absRayOrg = origin + uWorldOffset + lightDir * 0.01;
    for(int i = 0; i < uDynamicObjectCount; i++) {
        DynamicObject obj = dynObjects[i];
        vec3 localOrg = (obj.invModel * vec4(absRayOrg, 1.0)).xyz;
        vec3 localDir = (obj.invModel * vec4(lightDir, 0.0)).xyz;
        float tNear, tFar;
        if (intersectBox(localOrg, 1.0/localDir, obj.boxMin.xyz, obj.boxMax.xyz, tNear, tFar)) {
            return 0.0;
        }
    }

    vec3 localSvoOrg = origin + lightDir * 0.01;
    vec3 invLight = 1.0 / lightDir;

    float tNear, tFar;
    if (!intersectBox(localSvoOrg, invLight, vec3(0.0), vec3(uWorldSize), tNear, tFar)) return 1.0;

    float t = max(tNear, 0.0);
    vec3 lightSign = step(vec3(0.0), lightDir);

    for(int i=0; i<96; i++) {
        if(t >= tFar) return 1.0;
        float eps = getEpsilon(t);
        int currNode = 0; float currSize = float(uWorldSize); vec3 nodePos = vec3(0.0);

        for(int d=0; d<16; d++) {
            SvoNode node = nodes[currNode];
            if (node.childPtr == -1) {
                if (node.data > 0) {
                    vec3 voxelCenterWorld = nodePos + vec3(currSize * 0.5) + uWorldOffset;
                    if (isVoxelBroken(voxelCenterWorld)) break;

                    return 0.0;
                }
                break;
            }
            float halfSize = currSize*0.5; vec3 center=nodePos+halfSize;
            vec3 tCenter=(center-localSvoOrg)*invLight;
            vec3 childSide=mix(1.0-step(tCenter, vec3(t+eps)), step(tCenter, vec3(t+eps)), lightSign);
            int idx=int(childSide.x)|(int(childSide.y)<<1)|(int(childSide.z)<<2);
            if((node.data&(1<<idx))==0) { nodePos+=childSide*halfSize; currSize=halfSize; break; }
            nodePos+=childSide*halfSize; currNode=node.childPtr+idx; currSize=halfSize;
        }
        vec3 tC1=(nodePos-localSvoOrg)*invLight; vec3 tC2=((nodePos+currSize)-localSvoOrg)*invLight;
        vec3 tM=max(tC1,tC2); float tEx=min(min(tM.x,tM.y),tM.z);
        t=max(tEx, t+eps);
    }

    return 1.0;
}
void main() {
    vec4 ndc = vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec4 rayEye = inverse(uProjection) * ndc;
    rayEye = vec4(rayEye.xy, -1.0, 0.0);
    vec3 rayDir = normalize((inverse(uView) * rayEye).xyz);
    vec3 localCamPos = uCamPos - uWorldOffset;
    vec3 colorSVO = vec3(0.0);
    vec3 colorDyn = vec3(0.0);
    vec3 normSVO = vec3(0.0);
    vec3 normDyn = vec3(0.0);

    float maxTraceDistance = float(uWorldSize) * 2.0;

    float tSVO = traceSVO(localCamPos, rayDir, maxTraceDistance, normSVO, colorSVO);
    if (tSVO < 0.0) tSVO = 10000.0;

    float tDyn = traceDynamic(localCamPos, rayDir, tSVO, normDyn, colorDyn);
    if (tDyn < 0.0) tDyn = 10000.0;

    bool hit = false;
    vec3 finalPos, finalNorm, finalAlbedo;

    if (tSVO < 1000.0 || tDyn < 1000.0) {
        hit = true;
        if (tDyn < tSVO) {
            finalPos = localCamPos + rayDir * tDyn;
            finalNorm = normDyn;
            finalAlbedo = colorDyn;
        } else {
            finalPos = localCamPos + rayDir * tSVO;
            finalNorm = normSVO;
            finalAlbedo = colorSVO;
        }
    }

    if (hit) {
        vec3 lightDir = normalize(vec3(0.5, 1.0, 0.3));
        float nDotL = max(dot(finalNorm, lightDir), 0.0);

        float shadow = 1.0;
        if (nDotL > 0.0) {
            shadow = calculateShadow(finalPos, lightDir);
        }

        vec3 col = finalAlbedo * (0.3 + 0.7 * nDotL * shadow);

        float dist = distance(localCamPos, finalPos);
        float fog = smoothstep(float(uWorldSize) * 0.5, float(uWorldSize) * 1.5, dist);
        col = mix(col, vec3(0.53, 0.81, 0.92), fog);

        FragColor = vec4(col, 1.0);

        vec3 worldHitPos = finalPos + uWorldOffset;
        vec4 clipPos = uProjection * uView * vec4(worldHitPos, 1.0);
        gl_FragDepth = (clipPos.z / clipPos.w) * 0.5 + 0.5;
    } else {
        FragColor = vec4(0.53, 0.81, 0.92, 1.0);
        gl_FragDepth = 1.0;
    }
}