// --- Engine/Graphics/GLSL/include/gi.glsl ---

struct ProbeData {
    vec4 pos;
    vec4 colorAndState;
};

layout(std430, binding = 16) readonly buffer GIProbePositions {
    ProbeData probes[];
};

uniform sampler2D uGIIrradianceAtlas;
uniform sampler2D uGIDepthAtlas;

uniform int   uGIGridBaseX;
uniform int   uGIGridBaseY;
uniform int   uGIGridBaseZ;
uniform float uGIProbeSpacing;
uniform int   uGIProbeX;
uniform int   uGIProbeY;
uniform int   uGIProbeZ;
uniform int   uIrradianceSize;
uniform int   uDepthSize;

int true_mod(int a, int b) { int m = a % b; return m < 0 ? m + b : m; }
vec2 signNotZero(vec2 v) { return vec2((v.x>=0.0)?1.0:-1.0, (v.y>=0.0)?1.0:-1.0); }
vec2 octEncode(vec3 v) {
    float l = abs(v.x) + abs(v.y) + abs(v.z);
    vec2 res = v.xy * (1.0 / l);
    res = (v.z >= 0.0) ? res : (1.0 - abs(res.yx)) * signNotZero(res);
    return res;
}

vec3 GetGIAmbientFallback(vec3 worldPos) {
    float heightFactor = clamp((worldPos.y - 20.0) / 10.0, 0.0, 1.0);
    float dayAmbient = 0.35;
    float nightAmbient = 0.04;
    return vec3(0.6, 0.7, 0.9) * mix(nightAmbient, dayAmbient, clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0)) * heightFactor;
}

