// --- Engine/Graphics/GLSL/include/gi.glsl ---
// Одноуровневый GI: spacing=1м, сетка 32x16x32
// Совместим с текущим GIProbeSystem.cs (SetSamplingUniforms)

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
// ProbeData: vec4 pos (xyz=позиция, w=живой 1.0/мёртвый 0.0) + vec4 colorAndState
// В буфере лежит именно ProbeData (2 vec4 = 8 float на зонд)
layout(std430, binding = 16) readonly buffer GIProbePositions {
    vec4 giProbeData[]; // чередуются: [0]=pos, [1]=colorAndState, [2]=pos, ...
};
layout(std430, binding = 17) readonly buffer GIProbeIrradiance {
    float giProbeIrr[];
};

// ── Uniforms (устанавливаются через SetSamplingUniforms) ──────
uniform int   uGIGridBaseX;
uniform int   uGIGridBaseY;
uniform int   uGIGridBaseZ;
uniform float uGIProbeSpacing;
uniform int   uGIProbeX;
uniform int   uGIProbeY;
uniform int   uGIProbeZ;

// ─────────────────────────────────────────────────────────────
int gi_mod(int a, int b) { int m = a % b; return m < 0 ? m + b : m; }

// Читаем w-компоненту pos (живость зонда)
// ProbeData состоит из двух vec4: pos (index*2+0) и colorAndState (index*2+1)
float ProbeAlive(int idx) {
    return giProbeData[idx * 2].w; // pos.w = 1.0 живой, 0.0 мёртвый
}

// SH L1 evaluation
vec3 EvalSH(int idx, vec3 n) {
    int b = idx * 12;
    const float A0 = 3.14159265, A1 = 2.09439510, PI = 3.14159265;
    float y0 = 0.282095;
    float y1 = 0.488603 * n.y;
    float y2 = 0.488603 * n.z;
    float y3 = 0.488603 * n.x;
    float r = giProbeIrr[b+0]*y0*A0 + giProbeIrr[b+1]*y1*A1 + giProbeIrr[b+2]*y2*A1 + giProbeIrr[b+3]*y3*A1;
    float g = giProbeIrr[b+4]*y0*A0 + giProbeIrr[b+5]*y1*A1 + giProbeIrr[b+6]*y2*A1 + giProbeIrr[b+7]*y3*A1;
    float b2= giProbeIrr[b+8]*y0*A0 + giProbeIrr[b+9]*y1*A1 + giProbeIrr[b+10]*y2*A1+ giProbeIrr[b+11]*y3*A1;
    return max(vec3(0.0), vec3(r, g, b2) / PI);
}

// Ambient fallback — никогда не возвращает полный ноль
vec3 GIFallback(vec3 normal) {
    float skyVis = max(normal.y * 0.5 + 0.5, 0.15);
    float t = clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0);
    return vec3(0.6, 0.7, 0.9) * max(mix(0.06, 0.30, t), 0.05) * skyVis;
}

// ─────────────────────────────────────────────────────────────
// ГЛАВНАЯ ФУНКЦИЯ: трилинейный сэмплинг с renormalization
// ─────────────────────────────────────────────────────────────
vec3 SampleGIProbes(vec3 worldPos, vec3 normal) {
    vec3 fallback = GIFallback(normal);

    // Небольшой bias от поверхности (фиксированный, не зависит от spacing)
    vec3 samplePos = worldPos + normal * 0.3;

    // Позиция зонда (0,0,0):
    //   compute записывает: targetPos = vec3(gx,gy,gz) * spacing + spacing*0.5
    // => gridOrigin = vec3(baseX,baseY,baseZ) * spacing + spacing*0.5
    float sp = uGIProbeSpacing;
    vec3 gridOrigin = vec3(float(uGIGridBaseX), float(uGIGridBaseY), float(uGIGridBaseZ)) * sp
    + vec3(sp * 0.5);

    vec3 localPos = (samplePos - gridOrigin) / sp;

    // Клямп внутри границ сетки
    localPos = clamp(localPos,
                     vec3(0.0),
                     vec3(float(uGIProbeX)-1.0, float(uGIProbeY)-1.0, float(uGIProbeZ)-1.0));

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);
    vec3  fs = f * f * (3.0 - 2.0 * f); // smoothstep — убирает резкие границы

    vec3  irr = vec3(0.0);
    float ws  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++)
    {
        ivec3 p = clamp(p0 + ivec3(dx,dy,dz),
                        ivec3(0),
                        ivec3(uGIProbeX-1, uGIProbeY-1, uGIProbeZ-1));

        // Кольцевой буфер: реальный индекс в массиве
        ivec3 g  = ivec3(uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ) + p;
        int modX = gi_mod(g.x, uGIProbeX);
        int modY = gi_mod(g.y, uGIProbeY);
        int modZ = gi_mod(g.z, uGIProbeZ);
        int idx  = modX + uGIProbeX * (modY + uGIProbeY * modZ);

        // Пропускаем мёртвые зонды (внутри блоков)
        if (ProbeAlive(idx) < 0.5) continue;

        float wx = (dx == 0) ? (1.0 - fs.x) : fs.x;
        float wy = (dy == 0) ? (1.0 - fs.y) : fs.y;
        float wz = (dz == 0) ? (1.0 - fs.z) : fs.z;
        float w  = wx * wy * wz;

        irr += EvalSH(idx, normal) * w;
        ws  += w;
    }

    // Renormalize: перераспределяем вес мёртвых зондов на живых
    // Это ключевое исправление — именно оно убирает тёмные прямоугольники
    if (ws < 0.001) return fallback;
    irr /= ws;

    // Плавный fade на краях сетки → переход к fallback без резких границ
    vec3 gridCenter = (vec3(float(uGIGridBaseX), float(uGIGridBaseY), float(uGIGridBaseZ))
    + vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * 0.5) * sp;
    vec3 halfExt = vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * sp * 0.5;
    vec3 distEdge = halfExt - abs(worldPos - gridCenter);
    float edgeFade = clamp(min(min(distEdge.x, distEdge.y), distEdge.z) / (sp * 2.0), 0.0, 1.0);

    return mix(fallback, irr, edgeFade);
}
