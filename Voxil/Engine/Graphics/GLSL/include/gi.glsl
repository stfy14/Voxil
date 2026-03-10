// --- Engine/Graphics/GLSL/include/gi.glsl ---
// DDGI: октаэдрические атласы irradiance + depth, Chebyshev visibility test

// ─────────────────────────────────────────────────────────────
// Point Lights (binding 18)
// ─────────────────────────────────────────────────────────────
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

// ─────────────────────────────────────────────────────────────
// Атласы irradiance (units 10/12/14) и depth (units 11/13/15)
// ─────────────────────────────────────────────────────────────
uniform sampler2D uGIIrrAtlas;      // L0 irradiance
uniform sampler2D uGIDepthAtlas;    // L0 depth
uniform sampler2D uGIIrrAtlasL1;   // L1 irradiance
uniform sampler2D uGIDepthAtlasL1; // L1 depth
uniform sampler2D uGIIrrAtlasL2;   // L2 irradiance
uniform sampler2D uGIDepthAtlasL2; // L2 depth

// ─────────────────────────────────────────────────────────────
// Uniforms сетки
// ─────────────────────────────────────────────────────────────
// L0
uniform int   uGIGridBaseX,    uGIGridBaseY,    uGIGridBaseZ;
uniform float uGIProbeSpacing;
// L1
uniform int   uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1;
uniform float uGIProbeSpacingL1;
// L2
uniform int   uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2;
uniform float uGIProbeSpacingL2;
// Общие
uniform int uGIProbeX, uGIProbeY, uGIProbeZ;
// Размеры тайлов
uniform int uIrrTile;    // = 8
uniform int uDepthTile;  // = 16

// ─────────────────────────────────────────────────────────────
// Утилиты
// ─────────────────────────────────────────────────────────────
int gi_mod(int a, int b) { int m = a % b; return m < 0 ? m + b : m; }

// ─────────────────────────────────────────────────────────────
// Октаэдрический маппинг (совпадает с compute шейдером)
// ─────────────────────────────────────────────────────────────
vec2 OctEncode(vec3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    if (n.z < 0.0) {
        float x = n.x, y = n.y;
        n.x = (1.0 - abs(y)) * (x >= 0.0 ? 1.0 : -1.0);
        n.y = (1.0 - abs(x)) * (y >= 0.0 ? 1.0 : -1.0);
    }
    return n.xy * 0.5 + 0.5;
}

// ─────────────────────────────────────────────────────────────
// UV в атласе для зонда probeIdx в направлении dir
// col = wx,  row = wy + wz * PROBE_Y
// ─────────────────────────────────────────────────────────────
vec2 ProbeAtlasUV(int probeIdx, vec3 dir, int tileSize, int atlasW, int atlasH) {
    int pad = tileSize + 2;

    int wx = probeIdx % uGIProbeX;
    int wy = (probeIdx / uGIProbeX) % uGIProbeY;
    int wz = probeIdx / (uGIProbeX * uGIProbeY);

    int col = wx;
    int row = wy + wz * uGIProbeY;

    // Начало тайла в пикселях (+1 = border)
    vec2 tileOrigin = vec2(float(col * pad) + 1.0, float(row * pad) + 1.0);

    // Позиция внутри тайла
    vec2 octUV   = OctEncode(normalize(dir));         // [0,1]
    vec2 pixelXY = tileOrigin + octUV * float(tileSize);

    return pixelXY / vec2(float(atlasW), float(atlasH));
}

// ─────────────────────────────────────────────────────────────
// Chebyshev visibility test
// depthMoments.x = mean dist, depthMoments.y = mean dist²
// dist           = расстояние от поверхности до зонда
// Возвращает [0,1]: 1 = зонд полностью видит, 0 = за стеной
// ─────────────────────────────────────────────────────────────
float ChebyshevVisibility(vec2 depthMoments, float dist) {
    // Зонд ближе чем ожидается → точно видит
    if (dist <= depthMoments.x) return 1.0;

    float variance = depthMoments.y - (depthMoments.x * depthMoments.x);
    variance = max(variance, 0.0001); // числовая стабильность

    float d    = dist - depthMoments.x;
    float cheb = variance / (variance + d * d);

    // Сглаживаем порог — убираем жёсткий переход
    return clamp((cheb - 0.1) / 0.9, 0.0, 1.0);
}

// ─────────────────────────────────────────────────────────────
// Fallback ambient — небо + ночь, никогда не ноль
// ─────────────────────────────────────────────────────────────
vec3 GIFallback(vec3 normal) {
    float skyVis = max(normal.y * 0.5 + 0.5, 0.15);
    float t = clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0);
    return vec3(0.6, 0.7, 0.9) * max(mix(0.06, 0.30, t), 0.05) * skyVis;
}

// ─────────────────────────────────────────────────────────────
// Fade на краях сетки → плавный переход к fallback / следующему LOD
// ─────────────────────────────────────────────────────────────
float ComputeEdgeFade(vec3 worldPos, int bX, int bY, int bZ, float sp) {
    vec3 gridCenter = (vec3(float(bX), float(bY), float(bZ))
    + vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * 0.5) * sp;
    vec3 halfExt    = vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * sp * 0.5;
    vec3 distEdge   = halfExt - abs(worldPos - gridCenter);
    return clamp(min(min(distEdge.x, distEdge.y), distEdge.z) / (sp * 2.0), 0.0, 1.0);
}

