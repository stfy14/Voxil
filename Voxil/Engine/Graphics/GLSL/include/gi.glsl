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

float ChebyshevVisibility(vec2 depthMoments, float dist) {
    if (dist <= depthMoments.x) return 1.0;
    
    float variance = depthMoments.y - (depthMoments.x * depthMoments.x);
    // БЫЛО: variance = max(variance, 0.05);
    // СТАЛО: Для вокселей нужно чуть больше допуска, так как лучи зонда бьют в плоскую стену
    variance = max(variance, 0.15); 
    
    float d = dist - depthMoments.x;
    float cheb = variance / (variance + d * d);
    
    // БЫЛО: cheb = max(0.0, cheb - 0.2) / 0.8;
    // СТАЛО: Агрессивнее давим утечки света (Light Leaks) через 1-блочные стены
    cheb = max(0.0, cheb - 0.3) / 0.7; 
    
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
                       vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * 0.5) * sp;
    vec3 halfExt    = vec3(float(uGIProbeX), float(uGIProbeY), float(uGIProbeZ)) * sp * 0.5;
    vec3 distEdge   = halfExt - abs(worldPos - gridCenter);
    return clamp(min(min(distEdge.x, distEdge.y), distEdge.z) / (sp * 2.0), 0.0, 1.0);
}

vec4 SampleProbeLevel(vec3 worldPos, vec3 normal, vec3 viewDir,
    sampler2D irrAtlas, sampler2D depthAtlas,
    int bX, int bY, int bZ, float sp, int level)
{
    vec3 samplePos = worldPos + normal * 0.55;

    vec3 gridOrigin = vec3(float(bX), float(bY), float(bZ)) * sp + sp * 0.5;
    vec3 localPos   = (samplePos - gridOrigin) / sp;

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);

    int irrAtlasW   = uGIProbeX * (uIrrTile   + 2);
    int irrAtlasH   = uGIProbeY * uGIProbeZ * (uIrrTile   + 2);
    int depthAtlasW = uGIProbeX * (uDepthTile + 2);
    int depthAtlasH = uGIProbeY * uGIProbeZ * (uDepthTile + 2);

    vec3  irrSum = vec3(0.0); 
    float wsSum  = 0.0;

    // Цикл от 0 до 7 - это то же самое, что тройной цикл, но работает быстрее на GPU
    for (int i = 0; i < 8; i++)
    {
        int dx = i & 1;
        int dy = (i >> 1) & 1;
        int dz = (i >> 2) & 1;

        ivec3 p = p0 + ivec3(dx, dy, dz);

        // Пропускаем зонды вне сетки
        if (any(lessThan(p, ivec3(0))) || any(greaterThanEqual(p, ivec3(uGIProbeX, uGIProbeY, uGIProbeZ)))) continue;

        ivec3 g   = ivec3(bX, bY, bZ) + p;
        int   idx = gi_mod(g.x, uGIProbeX)
                  + uGIProbeX * (gi_mod(g.y, uGIProbeY)
                  + uGIProbeY *  gi_mod(g.z, uGIProbeZ));

        float storedState;
        vec3 probeWorldPos; 
        
        // Читаем позицию и статус напрямую из буфера
        if (level == 0) {
            storedState = giProbesL0[idx].pos.w;
            probeWorldPos = giProbesL0[idx].pos.xyz;
        } else if (level == 1) {
            storedState = giProbesL1[idx].pos.w;
            probeWorldPos = giProbesL1[idx].pos.xyz;
        } else {
            storedState = giProbesL2[idx].pos.w;
            probeWorldPos = giProbesL2[idx].pos.xyz;
        }
        
        if (storedState < 0.5) continue; // Зонд внутри стены (выключаем его вес)

        // === ДОБАВИТЬ ЭТОТ КОД ===
        // Проверяем, не является ли этот зонд "призраком" (еще не обновлен)
        // Вычисляем, где этот зонд ДОЛЖЕН находиться математически:
        vec3 expectedPos = gridOrigin + vec3(p) * sp;
        
        // Если реальная позиция в памяти отличается от ожидаемой больше чем на 10 см,
        // значит тороидальная сетка сдвинулась, а этот зонд стоит в очереди на апдейт.
        // Игнорируем его мусорные данные!
        if (distance(probeWorldPos, expectedPos) > 0.1) continue; 
        // ==========================

        // 1. Базовый вес (Трилинейная интерполяция)
        vec3 trilinear = mix(1.0 - f, f, vec3(dx, dy, dz));
        float weight = trilinear.x * trilinear.y * trilinear.z;

        if (weight < 0.001) continue;

        vec3  toProbe    = probeWorldPos - samplePos;
        float probeDist  = length(toProbe);
        if (probeDist > sp * 1.5) continue;
        vec3  dirToProbe = toProbe / max(probeDist, 0.0001);

        // 2. Отсечение задних граней (Backface weighting) - свет не проходит сквозь куб
        float backfaceW = (dot(normal, dirToProbe) + 1.0) * 0.5;
        weight *= backfaceW;
        
        if (weight < 0.001) continue;

        vec2 depthMoments = texture(depthAtlas, ProbeAtlasUV(idx, -dirToProbe, uDepthTile, depthAtlasW, depthAtlasH)).rg;

        if (depthMoments.x < 0.001) continue; // Зонд свежий и еще не запекся (выключаем его вес)

        // 3. Чебышёв (Видимость)
        float visibility = ChebyshevVisibility(depthMoments, probeDist);
        weight *= visibility; 
        
        if (weight < 0.001) continue;

        // Если зонд прошел все проверки - берем его свет
        vec2 irrUV = ProbeAtlasUV(idx, normal, uIrrTile, irrAtlasW, irrAtlasH);
        irrSum += texture(irrAtlas, irrUV).rgb * weight;
        wsSum  += weight;
    }

    // Никаких if'ов. Если wsSum == 0, функция вернет vec4(0.0), и включится следующий каскад.
    return vec4(irrSum, wsSum);
}

