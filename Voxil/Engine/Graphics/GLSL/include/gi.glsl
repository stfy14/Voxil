// --- Engine/Graphics/GLSL/include/gi.glsl ---
// Sampling irradiance probes (SH L1) for indirect diffuse lighting.
// Include AFTER common.glsl and BEFORE composite.frag's main().

// ============================================================
// PROBE BUFFERS (привязаны через GIProbeSystem.Bind())
// ============================================================
layout(std430, binding = 16) readonly buffer GIProbePositions {
    vec4 giProbePositions[];
};

layout(std430, binding = 17) readonly buffer GIProbeIrradiance {
    float giProbeIrradiance[];
};

// ============================================================
// UNIFORMS (устанавливаются GIProbeSystem.SetSamplingUniforms)
// ============================================================
uniform int   uGIGridBaseX;
uniform int   uGIGridBaseY;
uniform int   uGIGridBaseZ;
uniform float uGIProbeSpacing;
uniform int   uGIProbeX;
uniform int   uGIProbeY;
uniform int   uGIProbeZ;
// uSunDir УДАЛЕН ОТСЮДА, ТАК КАК ОН УЖЕ ЕСТЬ В common.glsl!

// ============================================================
// ВСПОМОГАТЕЛЬНАЯ ФУНКЦИЯ: Математически верный модуль для отрицательных чисел
// ============================================================
int true_mod(int a, int b) {
    int m = a % b;
    return m < 0 ? m + b : m;
}

// ============================================================
// SH L1 EVALUATION
// Восстанавливает irradiance в направлении normal из SH коэффициентов.
// Возвращает vec3 (RGB irradiance).
// ============================================================
vec3 EvalSHL1(int probeIdx, vec3 normal) {
    int base = probeIdx * 12;

    // Базисные полиномы: Y00, Y10=ny, Y11=nz, Y12=nx
    float y00 = 0.282095;
    float y10 = 0.488603 * normal.y;
    float y11 = 0.488603 * normal.z;
    float y12 = 0.488603 * normal.x;

    // Множители косинусной свертки для диффузного света (A0, A1)
    const float A0 = 3.14159265;      // PI
    const float A1 = 2.09439510;      // 2 * PI / 3

    // Восстанавливаем каналы с применением свертки
    float r = giProbeIrradiance[base + 0] * y00 * A0
    + giProbeIrradiance[base + 1] * y10 * A1
    + giProbeIrradiance[base + 2] * y11 * A1
    + giProbeIrradiance[base + 3] * y12 * A1;

    float g = giProbeIrradiance[base + 4] * y00 * A0
    + giProbeIrradiance[base + 5] * y10 * A1
    + giProbeIrradiance[base + 6] * y11 * A1
    + giProbeIrradiance[base + 7] * y12 * A1;

    float b = giProbeIrradiance[base + 8]  * y00 * A0
    + giProbeIrradiance[base + 9]  * y10 * A1
    + giProbeIrradiance[base + 10] * y11 * A1
    + giProbeIrradiance[base + 11] * y12 * A1;

    // Делим на PI для стандартного PBR диффуза (Albedo / PI * Irradiance)
    return max(vec3(0.0), vec3(r, g, b) / 3.14159265);
}

// ============================================================
// ТРИЛИНЕЙНАЯ ИНТЕРПОЛЯЦИЯ ЗОНДОВ (С ПОДДЕРЖКОЙ КОЛЬЦЕВОГО БУФЕРА)
// Находит 8 ближайших зондов и интерполирует irradiance.
// ============================================================
vec3 SampleGIProbes(vec3 worldPos, vec3 normal) {
    // Находим мировой старт сетки с учетом базового индекса
    vec3 gridBaseWorld = vec3(float(uGIGridBaseX), float(uGIGridBaseY), float(uGIGridBaseZ)) * uGIProbeSpacing;
    vec3 localPos = (worldPos - gridBaseWorld) / uGIProbeSpacing;

    // ИСПРАВЛЕНИЕ МЕРЦАНИЯ: Ограничиваем локальные координаты внутренним "безопасным" слоем зондов!
    localPos = clamp(localPos,
                     vec3(1.001),
                     vec3(float(uGIProbeX) - 2.001, float(uGIProbeY) - 2.001, float(uGIProbeZ) - 2.001)
    );

    // Нижний угол куба интерполяции
    ivec3 p0 = ivec3(floor(localPos));
    vec3  f  = fract(localPos);

    // Сглаженная интерполяция (smoothstep)
    vec3 fSmooth = f * f * (3.0 - 2.0 * f);

    vec3 irradiance = vec3(0.0);
    float weightSum  = 0.0;

    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++)
    {
        // Локальные координаты куба
        ivec3 p = p0 + ivec3(dx, dy, dz);

        // Абсолютные координаты зонда в мире (в индексах сетки)
        ivec3 g = ivec3(uGIGridBaseX, uGIGridBaseY, uGIGridBaseZ) + p;

        // Ищем индекс зонда в кольцевом буфере (Toroidal Addressing)
        int modX = true_mod(g.x, uGIProbeX);
        int modY = true_mod(g.y, uGIProbeY);
        int modZ = true_mod(g.z, uGIProbeZ);

        int probeIdx = modX + uGIProbeX * (modY + uGIProbeY * modZ);

        // Проверяем, успел ли зонд обновиться
        vec3 expectedWorldPos = vec3(g) * uGIProbeSpacing;
        vec3 actualWorldPos = giProbePositions[probeIdx].xyz;

        // Вес трилинейной интерполяции
        vec3 wv = vec3(dx == 0 ? (1.0 - fSmooth.x) : fSmooth.x,
        dy == 0 ? (1.0 - fSmooth.y) : fSmooth.y,
        dz == 0 ? (1.0 - fSmooth.z) : fSmooth.z);
        float w = wv.x * wv.y * wv.z;

        // ЗАЩИТА: Если зонд перенесен, но еще не успел пересчитаться в ComputeShader, 
        // его данные - мусор из прошлой локации. Игнорируем его.
        if (distance(expectedWorldPos, actualWorldPos) > 0.1) {
            w = 0.0;
        }

        if (w > 0.0001) {
            irradiance += EvalSHL1(probeIdx, normal) * w;
            weightSum  += w;
        }
    }

    if (weightSum > 0.001) {
        return irradiance / weightSum;
    }

    // Fallback: Если все 8 зондов оказались "грязными" (редкость, только при мгновенных телепортах)
    float dayAmbient = 0.35;
    float nightAmbient = 0.04;
    return vec3(0.6, 0.7, 0.9) * mix(nightAmbient, dayAmbient, clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0));
}

// ============================================================
// POINT LIGHTS (для GlowBall и других динамических светящихся объектов)
// ============================================================
#define MAX_POINT_LIGHTS 32

struct PointLightData {
    vec4 posRadius;      // xyz = мировая позиция, w = радиус влияния
    vec4 colorIntensity; // xyz = цвет, w = интенсивность
};

layout(std430, binding = 18) readonly buffer PointLightBuffer {
    PointLightData pointLights[];
};

uniform int uPointLightCount;

/// Добавляет вклад всех point lights в освещение данной точки.
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

        // Физически корректное затухание с плавным обрезанием
        float falloff  = (dist * dist);
        float window   = max(0.0, 1.0 - (dist / lRadius));
        window         = window * window * window * window;
        float attenuation = (lIntensity / max(falloff, 0.01)) * window;

        totalLight += lColor * nDotL * attenuation;
    }
    return totalLight;
} 