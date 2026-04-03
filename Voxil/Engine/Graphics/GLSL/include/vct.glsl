// --- vct.glsl ---
// Voxel Cone Tracing — include this in your shadow/lighting pass.
// Replaces SampleGIProbes() from the old gi.glsl.
//
// Usage:
//   #include "include/vct.glsl"
//   ...
//   vec3 gi = SampleGIVCT(worldPos, normal);
//   finalColor = albedo * (direct + gi);
//
// Required uniforms (set via VCTSystem.SetSamplingUniforms):
//   uVCTClipmapL0, uVCTClipmapL1, uVCTClipmapL2 — sampler3D (GL_REPEAT wrap)
//   uVCTOriginL0, uVCTOriginL1, uVCTOriginL2    — ivec3 (min-corner world texel)
//   uVCTClipmapSize                              — int (64)
//
// The clipmap must already be built by vct_clipmap_build.comp before this runs.

uniform sampler3D uVCTClipmapL0;
uniform sampler3D uVCTClipmapL1;
uniform sampler3D uVCTClipmapL2;

uniform int uVCTOriginL0X, uVCTOriginL0Y, uVCTOriginL0Z;
uniform int uVCTOriginL1X, uVCTOriginL1Y, uVCTOriginL1Z;
uniform int uVCTOriginL2X, uVCTOriginL2Y, uVCTOriginL2Z;

#define uVCTOriginL0 ivec3(uVCTOriginL0X, uVCTOriginL0Y, uVCTOriginL0Z)
#define uVCTOriginL1 ivec3(uVCTOriginL1X, uVCTOriginL1Y, uVCTOriginL1Z)
#define uVCTOriginL2 ivec3(uVCTOriginL2X, uVCTOriginL2Y, uVCTOriginL2Z)

uniform int uVCTClipmapSize; // 64

// === CONSTANTS ===

const float VCT_CELL_L0 = 2.0;
const float VCT_CELL_L1 = 8.0;
const float VCT_CELL_L2 = 32.0;

// Cone directions in local tangent space (Y = surface normal).
// 6 cones uniformly covering the upper hemisphere:
//   - 1 straight up (polar angle = 0°)
//   - 5 at 60° from up, evenly spaced in azimuth
const vec3 VCT_CONE_DIRS[6] = vec3[](
    vec3( 0.000000,  1.000000,  0.000000),
    vec3( 0.866025,  0.500000,  0.000000),
    vec3( 0.267617,  0.500000,  0.823639),
    vec3(-0.700629,  0.500000,  0.509037),
    vec3(-0.700629,  0.500000, -0.509037),
    vec3( 0.267617,  0.500000, -0.823639)
);

// Solid angle weights — must sum to 1.0
// Top cone covers more steradians than the ring cones.
const float VCT_CONE_WEIGHTS[6] = float[](0.25, 0.15, 0.15, 0.15, 0.15, 0.15);

// tan(30°) — half-angle of each cone.
// 30° gives good hemisphere coverage with 6 cones and minimal overlap.
const float VCT_TAN_HALF_ANGLE = 0.57735;

// === TBN CONSTRUCTION ===
// Builds a matrix that maps local Y → world normal.
// Used to orient cones along the surface normal.

mat3 VCT_GetTBN(vec3 N) {
    // Choose a helper vector that is not parallel to N
    vec3 helper = (abs(N.y) > 0.9999) ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 T = normalize(cross(helper, N));
    vec3 B = cross(N, T);
    // Columns: T=localX, N=localY, B=localZ in world space
    return mat3(T, N, B);
}

// === CLIPMAP SAMPLING ===
// Returns vec4(radiance.rgb, opacity) at world position `pos`.
// Selects cascade based on cone diameter to match VCT LOD logic.
// Falls back to next coarser cascade if pos is out of bounds.

