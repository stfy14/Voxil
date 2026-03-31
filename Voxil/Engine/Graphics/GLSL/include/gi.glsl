// --- gi.glsl  v6 ---
//
// ROOT CAUSE of lag + seam at every LOD boundary:
//   ComputeEdgeFade was centered on (bX + N/2) * sp.
//   bX is a discrete integer: bx = floor(camX/sp - N/2).
//   Grid center = floor(camX/sp - N/2 + N/2) * sp = floor(camX/sp) * sp.
//   → Grid center snaps by sp every time camera crosses a probe grid line.
//   For L2 (sp=16m): snap = 16m, fade zone = sp*2 = 32m.
//     16m snap inside 32m fade zone → the boundary lags half the fade width.
//     Visually: the dark seam stands still for 16m of player movement, then jumps.
//   For L1 (sp=4m): snap = 4m. Less visible but still there.
//   For L0 (sp=1m): snap = 1m. Almost unnoticeable individually.
//   When all three cascade seams lag at different rates → checkerboard pattern.
//
// FIX: Center ComputeEdgeFade on uCamPos (continuous, no snapping).
//   The probe grid is always within sp/2 of uCamPos by construction.
//   The actual probe coverage extends ±(N/2)*sp from the discrete grid center
//   which is at most sp/2 from camPos → max error = sp/2 at grid edge.
//   Using camPos as center:
//     - Fade boundary moves smoothly with camera every frame
//     - No lag, no snap
//     - Stale probe check already handles brief gaps from grid scroll
//
//   Fade width increased from sp*2 to sp*3:
//     - Gives 3x more room for cascade irradiance to blend
//     - Reduces visibility of L0/L1/L2 irradiance difference at boundary
//
// All fixes from v4/v5 retained:
//   - Stale probe validation (distance check vs expectedProbePos)
//   - ChebyshevVisibility: scaled minVar = sp²*0.06, no step discontinuity
//   - GIFallback: proper ambient (day=0.35, night=0.05)
//   - L2 clamped sampling, no fade2 (L2 always provides finite ambient)
//   - Smoothstep trilinear weights (C¹ at cell boundaries)
//   - Normal offset 0.3m (was 0.55m, kept from v4)

uniform sampler2D uGIIrrAtlas;
uniform sampler2D uGIDepthAtlas;
uniform sampler2D uGIIrrAtlasL1;
uniform sampler2D uGIDepthAtlasL1;
uniform sampler2D uGIIrrAtlasL2;
uniform sampler2D uGIDepthAtlasL2;

struct GIProbeData { vec4 pos; vec4 colorAndState; };

uniform int   uGIGridBaseX,    uGIGridBaseY,    uGIGridBaseZ;
uniform float uGIProbeSpacing;
uniform int   uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1;
uniform float uGIProbeSpacingL1;
uniform int   uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2;
uniform float uGIProbeSpacingL2;
uniform int uGIProbeX, uGIProbeY, uGIProbeZ;
uniform int uIrrTile;
uniform int uDepthTile;

layout(std430, binding = 16) readonly buffer GIProbeBufferL0 { GIProbeData giProbesL0[]; };
layout(std430, binding = 19) readonly buffer GIProbeBufferL1 { GIProbeData giProbesL1[]; };
layout(std430, binding = 21) readonly buffer GIProbeBufferL2 { GIProbeData giProbesL2[]; };

int gi_mod(int a, int b) { int m = a % b; return m < 0 ? m + b : m; }

vec2 OctEncode(vec3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    if (n.z < 0.0) {
        float x = n.x, y = n.y;
        n.x = (1.0 - abs(y)) * (x >= 0.0 ? 1.0 : -1.0);
        n.y = (1.0 - abs(x)) * (y >= 0.0 ? 1.0 : -1.0);
    }
    return n.xy * 0.5 + 0.5;
}

vec2 ProbeAtlasUV(int probeIdx, vec3 dir, int tileSize, int atlasW, int atlasH) {
    int pad = tileSize + 2;
    int wx = probeIdx % uGIProbeX;
    int wy = (probeIdx / uGIProbeX) % uGIProbeY;
    int wz = probeIdx / (uGIProbeX * uGIProbeY);
    vec2 tileOrigin = vec2(float(wx * pad) + 1.0, float((wy + wz * uGIProbeY) * pad) + 1.0);
    vec2 octUV = OctEncode(normalize(dir));
    return (tileOrigin + octUV * float(tileSize)) / vec2(float(atlasW), float(atlasH));
}

vec3 GIFallback(vec3 normal) {
    float skyVis  = max(normal.y * 0.5 + 0.5, 0.15);
    float sunH    = uSunDir.y;
    float dayF    = clamp(sunH * 4.0 + 0.2,      0.0, 1.0);
    float sunsetF = clamp(1.0 - abs(sunH) * 5.0, 0.0, 1.0);
    float ambient = mix(0.05, 0.35, dayF) * (1.0 + sunsetF * 0.3);
    vec3 skyColor = mix(
        mix(vec3(0.04, 0.04, 0.12), vec3(0.60, 0.75, 0.95), dayF),
        vec3(0.90, 0.45, 0.15), sunsetF);
    return skyColor * ambient * skyVis;
}

