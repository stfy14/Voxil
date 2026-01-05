// =============================================================================
// PHOTON WATER SETTINGS
// =============================================================================
#define WATER_PARALLAX
#define WATER_WAVES
#define WATER_WAVE_ITERATIONS 3
#define WATER_WAVE_STRENGTH 1.00
#define WATER_WAVE_FREQUENCY 1.00
#define WATER_WAVE_SPEED_STILL 1.00
#define WATER_WAVE_SPEED_FLOWING 1.00
#define WATER_WAVE_PERSISTENCE 1.00
#define WATER_WAVE_LACUNARITY 1.00
#define WATER_WAVES_HEIGHT_VARIATION
#define WATER_ABSORPTION_R_UNDERWATER 0.20
#define WATER_ABSORPTION_G_UNDERWATER 0.08
#define WATER_ABSORPTION_B_UNDERWATER 0.04
#define WATER_SCATTERING_UNDERWATER 0.03

const float tau = 6.28318530718;
const float degree = 0.0174532925;
const float golden_angle = 2.39996323;

// Helper Math
float sqr(float x) { return x * x; }
float rcp(float x) { return 1.0 / x; }

// =============================================================================
// NOISE GENERATION (TWO MODES)
// =============================================================================

#ifdef WATER_MODE_PROCEDURAL
    // === ВАРИАНТ 1: ПРОЦЕДУРНЫЙ ШУМ (АГРЕССИВНЫЙ) ===
