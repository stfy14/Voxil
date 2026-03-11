// --- Engine/Graphics/GLSL/include/common.glsl ---

// === ГЛАВНЫЕ UNIFORMS ===
uniform vec3 uCamPos;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uInvView;
uniform mat4 uInvProjection;
uniform bool uShowDebugHeatmap;

uniform float uRenderDistance;
uniform vec3 uSunDir;
uniform vec3 uMoonDir;
uniform float uTime;
uniform int uMaxRaySteps;
uniform int uSoftShadowSamples;
uniform int uObjectCount;

// === СЕТКА УСКОРЕНИЯ ===
uniform int uBoundMinX; uniform int uBoundMinY; uniform int uBoundMinZ;
uniform int uBoundMaxX; uniform int uBoundMaxY; uniform int uBoundMaxZ;
uniform vec3 uGridOrigin;
uniform float uGridStep;
uniform int uGridSize;

// === ТЕКСТУРЫ И БУФЕРЫ ===
uniform sampler2D uNoiseTexture;

// === BEAM OPTIMIZATION ===
uniform int uIsBeamPass;
uniform sampler2D uBeamTexture;

// === LOD ===
uniform float uLodDistance;
uniform int uDisableEffectsOnLOD;

#ifdef EDITOR_MODE
uniform vec3 uHoverVoxelMin;
uniform vec3 uHoverVoxelMax;
#endif

// ИСПРАВЛЕНИЕ: usampler3D вместо uimage3D - Включает аппаратный кэш!
layout(binding = 6) uniform usampler3D uPageTable;
const ivec3 PAGE_TABLE_SIZE = ivec3(512, 16, 512);

struct DynamicObject {
    mat4  model;
    mat4  invModel;
    vec4  color;
    vec4  boxMin;
    vec4  boxMax;
    uint  svoOffset;
    uint  gridSize;
    float voxelSize;
    uint  padding;
};

struct ListNode {
    uint objectID;
    int nextNode;
};

layout(std430, binding = 2) buffer DynObjects { DynamicObject dynObjects[]; };
layout(std430, binding = 3) buffer LinkedList { ListNode listNodes[]; };

// ИСПРАВЛЕНИЕ: isampler3D вместо iimage3D
layout(binding = 7) uniform isampler3D uObjectGridHead;

// === HELPERS ===
vec3 GetColor(uint id) {
    if (id == 1u) return vec3(0.45, 0.15, 0.05); // Dirt
    if (id == 2u) return vec3(0.50, 0.50, 0.55); // Stone
    if (id == 3u) return vec3(0.25, 0.12, 0.05); // Wood
    if (id == 4u) return vec3(0.15, 0.25, 0.60); // Water
    if (id == 5u) return vec3(0.15, 0.60, 0.05); // Grass
    if (id == 6u) return vec3(0.9,  0.1,  0.1);  // TNT
    if (id == 7u) return vec3(0.97, 0.82, 0.20); // Glow
    return vec3(1.0, 0.0, 1.0); // Error pink
}

float IGN(vec2 p) {
    vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
    return fract(magic.z * fract(dot(p, magic.xy)));
}