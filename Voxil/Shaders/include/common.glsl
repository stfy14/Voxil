// Uniforms
uniform vec3 uCamPos;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uRenderDistance;
uniform vec3 uSunDir;
uniform float uTime;
uniform int uMaxRaySteps;
uniform int uSoftShadowSamples;

// Culling / Bounds
uniform int uBoundMinX; uniform int uBoundMinY; uniform int uBoundMinZ;
uniform int uBoundMaxX; uniform int uBoundMaxY; uniform int uBoundMaxZ;

// Objects / Grid
uniform int uObjectCount;
uniform vec3 uGridOrigin;
uniform float uGridStep;
uniform int uGridSize;

// --- LINKED LIST GRID ---
layout(binding = 1, r32i) uniform iimage3D uObjectGridHead;

struct ListNode {
    uint objectID;
    int nextNode;
};

layout(std430, binding = 3) buffer LinkedList {
    ListNode listNodes[];
};
// -----------------------------------------------------

uniform sampler2D uNoiseTexture;

const ivec3 PAGE_TABLE_SIZE = ivec3(512, 16, 512);

// --- ИЗМЕНЕНИЕ: Тип uniform для uint ---
layout(binding = 0, r32ui) uniform uimage3D uPageTable;

layout(std430, binding = 1) buffer VoxelSSBO { uint packedVoxels[]; };

struct DynamicObject {
    mat4 model; mat4 invModel; vec4 color; vec4 boxMin; vec4 boxMax;
};
layout(std430, binding = 2) buffer DynObjects { DynamicObject dynObjects[]; };

// --- HELPERS ---

vec3 GetColor(uint id) {
    if (id == 1u) return vec3(0.45, 0.15, 0.05); // Dirt
    if (id == 2u) return vec3(0.50, 0.50, 0.55); // Stone
    if (id == 3u) return vec3(0.25, 0.12, 0.05); // Wood
    if (id == 4u) return vec3(0.15, 0.25, 0.60); // Water (Base)
    if (id == 5u) return vec3(0.15, 0.60, 0.05); // Grass
    return vec3(1.0, 0.0, 1.0); // Error
}

float IGN(vec2 p) {
    vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
    return fract(magic.z * fract(dot(p, magic.xy)));
}

float random(vec2 p) {
    vec3 p3  = fract(vec3(p.xyx) * .1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}