float hash(vec2 p) {
    vec3 p3  = fract(vec3(p.xyx) * .1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float value_noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float sample_water_noise(vec2 coord) {
    return value_noise(coord * 256.0);
}

#else
    // === ВАРИАНТ 2: ТЕКСТУРНЫЙ ШУМ (СПОКОЙНЫЙ) ===
// Texture uniform must be defined in main shader or implicitly available
// Мы предполагаем, что uniform sampler2D uNoiseTexture объявлен в главном файле

float sample_water_noise(vec2 coord) {
    return texture(uNoiseTexture, coord).r;
}
#endif

// =============================================================================
// SHARED WAVE PHYSICS
// =============================================================================

float gerstner_wave(vec2 coord, vec2 wave_dir, float t, float noise, float wavelength) {
    const float g = 9.8;
    float k = tau / wavelength;
    float w = sqrt(g * k);
    float x = w * t - k * (dot(wave_dir, coord) + noise);
    return sqr(sin(x) * 0.5 + 0.5);
}

void water_waves_setup(bool flowing_water, vec2 flow_dir, out vec2 wave_dir, out mat2 wave_rot, out float t) {
    const float wave_speed_still   = 0.5 * WATER_WAVE_SPEED_STILL;
    const float wave_speed_flowing = 0.50 * WATER_WAVE_SPEED_FLOWING;
    const float wave_angle         = 30.0 * degree;
    t = (flowing_water ? wave_speed_flowing : wave_speed_still) * uTime;
    wave_dir = flowing_water ?  flow_dir : vec2(cos(wave_angle), sin(wave_angle));
    wave_rot = flowing_water ? mat2(1.0) : mat2(cos(golden_angle), sin(golden_angle), -sin(golden_angle), cos(golden_angle));
}

float get_water_height(vec2 coord, vec2 wave_dir, mat2 wave_rot, float t) {
    const float wave_frequency     = 0.7 * WATER_WAVE_FREQUENCY;
    const float persistence        = 0.5 * WATER_WAVE_PERSISTENCE;
    const float lacunarity         = 1.7 * WATER_WAVE_LACUNARITY;
    const float noise_frequency    = 0.007;
    const float noise_strength     = 2.0;
    const float height_variation_frequency    = 0.001;
    const float min_height                    = 0.4;
    const float height_variation_scale        = 2.0;
    const float height_variation_offset       = -0.5;
    const float height_variation_scroll_speed = 0.1;
    const float amplitude_normalization_factor = (1.0 - persistence) / (1.0 - pow(persistence, float(WATER_WAVE_ITERATIONS)));

    float wave_noise[WATER_WAVE_ITERATIONS];
    vec2 noise_coord = (coord + vec2(0.0, 0.25 * t)) * noise_frequency;

    for (int i = 0; i < WATER_WAVE_ITERATIONS; ++i) {
        wave_noise[i] = sample_water_noise(noise_coord); // <--- Calls either Proc or Tex function
        noise_coord *= 2.5;
    }

    #ifdef WATER_WAVES_HEIGHT_VARIATION
        float height_variation_noise = sample_water_noise((coord + vec2(0.0, height_variation_scroll_speed * t)) * height_variation_frequency);
    #endif

    float height = 0.0;
    float amplitude = 1.0;
    float frequency = wave_frequency;
    float wave_length = 1.0;

    for (int i = 0; i < WATER_WAVE_ITERATIONS; ++i) {
        height += gerstner_wave(coord * frequency, wave_dir, t, wave_noise[i] * noise_strength, wave_length) * amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
        wave_length *= 1.5;
        wave_dir *= wave_rot;
    }

    #ifdef WATER_WAVES_HEIGHT_VARIATION
        height *= max(min_height, height_variation_noise * height_variation_scale + height_variation_offset);
    #endif

    return height * amplitude_normalization_factor;
}

vec3 get_water_normal_photon(vec3 world_pos, vec3 flat_normal, vec2 coord, vec2 flow_dir, float skylight, bool flowing_water) {
    vec2 wave_dir; mat2 wave_rot; float t;
    water_waves_setup(flowing_water, flow_dir, wave_dir, wave_rot, t);

    const float h = 0.1;
    float wave0 = get_water_height(coord, wave_dir, wave_rot, t);
    float wave1 = get_water_height(coord + vec2(h, 0.0), wave_dir, wave_rot, t);
    float wave2 = get_water_height(coord + vec2(0.0, h), wave_dir, wave_rot, t);

    float normal_influence = 0.05 * WATER_WAVE_STRENGTH;
    vec3 viewDir = normalize(world_pos - uCamPos);
    normal_influence *= smoothstep(0.0, 0.15, abs(dot(flat_normal, viewDir)));

    vec3 normal = vec3(wave1 - wave0, wave2 - wave0, h);
    normal.xy *= normal_influence;
    return normalize(normal);
}

vec2 get_water_parallax_coord(vec3 tangent_dir, vec2 coord, vec2 flow_dir, bool flowing_water) {
    const int step_count = 4;
    const float parallax_depth = 0.2;
    vec2 wave_dir; mat2 wave_rot; float t;
    water_waves_setup(flowing_water, flow_dir, wave_dir, wave_rot, t);
    vec2 ray_step = tangent_dir.xy * rcp(-tangent_dir.z) * parallax_depth * rcp(float(step_count));
    float depth_value = get_water_height(coord, wave_dir, wave_rot, t);
    float depth_march = 0.0;
    float depth_previous;
    int i = 0;
    while (i < step_count && depth_march < depth_value) {
        coord += ray_step;
        depth_previous = depth_value;
        depth_value = get_water_height(coord, wave_dir, wave_rot, t);
        depth_march += rcp(float(step_count));
        i++;
    }
    float depth_before = depth_previous - depth_march + rcp(float(step_count));
    float depth_after  = depth_value - depth_march;
    return mix(coord, coord - ray_step, depth_after / (depth_after - depth_before));
}

float GetMasterWaveSimple(vec2 p, float t) {
    float h = 0.0;
    h += sin(p.x * 0.05 + t * 0.2) * 0.25;
    h += sin(p.y * 0.04 + p.x * 0.03 + t * 0.3) * 0.2;
    h += sin(p.x * 0.2 - p.y * 0.15 + t * 0.6) * 0.1;
    h += cos(length(p) * 0.5 - t * 1.0) * 0.05;
    return h / 0.6;
}

float GetCausticsNoise(vec2 p, float t) {
    float noise = 0.0;
    noise += sin(p.x * 1.5 + t * 1.8);
    noise += sin(p.y * 2.0 - t * 1.5);
    noise += sin((p.x + p.y) * 1.2 + t * 1.2);
    return noise / 3.0;
}

float GetCaustics(vec3 pos, float t) {
    vec2 distortedUV = pos.xz + GetMasterWaveSimple(pos.xz, t) * 0.05;
    float v = GetCausticsNoise(distortedUV, t);
    float val = v * 0.5 + 0.5;
    return pow(val, 16.0) * 0.5;
}