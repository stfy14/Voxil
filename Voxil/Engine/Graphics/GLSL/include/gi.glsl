// --- Engine/Graphics/GLSL/include/gi.glsl ---
// Трёхуровневый GI: L0=1м/~16м, L1=4м/~64м, L2=16м/~256м

// ── Point Lights (binding 18) ─────────────────────────────────
#define MAX_POINT_LIGHTS 32
struct PointLightData {
    vec4 posRadius;
    vec4 colorIntensity;
};
layout(std430, binding = 18) readonly buffer PointLightBuffer {
    PointLightData pointLights[];
};
uniform int uPointLightCount;

vec3 EvaluatePointLights(vec3 hitPos, vec3 normal) {
    vec3 total = vec3(0.0);
    for (int i = 0; i < uPointLightCount && i < MAX_POINT_LIGHTS; i++) {
        vec3  lPos  = pointLights[i].posRadius.xyz;
        float lRad  = pointLights[i].posRadius.w;
        vec3  lCol  = pointLights[i].colorIntensity.rgb;
        float lInt  = pointLights[i].colorIntensity.a;
        vec3  toL   = lPos - hitPos;
        float dist  = length(toL);
        if (dist > lRad) continue;
        float nDotL = max(0.0, dot(normal, toL / dist));
        float win   = max(0.0, 1.0 - dist / lRad);
        win = win * win * win * win;
        total += lCol * nDotL * (lInt / max(dist * dist, 0.01)) * win;
    }
    return total;
}

// ── Probe SSBOs ───────────────────────────────────────────────
// L0
layout(std430, binding = 16) readonly buffer GIProbePositions {
    vec4 giProbeData[];
};
layout(std430, binding = 17) readonly buffer GIProbeIrradiance {
    float giProbeIrr[];
};
// L1
layout(std430, binding = 19) readonly buffer GIProbePositionsL1 {
    vec4 giProbeDataL1[];
};
layout(std430, binding = 20) readonly buffer GIProbeIrradianceL1 {
    float giProbeIrrL1[];
};
// L2
layout(std430, binding = 21) readonly buffer GIProbePositionsL2 {
    vec4 giProbeDataL2[];
};
layout(std430, binding = 22) readonly buffer GIProbeIrradianceL2 {
    float giProbeIrrL2[];
};

// ── Uniforms ──────────────────────────────────────────────────
// L0
uniform int   uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ;
uniform float uGIProbeSpacing;
// L1
uniform int   uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1;
uniform float uGIProbeSpacingL1;
// L2
uniform int   uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2;
uniform float uGIProbeSpacingL2;
// Общие
uniform int   uGIProbeX, uGIProbeY, uGIProbeZ;

// ─────────────────────────────────────────────────────────────
int gi_mod(int a, int b) { int m = a % b; return m < 0 ? m + b : m; }

// ─────────────────────────────────────────────────────────────
// Живость зонда и EvalSH — по уровням
// ─────────────────────────────────────────────────────────────
float ProbeAlive(int idx)   { return giProbeData[idx * 2].w; }
float ProbeAliveL1(int idx) { return giProbeDataL1[idx * 2].w; }
float ProbeAliveL2(int idx) { return giProbeDataL2[idx * 2].w; }

vec3 EvalSH_impl(int b, vec3 n, float[12] irr_arr) {
    // Нельзя передать SSBO — используем макрос ниже
    return vec3(0.0); // заглушка
}

