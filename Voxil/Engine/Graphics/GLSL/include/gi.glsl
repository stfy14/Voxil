// --- START OF FILE gi.glsl.txt ---
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
    vec2 octUV   = OctEncode(normalize(dir));
    return (tileOrigin + octUV * float(tileSize)) / vec2(float(atlasW), float(atlasH));
}

float ChebyshevVisibility(vec2 depthMoments, float dist, float spacing) {
    float depthBias = clamp(spacing * 0.15, 0.1, 0.5); 
    if (dist <= depthMoments.x + depthBias) return 1.0;
    
    float variance = depthMoments.y - (depthMoments.x * depthMoments.x);
    // Увеличиваем минимальную дисперсию (это сгладит резкие переходы света на стене)
    variance = max(variance, 0.02 * spacing);
    
    float d    = dist - depthMoments.x;
    float cheb = variance / (variance + d * d);
    
    float lightLeakReduction = 0.35; // было 0.1
    cheb = max(0.0, cheb - lightLeakReduction) / (1.0 - lightLeakReduction);
    
    return clamp(cheb, 0.0, 1.0);
}

vec3 GIFallback(vec3 normal) {
    float skyVis = max(normal.y * 0.5 + 0.5, 0.15);
    vec3 day = vec3(0.60, 0.75, 0.95);
    vec3 sunset = vec3(1.0, 0.4, 0.2);
    vec3 night = vec3(0.02, 0.02, 0.05);

    float sunHeight = uSunDir.y;
    float dayFactor = clamp(sunHeight * 4.0 + 0.2, 0.0, 1.0); 
    float sunsetFactor = clamp(1.0 - abs(sunHeight) * 5.0, 0.0, 1.0); 

    vec3 col = mix(mix(night, day, dayFactor), sunset, sunsetFactor);
    return col * max(mix(0.05, 0.35, dayFactor), 0.05) * skyVis;
}

float ComputeEdgeFade(vec3 worldPos, int bX, int bY, int bZ, float sp) {
    vec3 gridCenter = (vec3(float(bX), float(bY), float(bZ)) + 
                       vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * 0.5) * sp + vec3(0.5 / VOXELS_PER_METER);
    vec3 halfExt    = vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * sp * 0.5;
    vec3 distEdge   = halfExt - abs(worldPos - gridCenter);
    return clamp(min(min(distEdge.x, distEdge.y), distEdge.z) / (sp * 2.0), 0.0, 1.0);
}

