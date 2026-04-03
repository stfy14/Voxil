// --- vct.glsl ---
// Anisotropic VCT sampling для lighting pass.
//
// ИЗМЕНЕНИЯ относительно предыдущей версии:
//   + VCT_SkyColor синхронизирован с GetSkyColor() в build-шейдере
//     (раньше были разные числа → небо выглядело по-разному в GI и в VCT)
//   Остальной код без изменений — cone tracing физически корректен.

uniform sampler3D uVCTClipmapL0;
uniform sampler3D uVCTClipmapL1;
uniform sampler3D uVCTClipmapL2;

uniform sampler3D uVCTAnisoL0;
uniform sampler3D uVCTAnisoL1;
uniform sampler3D uVCTAnisoL2;

uniform int uVCTOriginL0X, uVCTOriginL0Y, uVCTOriginL0Z;
uniform int uVCTOriginL1X, uVCTOriginL1Y, uVCTOriginL1Z;
uniform int uVCTOriginL2X, uVCTOriginL2Y, uVCTOriginL2Z;

#define uVCTOriginL0 ivec3(uVCTOriginL0X, uVCTOriginL0Y, uVCTOriginL0Z)
#define uVCTOriginL1 ivec3(uVCTOriginL1X, uVCTOriginL1Y, uVCTOriginL1Z)
#define uVCTOriginL2 ivec3(uVCTOriginL2X, uVCTOriginL2Y, uVCTOriginL2Z)

uniform int uVCTClipmapSize;

// === CONSTANTS ===

const float VCT_CELL_L0 = 2.0;
const float VCT_CELL_L1 = 8.0;
const float VCT_CELL_L2 = 32.0;

// 6 диффузных конусов по верхней полусфере:
//   1 вертикально вверх + 5 под углом 60° к нормали
const vec3 VCT_CONE_DIRS[6] = vec3[](
    vec3( 0.000000,  1.000000,  0.000000),
    vec3( 0.866025,  0.500000,  0.000000),
    vec3( 0.267617,  0.500000,  0.823639),
    vec3(-0.700629,  0.500000,  0.509037),
    vec3(-0.700629,  0.500000, -0.509037),
    vec3( 0.267617,  0.500000, -0.823639)
);

// Веса телесных углов — сумма = 1.0
const float VCT_CONE_WEIGHTS[6] = float[](0.25, 0.15, 0.15, 0.15, 0.15, 0.15);

// tan(30°) — полуугол конуса. 30° × 6 конусов ≈ перекрывают полусферу.
const float VCT_TAN_HALF_ANGLE = 0.57735;

// === TBN ===

mat3 VCT_GetTBN(vec3 N) {
    vec3 helper = (abs(N.y) > 0.9999) ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 T = normalize(cross(helper, N));
    vec3 B = cross(N, T);
    return mat3(T, N, B);
}

// === RADIANCE SAMPLING ===

vec4 VCT_Sample(vec3 pos, float coneDiam) {
    float sizeL0 = float(uVCTClipmapSize) * VCT_CELL_L0;
    float sizeL1 = float(uVCTClipmapSize) * VCT_CELL_L1;
    float sizeL2 = float(uVCTClipmapSize) * VCT_CELL_L2;

    if (coneDiam < VCT_CELL_L1) {
        vec3 rel = pos - vec3(uVCTOriginL0) * VCT_CELL_L0;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL0)))) {
            vec4 s0 = texture(uVCTClipmapL0, fract(pos / sizeL0));
            float bf = smoothstep(VCT_CELL_L1 * 0.5, VCT_CELL_L1, coneDiam);
            if (bf > 0.001) {
                vec3 rel1 = pos - vec3(uVCTOriginL1) * VCT_CELL_L1;
                if (all(greaterThanEqual(rel1, vec3(0.0))) && all(lessThan(rel1, vec3(sizeL1)))) {
                    vec4 s1 = texture(uVCTClipmapL1, fract(pos / sizeL1));
                    return mix(s0, s1, bf);
                }
            }
            return s0;
        }
    }

    if (coneDiam < VCT_CELL_L2) {
        vec3 rel = pos - vec3(uVCTOriginL1) * VCT_CELL_L1;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL1))))
            return texture(uVCTClipmapL1, fract(pos / sizeL1));
    }

    {
        vec3 rel = pos - vec3(uVCTOriginL2) * VCT_CELL_L2;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL2))))
            return texture(uVCTClipmapL2, fract(pos / sizeL2));
    }

    return vec4(0.0);
}

// === ANISOTROPIC OPACITY SAMPLING ===