// Макрос-like функции для каждого уровня
vec3 EvalSH(int idx, vec3 n) {
    int b = idx * 12;
    const float A0 = 3.14159265, A1 = 2.09439510, PI = 3.14159265;
    float y0 = 0.282095, y1 = 0.488603*n.y, y2 = 0.488603*n.z, y3 = 0.488603*n.x;
    float r  = giProbeIrr[b+0]*y0*A0 + giProbeIrr[b+1]*y1*A1 + giProbeIrr[b+2]*y2*A1 + giProbeIrr[b+3]*y3*A1;
    float g  = giProbeIrr[b+4]*y0*A0 + giProbeIrr[b+5]*y1*A1 + giProbeIrr[b+6]*y2*A1 + giProbeIrr[b+7]*y3*A1;
    float b2 = giProbeIrr[b+8]*y0*A0 + giProbeIrr[b+9]*y1*A1 + giProbeIrr[b+10]*y2*A1+ giProbeIrr[b+11]*y3*A1;
    return max(vec3(0.0), vec3(r, g, b2) / PI);
}
vec3 EvalSH_L1(int idx, vec3 n) {
    int b = idx * 12;
    const float A0 = 3.14159265, A1 = 2.09439510, PI = 3.14159265;
    float y0 = 0.282095, y1 = 0.488603*n.y, y2 = 0.488603*n.z, y3 = 0.488603*n.x;
    float r  = giProbeIrrL1[b+0]*y0*A0 + giProbeIrrL1[b+1]*y1*A1 + giProbeIrrL1[b+2]*y2*A1 + giProbeIrrL1[b+3]*y3*A1;
    float g  = giProbeIrrL1[b+4]*y0*A0 + giProbeIrrL1[b+5]*y1*A1 + giProbeIrrL1[b+6]*y2*A1 + giProbeIrrL1[b+7]*y3*A1;
    float b2 = giProbeIrrL1[b+8]*y0*A0 + giProbeIrrL1[b+9]*y1*A1 + giProbeIrrL1[b+10]*y2*A1+ giProbeIrrL1[b+11]*y3*A1;
    return max(vec3(0.0), vec3(r, g, b2) / PI);
}
vec3 EvalSH_L2(int idx, vec3 n) {
    int b = idx * 12;
    const float A0 = 3.14159265, A1 = 2.09439510, PI = 3.14159265;
    float y0 = 0.282095, y1 = 0.488603*n.y, y2 = 0.488603*n.z, y3 = 0.488603*n.x;
    float r  = giProbeIrrL2[b+0]*y0*A0 + giProbeIrrL2[b+1]*y1*A1 + giProbeIrrL2[b+2]*y2*A1 + giProbeIrrL2[b+3]*y3*A1;
    float g  = giProbeIrrL2[b+4]*y0*A0 + giProbeIrrL2[b+5]*y1*A1 + giProbeIrrL2[b+6]*y2*A1 + giProbeIrrL2[b+7]*y3*A1;
    float b2 = giProbeIrrL2[b+8]*y0*A0 + giProbeIrrL2[b+9]*y1*A1 + giProbeIrrL2[b+10]*y2*A1+ giProbeIrrL2[b+11]*y3*A1;
    return max(vec3(0.0), vec3(r, g, b2) / PI);
}

// ─────────────────────────────────────────────────────────────
// Fallback
// ─────────────────────────────────────────────────────────────
vec3 GIFallback(vec3 normal) {
    float skyVis = max(normal.y * 0.5 + 0.5, 0.15);
    float t = clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0);
    return vec3(0.6, 0.7, 0.9) * max(mix(0.06, 0.30, t), 0.05) * skyVis;
}

// ─────────────────────────────────────────────────────────────
// Универсальный трилинейный сэмплер для одного уровня.
// Возвращает vec4: .rgb = irradiance, .a = суммарный вес (0 = нет живых зондов)
// Уровень задаётся через #define-параметры — используем 3 копии функции,
// т.к. GLSL не позволяет передавать SSBO-буферы как параметры.
// ─────────────────────────────────────────────────────────────

