// --- Engine/Graphics/GLSL/include/gi.glsl ---

layout(std430, binding = 16) readonly buffer GIProbePositions {
    vec4 giProbePositions[]; // w = статус жизни (1.0 = жив, 0.0 = мертв)
};

layout(std430, binding = 17) readonly buffer GIProbeIrradiance {
    float giProbeIrradiance[];
};

uniform int   uGIGridBaseX;
uniform int   uGIGridBaseY;
uniform int   uGIGridBaseZ;
uniform float uGIProbeSpacing;
uniform int   uGIProbeX;
uniform int   uGIProbeY;
uniform int   uGIProbeZ;

int true_mod(int a, int b) { int m = a % b; return m < 0 ? m + b : m; }

vec3 EvalSHL1(int probeIdx, vec3 normal) {
    int base = probeIdx * 12;

    float y00 = 0.282095;
    float y10 = 0.488603 * normal.y;
    float y11 = 0.488603 * normal.z;
    float y12 = 0.488603 * normal.x;

    const float A0 = 3.14159265;
    const float A1 = 2.09439510;

    float r = giProbeIrradiance[base + 0] * y00 * A0
    + giProbeIrradiance[base + 1] * y10 * A1
    + giProbeIrradiance[base + 2] * y11 * A1
    + giProbeIrradiance[base + 3] * y12 * A1;

    float g = giProbeIrradiance[base + 4] * y00 * A0
    + giProbeIrradiance[base + 5] * y10 * A1
    + giProbeIrradiance[base + 6] * y11 * A1
    + giProbeIrradiance[base + 7] * y12 * A1;

    float b = giProbeIrradiance[base + 8] * y00 * A0
    + giProbeIrradiance[base + 9] * y10 * A1
    + giProbeIrradiance[base + 10]* y11 * A1
    + giProbeIrradiance[base + 11]* y12 * A1;

    return max(vec3(0.0), vec3(r, g, b) / 3.14159265);
}

// Запасной эмбиент неба работает ТОЛЬКО если мы смотрим на горизонт (за пределы сетки зондов)
vec3 GetGIAmbientFallback(vec3 normal) {
    float skyVis = clamp(normal.y * 0.5 + 0.5, 0.0, 1.0);
    float dayAmbient = 0.35;
    float nightAmbient = 0.04;
    return vec3(0.6, 0.7, 0.9) * mix(nightAmbient, dayAmbient, clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0)) * skyVis;
}

vec3 SampleGIProbes(vec3 worldPos, vec3 normal) {
    // === ВОКСЕЛЬНОЕ ВЫРАВНИВАНИЕ ===
    // Сдвигает точку ровно на 0.5 метра от стены.
    // При шаге зондов 1.0f это помещает нас точно на позицию зонда, устраняя любые артефакты геометрии!
    vec3 biasedPos = worldPos + normal * 0.5;

    vec3 gridBaseWorld = vec3(float(uGIGridBaseX), float(uGIGridBaseY), float(uGIGridBaseZ)) * uGIProbeSpacing + vec3(0.5);
    vec3 localPos = (biasedPos - gridBaseWorld) / uGIProbeSpacing;

    localPos = clamp(localPos, vec3(0.001), vec3(float(uGIProbeX) - 1.001, float(uGIProbeY) - 1.001, float(uGIProbeZ) - 1.001));

    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);
    vec3 fSmooth = f * f * (3.0 - 2.0 * f);

    vec3 irradiance = vec3(0.0);
    float weightSum  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++)
    {
        ivec3 p = p0 + ivec3(dx, dy, dz);
        ivec3 g = ivec3(uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ) + p;

        int modX = true_mod(g.x, uGIProbeX);
        int modY = true_mod(g.y, uGIProbeY);
        int modZ = true_mod(g.z, uGIProbeZ);
        int probeIdx = modX + uGIProbeX * (modY + uGIProbeY  * modZ);

        // Если зонд мертв (находится внутри камня), мы его полностью игнорируем!
        if (giProbePositions[probeIdx].w < 0.5) continue;

        vec3 wv = vec3(dx == 0 ? (1.0 - fSmooth.x) : fSmooth.x,
        dy == 0 ? (1.0 - fSmooth.y) : fSmooth.y,
        dz == 0 ? (1.0 - fSmooth.z) : fSmooth.z);
        float w = wv.x * wv.y * wv.z;

        irradiance += EvalSHL1(probeIdx, normal) * w;
        weightSum  += w;
    }

    vec3 gridCenterWorld = vec3(float(uGIGridBaseX) + float(uGIProbeX)*0.5,
    float(uGIGridBaseY) + float(uGIProbeY)*0.5,
    float(uGIGridBaseZ) + float(uGIProbeZ)*0.5) * uGIProbeSpacing + vec3(0.5);

    vec3 distToCenter = abs(worldPos - gridCenterWorld);
    vec3 gridHalfExtents = vec3(uGIProbeX, uGIProbeY, uGIProbeZ) * uGIProbeSpacing * 0.5;
    vec3 fadeDist = (gridHalfExtents - distToCenter) / (uGIProbeSpacing * 2.0);
    float edgeFade = clamp(min(min(fadeDist.x, fadeDist.y), fadeDist.z), 0.0, 1.0);

    // Если все 8 зондов мертвы (мы в пещере или внутри горы), будет АБСОЛЮТНАЯ ТЬМА
    vec3 finalGI = (weightSum > 0.0001) ? (irradiance / weightSum) : vec3(0.0);

    vec3 skyAmb = GetGIAmbientFallback(normal);
    return mix(skyAmb, finalGI, edgeFade);
}

// ... EvaluatePointLights без изменений
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