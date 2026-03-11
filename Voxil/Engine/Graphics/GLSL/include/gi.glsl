// --- START OF FILE gi.glsl.txt ---
// DDGI: октаэдрические атласы irradiance + depth, Chebyshev visibility test

uniform sampler2D uGIIrrAtlas;
uniform sampler2D uGIDepthAtlas;
uniform sampler2D uGIIrrAtlasL1;
uniform sampler2D uGIDepthAtlasL1;
uniform sampler2D uGIIrrAtlasL2;
uniform sampler2D uGIDepthAtlasL2;

uniform int   uGIGridBaseX,    uGIGridBaseY,    uGIGridBaseZ;
uniform float uGIProbeSpacing;
uniform int   uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1;
uniform float uGIProbeSpacingL1;
uniform int   uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2;
uniform float uGIProbeSpacingL2;
uniform int uGIProbeX, uGIProbeY, uGIProbeZ;
uniform int uIrrTile;
uniform int uDepthTile;

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
    int col = wx;
    int row = wy + wz * uGIProbeY;
    vec2 tileOrigin = vec2(float(col * pad) + 1.0, float(row * pad) + 1.0);
    vec2 octUV   = OctEncode(normalize(dir));
    vec2 pixelXY = tileOrigin + octUV * float(tileSize);
    return pixelXY / vec2(float(atlasW), float(atlasH));
}

float ChebyshevVisibility(vec2 depthMoments, float dist) {
    if (dist <= depthMoments.x) return 1.0;
    float variance = depthMoments.y - (depthMoments.x * depthMoments.x);
    variance = max(variance, 0.0001);
    float d    = dist - depthMoments.x;
    float cheb = variance / (variance + d * d);
    // Убираем hard cut (cheb-0.1)/0.9 — он добивает слабые угловые зонды
    // pow2 вместо pow4 — менее агрессивное затухание для бинарных воксельных границ
    float visibility = clamp(cheb, 0.0, 1.0);
    return visibility * visibility;
}

vec3 GIFallback(vec3 normal) {
    float skyVis = max(normal.y * 0.5 + 0.5, 0.15);
    float t = clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0);
    return vec3(0.6, 0.7, 0.9) * max(mix(0.06, 0.30, t), 0.05) * skyVis;
}

float ComputeEdgeFade(vec3 worldPos, int bX, int bY, int bZ, float sp) {
    vec3 gridCenter = (vec3(float(bX), float(bY), float(bZ)) + 
                       vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * 0.5) * sp;
    vec3 halfExt    = vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * sp * 0.5;
    vec3 distEdge   = halfExt - abs(worldPos - gridCenter);
    // Было sp * 2.0 — увеличь зону перехода:
    return clamp(min(min(distEdge.x, distEdge.y), distEdge.z) / (sp * 2.0), 0.0, 1.0);
}

vec4 SampleProbeLevel(vec3 worldPos, vec3 normal, vec3 viewDir, sampler2D irrAtlas, sampler2D depthAtlas, int bX, int bY, int bZ, float sp) {
    vec3 biasDir = normalize(normal * 0.2 + viewDir * 0.8);
    vec3 samplePos = worldPos + biasDir * (0.75 * sp * 0.3);
    vec3 gridOrigin = vec3(float(bX), float(bY), float(bZ)) * sp + sp * 0.5;
    vec3 localPos   = clamp((samplePos - gridOrigin) / sp, vec3(0.0), vec3(float(uGIProbeX)-1.0, float(uGIProbeY)-1.0, float(uGIProbeZ)-1.0));

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);
    vec3  fs = f * f * (3.0 - 2.0 * f);

    int irrAtlasW   = uGIProbeX * (uIrrTile   + 2);
    int irrAtlasH   = uGIProbeY * uGIProbeZ * (uIrrTile   + 2);
    int depthAtlasW = uGIProbeX * (uDepthTile + 2);
    int depthAtlasH = uGIProbeY * uGIProbeZ * (uDepthTile + 2);

    vec3  irrSum = vec3(0.0);
    float wsSum  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++) {
        ivec3 p = clamp(p0 + ivec3(dx, dy, dz), ivec3(0), ivec3(uGIProbeX-1, uGIProbeY-1, uGIProbeZ-1));
        ivec3 g   = ivec3(bX, bY, bZ) + p;
        int   idx = gi_mod(g.x, uGIProbeX) + uGIProbeX * (gi_mod(g.y, uGIProbeY) + uGIProbeY *  gi_mod(g.z, uGIProbeZ));

        vec3 probeWorldPos = vec3(float(g.x), float(g.y), float(g.z)) * sp + sp * 0.5;
        vec3  toProbe   = probeWorldPos - samplePos;
        float probeDist = length(toProbe) + 0.0001;
        float backfaceW = max(dot(normal, toProbe / probeDist), 0.0);
        float w  = ((dx == 0) ? (1.0 - fs.x) : fs.x) * ((dy == 0) ? (1.0 - fs.y) : fs.y) * ((dz == 0) ? (1.0 - fs.z) : fs.z);

        if (backfaceW <= 0.0) continue; // пропускаем зонды строго за спиной
        w *= backfaceW;

        if (w < 0.0001) continue;

        vec3  fromProbeDir = normalize(samplePos - probeWorldPos);
        vec2  depthUV      = ProbeAtlasUV(idx, fromProbeDir, uDepthTile, depthAtlasW, depthAtlasH);
        vec2  depthMoments = texture(depthAtlas, depthUV).rg;

        float visibility = ChebyshevVisibility(depthMoments, probeDist);
        w *= visibility;

        if (w < 0.0001) continue;

        vec2 irrUV = ProbeAtlasUV(idx, normal, uIrrTile, irrAtlasW, irrAtlasH);
        irrSum += texture(irrAtlas, irrUV).rgb * w;
        wsSum  += w;
    }
    return vec4(irrSum, wsSum);
}

vec3 SampleGIProbes(vec3 worldPos, vec3 normal, vec3 viewDir) {
    vec3 fallback = GIFallback(normal);

    // ИСПРАВЛЕНИЕ 4: Если wsSum < 0.001 (т.е. sX.a < 0.001) — это означает, что ВСЕ 8 соседних зондов 
    // полностью заблокированы геометрией (мы глубоко под землей или внутри толстой стены).
    // Вместо использования яркого цвета неба (fallback), мы возвращаем почти полную темноту vec3(0.001).

    vec4  s2    = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlasL2, uGIDepthAtlasL2, uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2);
    float fade2 = ComputeEdgeFade(worldPos, uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2);
    vec3 irr2 = (s2.a > 0.08) ? s2.rgb / s2.a : fallback;

    vec4  s1    = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlasL1, uGIDepthAtlasL1, uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1);
    float fade1 = ComputeEdgeFade(worldPos, uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1);
    vec3 irr1 = (s1.a > 0.08) ? s1.rgb / s1.a : fallback * 0.3;

    vec4  s0    = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlas, uGIDepthAtlas, uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ, uGIProbeSpacing);
    float fade0 = ComputeEdgeFade(worldPos, uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ, uGIProbeSpacing);
    vec3 irr0 = (s0.a > 0.08) ? s0.rgb / s0.a : fallback * 0.15;

    // Каскады смешиваются от крупного к мелкому по краям сетки
    vec3 result = mix(fallback, irr2, fade2);
    result      = mix(result,   irr1, fade1);
    result      = mix(result,   irr0, fade0);

    return result;
}