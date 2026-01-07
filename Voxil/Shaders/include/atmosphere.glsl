// --- START OF FILE include/atmosphere.glsl ---

// =============================================================================
//                            ATMOSPHERE SETTINGS
// =============================================================================

// Плотность тумана на границе прорисовки.
// 1.5 - оптимально, туман мягкий. 
// 2.5 - очень густой (как было раньше).
#define FOG_DENSITY 1.5

// Кривая тумана. 
// 1.0 = Линейный (туман начинается прямо от лица).
// 2.0 = Квадратичный (стандарт).
// 4.0 = Туман "отодвигается" к краям карты, вблизи всё четко.
#define FOG_CURVE 4.0

// Сила свечения тумана вокруг солнца (без диска)
#define FOG_SUN_GLOW 0.3

// =============================================================================

vec3 GetSkyColor(vec3 rayDir, vec3 sunDir) {
    vec3 horizonColor = vec3(0.60, 0.75, 0.95);
    vec3 skyTopColor = vec3(0.3, 0.5, 0.85);
    vec3 sunColor = vec3(1.0, 0.9, 0.8) * 100.0;
    vec3 sunGlowColor = vec3(1.0, 0.9, 0.8);

    vec3 skyBase = mix(horizonColor, skyTopColor, max(rayDir.y, 0.0));
    float sunDot = dot(rayDir, sunDir);
    float sunDisc = step(0.9985, sunDot);
    float sunGlow = pow(max(sunDot, 0.0), 64.0);

    vec3 skyFinal = skyBase + sunGlowColor * sunGlow * 1.5;
    skyFinal = mix(skyFinal, sunColor, sunDisc);
    return skyFinal;
}

vec3 ApplyFog(vec3 color, vec3 rayDir, vec3 sunDir, float dist, float maxDist) {
    vec3 horizonColor = vec3(0.60, 0.75, 0.95);
    vec3 skyTopColor = vec3(0.3, 0.5, 0.85);
    vec3 sunGlowColor = vec3(1.0, 0.9, 0.8);

    float distRatio = dist / maxDist;
    float fogSunDot = dot(rayDir, sunDir);

    // Свечение тумана вокруг солнца
    float fogSunGlow = pow(max(fogSunDot, 0.0), 16.0);

    vec3 fogSkyBase = mix(horizonColor, skyTopColor, max(rayDir.y, 0.0));

    // Цвет тумана
    vec3 fogColor = fogSkyBase + sunGlowColor * fogSunGlow * FOG_SUN_GLOW;

    // Расчет фактора тумана с новыми настройками
    // Используем FOG_CURVE, чтобы "отодвинуть" туман от камеры
    float fogFactor = 1.0 - exp(-pow(distRatio * FOG_DENSITY, FOG_CURVE));

    fogFactor = clamp(fogFactor, 0.0, 1.0);

    return mix(color, fogColor, fogFactor);
}