vec3 VCT_SampleAniso(vec3 pos, float coneDiam) {
    float sizeL0 = float(uVCTClipmapSize) * VCT_CELL_L0;
    float sizeL1 = float(uVCTClipmapSize) * VCT_CELL_L1;
    float sizeL2 = float(uVCTClipmapSize) * VCT_CELL_L2;

    if (coneDiam < VCT_CELL_L1) {
        vec3 rel = pos - vec3(uVCTOriginL0) * VCT_CELL_L0;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL0)))) {
            vec3 a0 = texture(uVCTAnisoL0, fract(pos / sizeL0)).rgb;
            float bf = smoothstep(VCT_CELL_L1 * 0.5, VCT_CELL_L1, coneDiam);
            if (bf > 0.001) {
                vec3 rel1 = pos - vec3(uVCTOriginL1) * VCT_CELL_L1;
                if (all(greaterThanEqual(rel1, vec3(0.0))) && all(lessThan(rel1, vec3(sizeL1)))) {
                    vec3 a1 = texture(uVCTAnisoL1, fract(pos / sizeL1)).rgb;
                    return mix(a0, a1, bf);
                }
            }
            return a0;
        }
    }

    if (coneDiam < VCT_CELL_L2) {
        vec3 rel = pos - vec3(uVCTOriginL1) * VCT_CELL_L1;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL1))))
            return texture(uVCTAnisoL1, fract(pos / sizeL1)).rgb;
    }

    {
        vec3 rel = pos - vec3(uVCTOriginL2) * VCT_CELL_L2;
        if (all(greaterThanEqual(rel, vec3(0.0))) && all(lessThan(rel, vec3(sizeL2))))
            return texture(uVCTAnisoL2, fract(pos / sizeL2)).rgb;
    }

    return vec3(0.0);
}

// Проецирует анизотропную opacity на направление конуса.
// dir=(0,1,0) → используется только op_y (горизонтальный пол → максимальная тень сверху).
// dir=(1,0,0) → только op_x (вертикальная стена → тень сбоку).
float VCT_DirectionalOpacity(vec3 anisoXYZ, vec3 dir) {
    vec3  w   = abs(dir);
    float sum = w.x + w.y + w.z;
    return (sum < 1e-6) ? (anisoXYZ.x + anisoXYZ.y + anisoXYZ.z) / 3.0
                        : dot(w, anisoXYZ) / sum;
}

// === CONE TRACING ===
// Front-to-back compositing с анизотропной opacity.
// Физически: интегрируем extinction вдоль конуса с учётом направления.

vec4 VCT_TraceCone(vec3 origin, vec3 dir, float startT, float maxDist) {
    vec4  accum = vec4(0.0);
    float t     = startT;

    while (t < maxDist && accum.a < 0.95) {
        float diam      = max(2.0 * VCT_TAN_HALF_ANGLE * t, VCT_CELL_L0);
        vec3  samplePos = origin + dir * t;

        vec4 s      = VCT_Sample(samplePos, diam);
        vec3 aniso  = VCT_SampleAniso(samplePos, diam);
        float dirOp = VCT_DirectionalOpacity(aniso, dir);

        // Clamp убирает белые пятна от перегретых вокселей в клипмапе
        vec3 safeRgb = min(s.rgb, vec3(3.0));

        float alpha = dirOp * (1.0 - accum.a);
        accum.rgb  += safeRgb * alpha;
        accum.a    += alpha;

        t += diam * 0.5; // стандартный шаг VCT (0.5 * diameter)
    }

    return accum;
}

// === SKY FALLBACK ===
// ИСПРАВЛЕНО: формула синхронизирована с GetSkyColor() в build-шейдере.
// Раньше числа отличались → небо в GI и в конусах было разного цвета на закате.

vec3 VCT_SkyColor(vec3 dir) {
    float dayF    = clamp(uSunDir.y * 4.0 + 0.2,       0.0, 1.0);
    float sunsetF = clamp(1.0 - abs(uSunDir.y) * 5.0,  0.0, 1.0);
    vec3 night   = vec3(0.04, 0.06, 0.15);
    vec3 day     = vec3(0.60, 0.75, 0.95);
    vec3 sunset  = vec3(0.90, 0.45, 0.15);
    vec3 sky     = mix(mix(night, day, dayF), sunset, sunsetF);

    // Ambient зависит от времени суток
    float ambient = mix(0.04, 0.45, dayF);

    // Конусы, смотрящие вниз, почти не получают свет неба
    float skyVis = max(0.0, dot(dir, vec3(0.0, 1.0, 0.0)) * 0.5 + 0.5);

    return sky * ambient * skyVis;
}

// === MAIN ENTRY POINT ===
// Трейсит 6 диффузных конусов с поверхности worldPos вдоль normal.
// Возвращает irradiance (НЕ умноженную на альбедо — это делается снаружи).

vec3 SampleGIVCT(vec3 worldPos, vec3 normal) {
    mat3  tbn     = VCT_GetTBN(normal);
    float maxDist = float(uVCTClipmapSize) * VCT_CELL_L2;
    // Смещение от поверхности: избегаем самопересечения
    vec3  origin  = worldPos + normal * (VCT_CELL_L0 * 0.6);
    float startT  = VCT_CELL_L0;

    vec3 irradiance = vec3(0.0);

    for (int i = 0; i < 6; i++) {
        vec3 coneDir = normalize(tbn * VCT_CONE_DIRS[i]);
        vec4 result  = VCT_TraceCone(origin, coneDir, startT, maxDist);

        // Если конус вышел за клипмап без полной окклюзии → небо
        float skyFrac   = 1.0 - result.a;
        float upDot     = dot(coneDir, vec3(0.0, 1.0, 0.0));
        float skyWeight = smoothstep(0.75, 1.0, upDot);
        result.rgb += VCT_SkyColor(coneDir) * skyFrac * skyWeight;

        irradiance += result.rgb * VCT_CONE_WEIGHTS[i];
    }

    return irradiance;
}