vec4 VCT_Sample(vec3 pos, float coneDiam) {
    // Total world extent of each cascade
    float sizeL0 = float(uVCTClipmapSize) * VCT_CELL_L0; // 128m
    float sizeL1 = float(uVCTClipmapSize) * VCT_CELL_L1; // 512m
    float sizeL2 = float(uVCTClipmapSize) * VCT_CELL_L2; // 2048m

    // L0: use when cone is small AND position is within L0 bounds
    if (coneDiam < VCT_CELL_L1) {
        vec3 rel = pos - vec3(uVCTOriginL0) * VCT_CELL_L0;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL0)))) {
            vec4 s0 = texture(uVCTClipmapL0, fract(pos / sizeL0));
            // Blend к L1 когда coneDiam приближается к VCT_CELL_L1
            float blendFactor = smoothstep(VCT_CELL_L1 * 0.5, VCT_CELL_L1, coneDiam);
            if (blendFactor > 0.001) {
                vec3 rel1 = pos - vec3(uVCTOriginL1) * VCT_CELL_L1;
                if (all(greaterThanEqual(rel1, vec3(0.0))) && all(lessThan(rel1, vec3(sizeL1)))) {
                    vec4 s1 = texture(uVCTClipmapL1, fract(pos / sizeL1));
                    return mix(s0, s1, blendFactor);
                }
            }
            return s0;
        }
    }

    // L1: use when cone is medium OR L0 missed
    if (coneDiam < VCT_CELL_L2) {
        vec3 rel = pos - vec3(uVCTOriginL1) * VCT_CELL_L1;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL1)))) {
            return texture(uVCTClipmapL1, fract(pos / sizeL1));
        }
    }

    // L2: largest cascade
    {
        vec3 rel = pos - vec3(uVCTOriginL2) * VCT_CELL_L2;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL2)))) {
            return texture(uVCTClipmapL2, fract(pos / sizeL2));
        }
    }

    return vec4(0.0); // Fully outside all cascades
}

// === SINGLE CONE TRACE ===
// Traces one cone from `origin` in direction `dir`.
// Front-to-back compositing: stops when accumulated opacity >= 0.95.
// Returns vec4(gathered_radiance.rgb, total_opacity).

vec4 VCT_TraceCone(vec3 origin, vec3 dir, float startT, float maxDist) {
    vec4 accum = vec4(0.0);
    float t    = startT;

    while (t < maxDist && accum.a < 0.95) {
        float diam = max(2.0 * VCT_TAN_HALF_ANGLE * t, VCT_CELL_L0);
        vec4 s = VCT_Sample(origin + dir * t, diam);

        // Солнце убираем отсюда — оно теперь в клипмапе
        // s.rgb уже содержит освещённый цвет поверхности

        float alpha = s.a * (1.0 - accum.a);
        accum.rgb  += s.rgb * alpha;
        accum.a    += alpha;

        t += diam * 0.5;
    }

    return accum;
}

// === SKY FALLBACK ===
// Called when a cone exits all cascades without full occlusion.
// Matches the sky model in gi.glsl GIFallback().

vec3 VCT_SkyColor(vec3 dir) {
    float sunH    = uSunDir.y;
    float dayF    = clamp(sunH * 4.0 + 0.2,       0.0, 1.0);
    float sunsetF = clamp(1.0 - abs(sunH) * 5.0,  0.0, 1.0);
    
    // Поднимаем ночной ambient (было 0.05, стало 0.20), чтобы VCT конусы видели свет звезд
    float ambient = mix(0.04, 0.45, dayF);

    // Даем ночному небу красивый синий оттенок
    vec3 skyColor = mix(
        mix(vec3(0.08, 0.12, 0.25), vec3(0.60, 0.75, 0.95), dayF),
        vec3(0.90, 0.45, 0.15), sunsetF
    );

    // Attenuate sky contribution toward the horizon
    float skyVis = max(0.0, dot(dir, vec3(0.0, 1.0, 0.0)) * 0.5 + 0.5);
    return skyColor * ambient * skyVis;
}

// === MAIN ENTRY POINT ===
// Traces 6 diffuse cones from worldPos along the surface normal.
// Returns irradiance (NOT pre-multiplied by albedo — do that in composite).
//
// Parameters:
//   worldPos — surface hit position
//   normal   — surface normal (world space, unit vector)

vec3 SampleGIVCT(vec3 worldPos, vec3 normal) {
    mat3  tbn     = VCT_GetTBN(normal);
    float maxDist = float(uVCTClipmapSize) * VCT_CELL_L2;
    vec3  origin  = worldPos + normal * (VCT_CELL_L0 * 0.6);
    float startT  = VCT_CELL_L0;

    vec3 irradiance = vec3(0.0);

    for (int i = 0; i < 6; i++) {
        vec3 coneDir = normalize(tbn * VCT_CONE_DIRS[i]);
        vec4 result  = VCT_TraceCone(origin, coneDir, startT, maxDist);

        // Если конус улетел за пределы геометрии (result.a < 1.0)
        float skyFrac = 1.0 - result.a;
        
        // Математика физики: Небо находится сверху. 
        // Если конус смотрит вниз (в пол пещеры) и вылетел за предел клипмапа — он не получит небо.
        float upDot = dot(coneDir, vec3(0.0, 1.0, 0.0));
        float skyWeight = smoothstep(-0.1, 0.5, upDot); 
        
        result.rgb += VCT_SkyColor(coneDir) * skyFrac * skyWeight;

        irradiance += result.rgb * VCT_CONE_WEIGHTS[i];
    }

    return irradiance;
}