// ─────────────────────────────────────────────────────────────
// Трилинейный сэмплинг одного LOD уровня
// Возвращает vec4: .rgb = irradiance, .a = суммарный вес
// ─────────────────────────────────────────────────────────────
vec4 SampleProbeLevel(
    vec3 worldPos, vec3 normal,
    sampler2D irrAtlas, sampler2D depthAtlas,
    int bX, int bY, int bZ, float sp)
{
    // Bias от поверхности: масштабируем с шагом сетки
    vec3 samplePos  = worldPos + normal * (sp * 0.5);

    vec3 gridOrigin = vec3(float(bX), float(bY), float(bZ)) * sp + sp * 0.5;
    vec3 localPos   = (samplePos - gridOrigin) / sp;
    localPos = clamp(localPos,
                     vec3(0.0),
                     vec3(float(uGIProbeX)-1.0, float(uGIProbeY)-1.0, float(uGIProbeZ)-1.0));

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);
    // Smoothstep убирает резкие границы между зондами
    vec3  fs = f * f * (3.0 - 2.0 * f);

    // Размеры атласов в пикселях
    int irrAtlasW   = uGIProbeX * (uIrrTile   + 2);
    int irrAtlasH   = uGIProbeY * uGIProbeZ * (uIrrTile   + 2);
    int depthAtlasW = uGIProbeX * (uDepthTile + 2);
    int depthAtlasH = uGIProbeY * uGIProbeZ * (uDepthTile + 2);

    vec3  irrSum = vec3(0.0);
    float wsSum  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++)
    {
        ivec3 p = clamp(p0 + ivec3(dx, dy, dz),
                        ivec3(0), ivec3(uGIProbeX-1, uGIProbeY-1, uGIProbeZ-1));

        // Реальный индекс зонда через кольцевой буфер
        ivec3 g   = ivec3(bX, bY, bZ) + p;
        int   idx = gi_mod(g.x, uGIProbeX)
        + uGIProbeX * (gi_mod(g.y, uGIProbeY)
        + uGIProbeY *  gi_mod(g.z, uGIProbeZ));

        // Мировая позиция этого зонда
        vec3 probeWorldPos = vec3(float(g.x), float(g.y), float(g.z)) * sp + sp * 0.5;

        // ── Трилинейный вес ───────────────────────────────
        float wx = (dx == 0) ? (1.0 - fs.x) : fs.x;
        float wy = (dy == 0) ? (1.0 - fs.y) : fs.y;
        float wz = (dz == 0) ? (1.0 - fs.z) : fs.z;
        float w  = wx * wy * wz;

        // ── Wrap shading weight ───────────────────────────
        // Зонды "за спиной" нормали дают меньший вклад
        vec3  toProbe   = probeWorldPos - samplePos;
        float probeDist = length(toProbe) + 0.0001;
        float backFace  = (dot(normal, toProbe / probeDist) + 1.0) * 0.5;
        w *= max(backFace, 0.05); // минимум 5% чтобы не было чёрных дыр

        if (w < 0.0001) continue;

        // ── Chebyshev visibility (ключевое для DDGI) ─────
        // Читаем depth атлас в направлении от зонда к поверхности
        vec3  fromProbeDir = normalize(samplePos - probeWorldPos);
        vec2  depthUV      = ProbeAtlasUV(idx, fromProbeDir, uDepthTile, depthAtlasW, depthAtlasH);
        vec2  depthMoments = texture(depthAtlas, depthUV).rg;

        float visibility = ChebyshevVisibility(depthMoments, probeDist);
        // Куб visibility — агрессивно режет зонды за стенами
        w *= visibility * visibility * visibility;

        if (w < 0.0001) continue;

        // ── Читаем irradiance ─────────────────────────────
        vec2 irrUV = ProbeAtlasUV(idx, normal, uIrrTile, irrAtlasW, irrAtlasH);
        vec3 irr   = texture(irrAtlas, irrUV).rgb;

        irrSum += irr * w;
        wsSum  += w;
    }

    return vec4(irrSum, wsSum);
}

// ─────────────────────────────────────────────────────────────
// ГЛАВНАЯ ФУНКЦИЯ: мульти-уровневый сэмплинг
// ─────────────────────────────────────────────────────────────
vec3 SampleGIProbes(vec3 worldPos, vec3 normal) {
    vec3 fallback = GIFallback(normal);

    // ── L0 ──────────────────────────────────────────────────
    vec4  s0    = SampleProbeLevel(worldPos, normal,
                                   uGIIrrAtlas,    uGIDepthAtlas,
                                   uGIGridBaseX,    uGIGridBaseY,    uGIGridBaseZ,    uGIProbeSpacing);
    float fade0 = ComputeEdgeFade(worldPos,
                                  uGIGridBaseX,    uGIGridBaseY,    uGIGridBaseZ,    uGIProbeSpacing);
    vec3  irr0  = (s0.a > 0.001) ? s0.rgb / s0.a : fallback;

    // ── L1 ──────────────────────────────────────────────────
    vec4  s1    = SampleProbeLevel(worldPos, normal,
                                   uGIIrrAtlasL1,  uGIDepthAtlasL1,
                                   uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1);
    float fade1 = ComputeEdgeFade(worldPos,
                                  uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1);
    vec3  irr1  = (s1.a > 0.001) ? s1.rgb / s1.a : fallback;

    // ── L2 ──────────────────────────────────────────────────
    vec4  s2    = SampleProbeLevel(worldPos, normal,
                                   uGIIrrAtlasL2,  uGIDepthAtlasL2,
                                   uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2);
    float fade2 = ComputeEdgeFade(worldPos,
                                  uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2);
    vec3  irr2  = (s2.a > 0.001) ? s2.rgb / s2.a : fallback;

    // ── Слоёвое смешивание: L2 → L1 → L0 ────────────────────
    // Каждый более точный уровень "побеждает" там где он покрывает точку
    vec3 result = mix(fallback, irr2, fade2);
    result      = mix(result,   irr1, fade1);
    result      = mix(result,   irr0, fade0);

    return result;
}