vec3 SampleGIProbes(vec3 worldPos, vec3 normal, vec3 viewDir) {
    vec3 fallback = GIFallback(normal);

    float fade0 = ComputeEdgeFade(worldPos, uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ, uGIProbeSpacing);
    vec4 s0 = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlas, uGIDepthAtlas,
    uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ, uGIProbeSpacing, 0);
    
    if (fade0 >= 0.999 && s0.a > 0.01) return s0.rgb / s0.a;

    float fade1 = ComputeEdgeFade(worldPos, uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1);
    vec4 s1 = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlasL1, uGIDepthAtlasL1,
    uGIGridBaseX_L1, uGIGridBaseY_L1, uGIGridBaseZ_L1, uGIProbeSpacingL1, 1);
    
    if (fade1 >= 0.999 && fade0 <= 0.001 && s1.a > 0.01) return s1.rgb / s1.a;

    float fade2 = ComputeEdgeFade(worldPos, uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2);
    vec4 s2 = SampleProbeLevel(worldPos, normal, viewDir, uGIIrrAtlasL2, uGIDepthAtlasL2,
    uGIGridBaseX_L2, uGIGridBaseY_L2, uGIGridBaseZ_L2, uGIProbeSpacingL2, 2);
    
    vec3 irr2 = (s2.a > 0.01) ? (s2.rgb / s2.a) : fallback;
    vec3 irr1 = (s1.a > 0.01) ? (s1.rgb / s1.a) : irr2;
    vec3 irr0 = (s0.a > 0.01) ? (s0.rgb / s0.a) : irr1;

    vec3 result = mix(fallback, irr2, fade2);
    result      = mix(result,   irr1, fade1);
    result      = mix(result,   irr0, fade0);

    return result;
}