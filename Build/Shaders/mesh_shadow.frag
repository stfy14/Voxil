#version 450 core

out vec4 FragColor;

in vec3 vFragPos;
in vec3 vColor;

uniform vec3 uWorldOffset; 
uniform int uWorldSize;

struct SvoNode {
    int childPtr;
    int data;
};

layout(std430, binding = 0) buffer Nodes {
    SvoNode nodes[];
};

bool intersectBox(vec3 rayOrigin, vec3 invDir, vec3 boxMin, vec3 boxMax, out float tNear, out float tFar) {
    vec3 tBot = (boxMin - rayOrigin) * invDir;
    vec3 tTop = (boxMax - rayOrigin) * invDir;
    vec3 tMin = min(tTop, tBot);
    vec3 tMax = max(tTop, tBot);
    float t0 = max(tMin.x, max(tMin.y, tMin.z));
    float t1 = min(tMax.x, min(tMax.y, tMax.z));
    tNear = t0;
    tFar = t1;
    return t1 > max(t0, 0.0);
}

float calculateShadow(vec3 origin, vec3 lightDir, vec3 normal) {
    vec3 rayOrigin = origin + normal * 0.001 + lightDir * 0.001;
    
    vec3 rayDir = lightDir;
    if (abs(rayDir.x) < 1e-5) rayDir.x = 1e-5;
    if (abs(rayDir.y) < 1e-5) rayDir.y = 1e-5;
    if (abs(rayDir.z) < 1e-5) rayDir.z = 1e-5;
    vec3 invRayDir = 1.0 / rayDir;

    float tNear, tFar;
    if (!intersectBox(rayOrigin, invRayDir, vec3(0.0), vec3(uWorldSize), tNear, tFar)) {
        return 1.0;
    }
    
    float t = max(tNear, 0.0);
    
    for(int i=0; i<128; i++) {
        if(t > tFar) return 1.0;
        
        vec3 p = rayOrigin + rayDir * t;
        
        float bias = 1e-4 + t * 2e-5;
        vec3 lookupPos = p + rayDir * bias;
        
        int currNode = 0;
        float currSize = float(uWorldSize);
        vec3 nodePos = vec3(0.0);
        
        for(int d=0; d<10; d++) {
            SvoNode node = nodes[currNode];
            if (node.childPtr == -1) {
                if (node.data > 0) return 0.0;
                break;
            }
            float halfSize = currSize * 0.5;
            vec3 center = nodePos + vec3(halfSize);
            vec3 s = step(center, lookupPos);
            int childIdx = int(s.x) | (int(s.y) << 1) | (int(s.z) << 2);
            nodePos += s * halfSize;
            
            if ((node.data & (1 << childIdx)) == 0) {
                currSize = halfSize;
                break;
            }
            currNode = node.childPtr + childIdx;
            currSize = halfSize;
        }
        
        vec3 t1 = (nodePos - rayOrigin) * invRayDir;
        vec3 t2 = (nodePos + vec3(currSize) - rayOrigin) * invRayDir;
        vec3 tMax = max(t1, t2);
        float distToExit = min(min(tMax.x, tMax.y), tMax.z);
        
        t = max(t + bias * 2.0, distToExit + bias); 
    }
    return 1.0;
}

void main() {
    vec3 xTangent = dFdx(vFragPos);
    vec3 yTangent = dFdy(vFragPos);
    vec3 normal = normalize(cross(xTangent, yTangent));

    vec3 lightDir = normalize(vec3(0.5, 1.0, 0.3));
    float ambient = 0.3;
    float diffuse = 0.7;
    
    float nDotL = max(dot(normal, lightDir), 0.0);
    float shadow = 1.0;
    
    vec3 localPos = vFragPos - uWorldOffset;

    if (nDotL > 0.0) {
        shadow = calculateShadow(localPos, lightDir, normal);
    }
    
    vec3 finalLight = (ambient + diffuse * nDotL * shadow) * vec3(1.0);
    
    FragColor = vec4(vColor * finalLight, 1.0);
}