vec4 SampleLevel0(vec3 worldPos, vec3 normal, int bX, int bY, int bZ, float sp) {
    vec3 samplePos  = worldPos + normal * (sp * 0.5);
    vec3 gridOrigin = vec3(float(bX), float(bY), float(bZ)) * sp + vec3(sp * 0.5);
    vec3 localPos   = (samplePos - gridOrigin) / sp;
    localPos = clamp(localPos, vec3(0.0),
                     vec3(float(uGIProbeX)-1.0, float(uGIProbeY)-1.0, float(uGIProbeZ)-1.0));

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);
    vec3  fs = f * f * (3.0 - 2.0 * f);

    vec3  irr = vec3(0.0);
    float ws  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++)
    {
        ivec3 p = clamp(p0 + ivec3(dx,dy,dz),
                        ivec3(0), ivec3(uGIProbeX-1, uGIProbeY-1, uGIProbeZ-1));
        ivec3 g  = ivec3(bX, bY, bZ) + p;
        int idx  = gi_mod(g.x, uGIProbeX)
        + uGIProbeX * (gi_mod(g.y, uGIProbeY)
        + uGIProbeY *  gi_mod(g.z, uGIProbeZ));

        if (ProbeAlive(idx) < 0.5) continue;

        vec3  probeWorldPos = vec3(float(g.x), float(g.y), float(g.z)) * sp + vec3(sp * 0.5);
        vec3  probeDir      = probeWorldPos - samplePos;
        float visWeight     = max(0.05, dot(normal, normalize(probeDir)));

        float wx = (dx == 0) ? (1.0-fs.x) : fs.x;
        float wy = (dy == 0) ? (1.0-fs.y) : fs.y;
        float wz = (dz == 0) ? (1.0-fs.z) : fs.z;
        float w  = wx * wy * wz * visWeight;

        irr += EvalSH(idx, normal) * w;
        ws  += w;
    }
    return vec4(irr, ws);
}

vec4 SampleLevel1(vec3 worldPos, vec3 normal, int bX, int bY, int bZ, float sp) {
    vec3 samplePos  = worldPos + normal * (sp * 0.5);
    vec3 gridOrigin = vec3(float(bX), float(bY), float(bZ)) * sp + vec3(sp * 0.5);
    vec3 localPos   = (samplePos - gridOrigin) / sp;
    localPos = clamp(localPos, vec3(0.0),
                     vec3(float(uGIProbeX)-1.0, float(uGIProbeY)-1.0, float(uGIProbeZ)-1.0));

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);
    vec3  fs = f * f * (3.0 - 2.0 * f);

    vec3  irr = vec3(0.0);
    float ws  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++)
    {
        ivec3 p = clamp(p0 + ivec3(dx,dy,dz),
                        ivec3(0), ivec3(uGIProbeX-1, uGIProbeY-1, uGIProbeZ-1));
        ivec3 g  = ivec3(bX, bY, bZ) + p;
        int idx  = gi_mod(g.x, uGIProbeX)
        + uGIProbeX * (gi_mod(g.y, uGIProbeY)
        + uGIProbeY *  gi_mod(g.z, uGIProbeZ));

        if (ProbeAliveL1(idx) < 0.5) continue;

        vec3  probeWorldPos = vec3(float(g.x), float(g.y), float(g.z)) * sp + vec3(sp * 0.5);
        vec3  probeDir      = probeWorldPos - samplePos;
        float visWeight     = max(0.05, dot(normal, normalize(probeDir)));

        float wx = (dx == 0) ? (1.0-fs.x) : fs.x;
        float wy = (dy == 0) ? (1.0-fs.y) : fs.y;
        float wz = (dz == 0) ? (1.0-fs.z) : fs.z;
        float w  = wx * wy * wz * visWeight;

        irr += EvalSH_L1(idx, normal) * w;
        ws  += w;
    }
    return vec4(irr, ws);
}