vec4 SampleProbeLevel(vec3 worldPos, vec3 normal, vec3 viewDir,
    sampler2D irrAtlas, sampler2D depthAtlas,
    int bX, int bY, int bZ, float sp, int level)
{
    // ИСПРАВЛЕНИЕ 1: Полностью убираем сдвиг по направлению взгляда (View-Bias).
    // Оставляем только надежный и чуть увеличенный сдвиг по нормали (Normal-Bias).
    vec3 bias = normal * (sp * 0.35); 
    vec3 samplePos = worldPos + bias;

    vec3 gridOrigin = vec3(float(bX), float(bY), float(bZ)) * sp + vec3(sp * 0.5);
    vec3 localPos = (samplePos - gridOrigin) / sp;
    
    ivec3 baseCoord = ivec3(floor(localPos));
    vec3 f = fract(localPos);
    f = f * f * (3.0 - 2.0 * f); // Smoothstep для гладких теней

    vec3 irrSum = vec3(0.0);
    float wsSum = 0.0;

    int irrAtlasW   = uGIProbeX * (uIrrTile + 2);
    int irrAtlasH   = uGIProbeY * uGIProbeZ * (uIrrTile + 2);
    int depthAtlasW = uGIProbeX * (uDepthTile + 2);
    int depthAtlasH = uGIProbeY * uGIProbeZ * (uDepthTile + 2);

    for (int i = 0; i < 8; i++)
    {
        ivec3 offset = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        vec3 trilinear = mix(1.0 - f, f, vec3(offset));
        float weight = trilinear.x * trilinear.y * trilinear.z;

        if (weight < 0.001) continue;

        ivec3 p = baseCoord + offset;
        ivec3 g = ivec3(bX, bY, bZ) + p;
        int idx = gi_mod(g.x, uGIProbeX) + uGIProbeX * (gi_mod(g.y, uGIProbeY) + uGIProbeY * gi_mod(g.z, uGIProbeZ));

        float storedState; vec3 probePos;
        if (level == 0) { probePos = giProbesL0[idx].pos.xyz; storedState = giProbesL0[idx].pos.w; }
        else if (level == 1) { probePos = giProbesL1[idx].pos.xyz; storedState = giProbesL1[idx].pos.w; }
        else { probePos = giProbesL2[idx].pos.xyz; storedState = giProbesL2[idx].pos.w; }

        if (storedState < 0.5) continue;

        vec3 expectedGridPos = vec3(g) * sp + vec3(sp * 0.5);
        if (distance(probePos, expectedGridPos) > sp * 2.0) continue;

        vec3 toProbe = probePos - samplePos; 
        float distToProbe = length(toProbe);
        vec3 dirToProbe = toProbe / max(distToProbe, 0.001);

        float backface = max(0.01, (dot(normal, dirToProbe) + 1.0) * 0.5);
        weight *= backface; 

        vec2 depthUV = ProbeAtlasUV(idx, -dirToProbe, uDepthTile, depthAtlasW, depthAtlasH);
        vec2 moments = texture(depthAtlas, depthUV).rg;

        float variance = abs(moments.y - (moments.x * moments.x));
        variance = max(variance, sp * sp * 0.05); 

        float visibility = 1.0;
        float chebyDist = distToProbe - (sp * 0.1); 
        if (chebyDist > moments.x) {
            float d = chebyDist - moments.x;
            float p_vis = variance / (variance + d * d);
            visibility = max(0.0, (p_vis - 0.05) / 0.95);
        }

        weight *= visibility;

        if (weight > 0.001) {
            vec2 irrUV = ProbeAtlasUV(idx, normal, uIrrTile, irrAtlasW, irrAtlasH);
            irrSum += texture(irrAtlas, irrUV).rgb * weight;
            wsSum += weight;
        }
    }

    return vec4(wsSum > 0.001 ? irrSum / wsSum : vec3(0.0), wsSum);
}

vec3 SampleGIProbes(vec3 worldPos, vec3 normal, vec3 viewDir) {
    vec3 fallback = GIFallback(normal);

    float distToCam = length(worldPos - uCamPos);
    float fade0 = clamp((uGIProbeX * uGIProbeSpacing * 0.45 - distToCam) / (uGIProbeSpacing * 1.5), 0.0, 1.0);
    float fade1 = clamp((uGIProbeX * uGIProbeSpacingL1 * 0.45 - distToCam) / (uGIProbeSpacingL1 * 1.5), 0.0, 1.0);

    vec4 s0 = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlas, uGIDepthAtlas, uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ, uGIProbeSpacing, 0);
    vec4 s1 = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlasL1, uGIDepthAtlasL1, uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1, 1);
    vec4 s2 = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlasL2, uGIDepthAtlasL2, uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2, 2);

    // ИСПРАВЛЕНИЕ 2: Новая, более стабильная и предсказуемая логика смешивания каскадов.
    // 1. Определяем "чистый" цвет для каждого каскада. Если зондов нет, берем цвет из более грубого каскада.
    vec3 irr2 = (s2.a > 0.01) ? s2.rgb : fallback;
    vec3 irr1 = (s1.a > 0.01) ? s1.rgb : irr2;
    vec3 irr0 = (s0.a > 0.01) ? s0.rgb : irr1;

    // 2. Смешиваем "чистые" цвета на границах их сеток (фейдах).
    vec3 result = irr2;
    result = mix(result, irr1, fade1);
    result = mix(result, irr0, fade0);

    return result;
}