vec4 SampleProbeLevel(vec3 worldPos, vec3 normal, vec3 viewDir,
    sampler2D irrAtlas, sampler2D depthAtlas, int bX, int bY, int bZ, float sp, int level) {
    // Увеличиваем отступ от геометрии, чтобы лучи не врезались сами в себя
    float normalBias = clamp(sp * 0.3, 0.1, 0.8);
    // Делаем упор на нормаль поверхности
    vec3 biasDir = normalize(normal * 0.9 + viewDir * 0.1);
    vec3 samplePos = worldPos + biasDir * normalBias;

    vec3 gridOrigin = vec3(float(bX), float(bY), float(bZ)) * sp + sp * 0.5 + vec3(0.5 / VOXELS_PER_METER);
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
    vec3  backupIrr = vec3(0.0);
    float backupWs  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++) {
        float trilinear = ((dx == 0) ? (1.0 - fs.x) : fs.x) * 
                          ((dy == 0) ? (1.0 - fs.y) : fs.y) * 
                          ((dz == 0) ? (1.0 - fs.z) : fs.z);
                          
        if (trilinear < 0.001) continue;

        ivec3 p = clamp(p0 + ivec3(dx, dy, dz), ivec3(0), ivec3(uGIProbeX-1, uGIProbeY-1, uGIProbeZ-1));
        ivec3 g   = ivec3(bX, bY, bZ) + p;
        int   idx = gi_mod(g.x, uGIProbeX) + uGIProbeX * (gi_mod(g.y, uGIProbeY) + uGIProbeY * gi_mod(g.z, uGIProbeZ));

        vec3 probeWorldPos = vec3(float(g.x), float(g.y), float(g.z)) * sp + sp * 0.5 + vec3(0.5 / VOXELS_PER_METER);
                // Читаем актуальную позицию из SSBO — compute пишет её сразу при обработке
        vec3 storedPos;
        float storedState;
        if      (level == 0) storedState = giProbesL0[idx].pos.w;
        else if (level == 1) storedState = giProbesL1[idx].pos.w;
        else                 storedState = giProbesL2[idx].pos.w;

        if (storedState < 0.5) continue;
        vec3  toProbe    = probeWorldPos - samplePos;
        float probeDist  = length(toProbe) + 0.0001;
        vec3  dirToProbe = toProbe / probeDist;

        float backfaceW = (dot(normal, dirToProbe) + 0.2) / 1.2;
        backfaceW = clamp(backfaceW, 0.0, 1.0);
        float w = trilinear * (backfaceW * backfaceW);
        if (w < 0.001) continue;

        vec3  fromProbeDir = -dirToProbe;
        vec2  depthMoments = texture(depthAtlas, ProbeAtlasUV(idx, fromProbeDir, uDepthTile, depthAtlasW, depthAtlasH)).rg;

        // КЛЮЧЕВОЙ ФИС: зонд валиден только если его depth-данные
        // соответствуют реальному расстоянию до него.
        // Если зонд переехал (grid shift), depth будет несоответствующим — пропускаем.
        // Порог: зонд принимаем только если depthMoments.x > 0 И не слишком далёк от ожидаемого.
        bool isValidProbe = (depthMoments.x > 0.001);

        // backupIrr — только если зонд "видит" точку: его средняя глубина >= 60% от расстояния до точки
        // Поверхностный зонд с depthMoments.x=2м не войдёт в backupIrr для подземной точки в 5м
        bool isBackupValid = isValidProbe && (depthMoments.x >= probeDist * 0.6);

        if (isBackupValid) {
            vec2 irrUV = ProbeAtlasUV(idx, normal, uIrrTile, irrAtlasW, irrAtlasH);
            backupIrr += texture(irrAtlas, irrUV).rgb * trilinear;
            backupWs  += trilinear;
        }

        float visibility = ChebyshevVisibility(depthMoments, probeDist, sp);
        w *= (visibility * visibility);
        if (w < 0.001) continue;

        vec2 irrUV = ProbeAtlasUV(idx, normal, uIrrTile, irrAtlasW, irrAtlasH);
        irrSum += texture(irrAtlas, irrUV).rgb * w;
        wsSum  += w;
    }
    
    if (wsSum < 0.001) return vec4(backupIrr, backupWs);
    return vec4(irrSum, wsSum);
}

vec3 SampleGIProbes(vec3 worldPos, vec3 normal, vec3 viewDir) {
    vec3 fallback = GIFallback(normal);

    // Считаем L0
    float fade0 = ComputeEdgeFade(worldPos, uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ, uGIProbeSpacing);
vec4 s0 = SampleProbeLevel(worldPos, normal, viewDir,
    uGIIrrAtlas, uGIDepthAtlas,
    uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ, uGIProbeSpacing, 0);
    
    // ОГРОМНАЯ ОПТИМИЗАЦИЯ ДЛЯ ФПС: 
    // Если мы идеально внутри L0 (в радиусе 16м от камеры) и есть качественный свет - скипаем расчеты дальних слоев!
    if (fade0 >= 0.999 && s0.a > 0.01) return s0.rgb / s0.a;

    // Считаем L1
    float fade1 = ComputeEdgeFade(worldPos, uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1);
    vec4 s1 = SampleProbeLevel(worldPos, normal, viewDir,
    uGIIrrAtlasL1, uGIDepthAtlasL1,
    uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1, 1);
    
    // ОПТИМИЗАЦИЯ: Если мы внутри L1, скипаем L2!
    if (fade1 >= 0.999 && fade0 <= 0.001 && s1.a > 0.01) return s1.rgb / s1.a;

    // Считаем L2
    float fade2 = ComputeEdgeFade(worldPos, uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2);
    vec4 s2 = SampleProbeLevel(worldPos, normal, viewDir,
    uGIIrrAtlasL2, uGIDepthAtlasL2,
    uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2, 2);
    
    vec3 irr2 = (s2.a > 0.001) ? (s2.rgb / s2.a) : vec3(0.0);
    vec3 irr1 = (s1.a > 0.001) ? (s1.rgb / s1.a) : irr2;
    vec3 irr0 = (s0.a > 0.001) ? (s0.rgb / s0.a) : irr1;

    vec3 result = mix(fallback, irr2, fade2);
    result      = mix(result,   irr1, fade1);
    result      = mix(result,   irr0, fade0);

    return result;
}