vec4 SampleLevel2(vec3 worldPos, vec3 normal, int bX, int bY, int bZ, float sp) {
    vec3 samplePos  = worldPos + normal * (sp * 0.5);
    vec3 gridOrigin = vec3(float(bX), float(bY), float(bZ)) * sp + vec3(sp * 0.5);
    vec3 localPos   = (samplePos - gridOrigin) / sp;
    localPos = clamp(localPos, vec3(0.0),
                     vec3(float(uGIProbeX)-1.0, float(uGIProbeY)-1.0, float(uGIProbeZ)-1.0));

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);
    vec3  fs = f * f * (3.0 - 2.0 * f);

    vec3  irr = vec3(0.0);
    float ws  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++)
    {
        ivec3 p = clamp(p0 + ivec3(dx,dy,dz),
                        ivec3(0), ivec3(uGIProbeX-1, uGIProbeY-1, uGIProbeZ-1));
        ivec3 g  = ivec3(bX, bY, bZ) + p;
        int idx  = gi_mod(g.x, uGIProbeX)
        + uGIProbeX * (gi_mod(g.y, uGIProbeY)
        + uGIProbeY *  gi_mod(g.z, uGIProbeZ));

        if (ProbeAliveL2(idx) < 0.5) continue;

        vec3  probeWorldPos = vec3(float(g.x), float(g.y), float(g.z)) * sp + vec3(sp * 0.5);
        vec3  probeDir      = probeWorldPos - samplePos;
        float visWeight     = max(0.05, dot(normal, normalize(probeDir)));

        float wx = (dx == 0) ? (1.0-fs.x) : fs.x;
        float wy = (dy == 0) ? (1.0-fs.y) : fs.y;
        float wz = (dz == 0) ? (1.0-fs.z) : fs.z;
        float w  = wx * wy * wz * visWeight;

        irr += EvalSH_L2(idx, normal) * w;
        ws  += w;
    }
    return vec4(irr, ws);
}

// ─────────────────────────────────────────────────────────────
// Вспомогательная: fade на краях сетки
// ─────────────────────────────────────────────────────────────
float ComputeEdgeFade(vec3 worldPos, int bX, int bY, int bZ, float sp) {
    vec3 gridCenter = (vec3(float(bX), float(bY), float(bZ))
    + vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * 0.5) * sp;
    vec3 halfExt    = vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * sp * 0.5;
    vec3 distEdge   = halfExt - abs(worldPos - gridCenter);
    return clamp(min(min(distEdge.x, distEdge.y), distEdge.z) / (sp * 2.0), 0.0, 1.0);
}

// ─────────────────────────────────────────────────────────────
// ГЛАВНАЯ ФУНКЦИЯ: мульти-уровневый сэмплинг с блендингом
// ─────────────────────────────────────────────────────────────
vec3 SampleGIProbes(vec3 worldPos, vec3 normal) {
    vec3 fallback = GIFallback(normal);

    vec4  s0    = SampleLevel0(worldPos, normal,
                               uGIGridBaseX,    uGIGridBaseY,    uGIGridBaseZ,    uGIProbeSpacing);
    float fade0 = ComputeEdgeFade(worldPos,
                                  uGIGridBaseX,    uGIGridBaseY,    uGIGridBaseZ,    uGIProbeSpacing);
    vec3  irr0  = (s0.a > 0.001) ? s0.rgb / s0.a : fallback;

    vec4  s1    = SampleLevel1(worldPos, normal,
                               uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1);
    float fade1 = ComputeEdgeFade(worldPos,
                                  uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1);
    vec3  irr1  = (s1.a > 0.001) ? s1.rgb / s1.a : fallback;

    vec4  s2    = SampleLevel2(worldPos, normal,
                               uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2);
    float fade2 = ComputeEdgeFade(worldPos,
                                  uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2);
    vec3  irr2  = (s2.a > 0.001) ? s2.rgb / s2.a : fallback;

    vec3 result = mix(fallback, irr2, fade2);
    result      = mix(result,   irr1, fade1);
    result      = mix(result,   irr0, fade0);

    return result;
}