vec3 SampleGIProbes(vec3 worldPos, vec3 normal) {
    // === ГЛАВНЫЙ ВОКСЕЛЬНЫЙ ТРЮК ===
    // Мы смещаем точку выборки ровно на 0.5 метра по нормали.
    // Это помещает нас в ИДЕАЛЬНЫЙ ЦЕНТР пустого воздушного блока перед стеной!
    // Благодаря этому, мы полностью обходим артефакты математического самозатенения.
    vec3 viewDir = normalize(uCamPos - worldPos);
    vec3 biasedPos = worldPos + normal * 0.5 + viewDir * 0.05;

    vec3 gridBaseWorld = vec3(float(uGIGridBaseX), float(uGIGridBaseY), float(uGIGridBaseZ)) * uGIProbeSpacing + vec3(0.5);
    vec3 localPos = (biasedPos - gridBaseWorld) / uGIProbeSpacing;

    localPos = clamp(localPos, vec3(1.001), vec3(float(uGIProbeX) - 2.001, float(uGIProbeY) - 2.001, float(uGIProbeZ) - 2.001));

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);
    vec3 fSmooth = f * f * (3.0 - 2.0 * f);

    vec3 irradiance = vec3(0.0);
    float weightSum  = 0.0;

    vec2 texSizeIrr = vec2(textureSize(uGIIrradianceAtlas, 0));
    vec2 texSizeDep = vec2(textureSize(uGIDepthAtlas, 0));

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++)
    {
        ivec3 p = p0 + ivec3(dx, dy, dz);
        ivec3 g = ivec3(uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ) + p;

        int modX = true_mod(g.x, uGIProbeX);
        int modY = true_mod(g.y, uGIProbeY);
        int modZ = true_mod(g.z, uGIProbeZ);

        int probeIdx = modX + uGIProbeX * (modY + uGIProbeY * modZ);
        int atlasX = probeIdx % uGIProbeX;
        int atlasY = (probeIdx / uGIProbeX) % uGIProbeY;
        int atlasZ = probeIdx / (uGIProbeX * uGIProbeY);
        int atlasGridY = atlasY + atlasZ * uGIProbeY;

        vec3 expectedWorldPos = vec3(g) * uGIProbeSpacing + vec3(0.5);
        vec3 probePos = probes[probeIdx].pos.xyz;

        vec3 wv = vec3(dx == 0 ? (1.0 - fSmooth.x) : fSmooth.x,
        dy == 0 ? (1.0 - fSmooth.y) : fSmooth.y,
        dz == 0 ? (1.0 - fSmooth.z) : fSmooth.z);
        float baseWeight = wv.x * wv.y * wv.z;

        float distErr = distance(expectedWorldPos, probePos);
        if (distErr > 0.1) {
            baseWeight *= clamp(1.0 - (distErr / uGIProbeSpacing), 0.0, 1.0);
        }

        vec3 trueDirToProbe = probePos - biasedPos;
        float distToProbe = length(trueDirToProbe);
        vec3 dir = trueDirToProbe / distToProbe;

        vec2 depTexUV = (vec2(atlasX, atlasGridY) * float(uDepthSize) + 1.0 + octEncode(dir) * float(uDepthSize - 2)) / texSizeDep;
        vec2 probeDep = texture(uGIDepthAtlas, depTexUV).rg;

        float mean = probeDep.x;

        // Плавное отключение зондов, застрявших в камне (без резких скачков)
        float wallWeight = smoothstep(0.05, 0.25, mean);

        // Мягкое огибание нормали (Wrap Lighting)
        float weightDir = max(0.0, dot(normal, dir));
        weightDir = (weightDir + 0.1) / 1.1;

        // Идеальный Чебышев для вокселей
        float variance = abs(probeDep.y - (mean * mean));
        variance = max(variance, 0.05);

        float chebyshev = 1.0;
        float diff = distToProbe - mean;

        if (diff > 0.05) {
            chebyshev = variance / (variance + diff * diff);
            chebyshev = max(pow(chebyshev, 2.0), 0.0); // Возводим в квадрат для устранения утечек света
        }

        // Защита +1e-5 гарантирует, что мы больше никогда не поделим на ноль!
        float finalWeight = baseWeight * weightDir * chebyshev * wallWeight + 1e-5;

        vec2 irrUV = octEncode(normal) * 0.5 + 0.5;
        vec2 irrTexUV = (vec2(atlasX, atlasGridY) * float(uIrradianceSize) + 1.0 + irrUV * float(uIrradianceSize - 2)) / texSizeIrr;
        vec3 probeIrr = texture(uGIIrradianceAtlas, irrTexUV).rgb;

        irradiance += probeIrr * finalWeight;
        weightSum  += finalWeight;
    }

    vec3 skyAmb = GetGIAmbientFallback(worldPos);

    if (weightSum > 1e-4) {
        vec3 finalGI = irradiance / weightSum;

        vec3 gridCenterWorld = vec3(float(uGIGridBaseX) + float(uGIProbeX)*0.5,
        float(uGIGridBaseY) + float(uGIProbeY)*0.5,
        float(uGIGridBaseZ) + float(uGIProbeZ)*0.5) * uGIProbeSpacing + vec3(0.5);

        vec3 distToCenter = abs(worldPos - gridCenterWorld);
        vec3 gridHalfExtents = vec3(uGIProbeX, uGIProbeY, uGIProbeZ) * uGIProbeSpacing * 0.5;
        vec3 fadeDist = (gridHalfExtents - distToCenter) / (uGIProbeSpacing * 2.0);
        float edgeFade = clamp(min(min(fadeDist.x, fadeDist.y), fadeDist.z), 0.0, 1.0);

        return mix(skyAmb, finalGI, edgeFade);
    }

    return skyAmb;
}

// ... EvaluatePointLights без изменений (оставил для копипаста целиком)
#define MAX_POINT_LIGHTS 32
struct PointLightData { vec4 posRadius; vec4 colorIntensity; };
layout(std430, binding = 18) readonly buffer PointLightBuffer { PointLightData pointLights[]; };
uniform int uPointLightCount;

vec3 EvaluatePointLights(vec3 hitPos, vec3 normal) {
    vec3 totalLight = vec3(0.0);
    for (int i = 0; i < uPointLightCount && i < MAX_POINT_LIGHTS; i++) {
        vec3  lPos       = pointLights[i].posRadius.xyz;
        float lRadius    = pointLights[i].posRadius.w;
        vec3  lColor     = pointLights[i].colorIntensity.rgb;
        float lIntensity = pointLights[i].colorIntensity.a;
        vec3  toLight  = lPos - hitPos;
        float dist     = length(toLight);
        if (dist > lRadius) continue;
        vec3  lightDir  = toLight / dist;
        float nDotL     = max(0.0, dot(normal, lightDir));
        float falloff  = (dist * dist);
        float window   = max(0.0, 1.0 - (dist / lRadius));
        window         = window * window * window * window;
        float attenuation = (lIntensity / max(falloff, 0.01)) * window;
        totalLight += lColor * nDotL * attenuation;
    }
    return totalLight;
}