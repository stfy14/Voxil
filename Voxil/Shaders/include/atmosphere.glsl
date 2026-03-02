// --- START OF FILE include/atmosphere.glsl ---

// =============================================================================
// НАСТРОЙКИ АТМОСФЕРЫ И НЕБА
// =============================================================================
#define FOG_DENSITY 1.5
#define FOG_CURVE 4.0
#define FOG_SUN_GLOW 0.3

// =============================================================================
// НАСТРОЙКИ ЛУНЫ
// =============================================================================
#define MOON_SIZE 0.9996                  // Размер диска луны (ближе к 1.0 = меньше)
#define MOON_GLOW_INTENSITY 0.05          // Яркость ореола ночью (было 0.15, стало 0.05 - еле заметно)
#define MOON_GLOW_SHARPNESS 400.0         // Радиус ореола (больше значение = меньше радиус, было 256.0)
#define MOON_DAY_BRIGHTNESS 0.15          // Видимость луны днем (1.0 = белая, 0.0 = невидимая, было 0.35)
#define MOON_NIGHT_EARTHSHINE 0.003       // Подсветка темной стороны луны ночью (пепельный свет, было ~0.01)
#define MOON_PHASE_EDGE_MIN 0.01          // Резкость терминатора (границы света и тени) - нижний порог
#define MOON_PHASE_EDGE_MAX 0.15          // Резкость терминатора - верхний порог

// =============================================================================
// НАСТРОЙКИ СОЛНЦА
// =============================================================================
#define SUN_SIZE 0.9985                   // Размер диска солнца
#define SUN_GLOW_INTENSITY 1.5            // Яркость ореола солнца днем
#define SUN_GLOW_SHARPNESS 64.0           // Радиус ореола солнца

// =============================================================================

vec3 GetSkyColor(vec3 rayDir, vec3 sunDir) {
    // Палитры неба
    vec3 dayHorizon = vec3(0.60, 0.75, 0.95);
    vec3 dayTop = vec3(0.3, 0.5, 0.85);
    
    vec3 sunsetHorizon = vec3(1.0, 0.4, 0.1); 
    vec3 sunsetTop = vec3(0.4, 0.3, 0.6);
    
    vec3 nightHorizon = vec3(0.02, 0.02, 0.05);
    vec3 nightTop = vec3(0.005, 0.005, 0.015);

    // Факторы времени суток
    float sunHeight = sunDir.y;
    float dayFactor = clamp(sunHeight * 4.0 + 0.2, 0.0, 1.0); 
    float sunsetFactor = clamp(1.0 - abs(sunHeight) * 5.0, 0.0, 1.0); 

    // Базовый цвет чистого неба
    vec3 currentHorizon = mix(mix(nightHorizon, dayHorizon, dayFactor), sunsetHorizon, sunsetFactor);
    vec3 currentTop = mix(mix(nightTop, dayTop, dayFactor), sunsetTop, sunsetFactor);
    vec3 baseSky = mix(currentHorizon, currentTop, max(rayDir.y, 0.0));
    
    vec3 skyFinal = baseSky;

    // --- ЗВЕЗДЫ ---
    if (dayFactor < 0.6 && rayDir.y > 0.0) {
        vec2 starCoord = rayDir.xy + rayDir.zx * 0.5 + uTime * 0.001;
        float star = fract(sin(dot(starCoord, vec2(12.9898, 78.233))) * 43758.5453);
        star = step(0.998, star); 
        float starVisibility = (1.0 - dayFactor / 0.6) * clamp(rayDir.y * 3.0, 0.0, 1.0);
        skyFinal += vec3(star) * starVisibility;
    }

    // --- СОЛНЦЕ ---
    float sunDot = dot(rayDir, uSunDir);
    float sunDisc = step(SUN_SIZE, sunDot) * clamp(sunHeight * 10.0 + 1.0, 0.0, 1.0);
    float sunGlow = pow(max(sunDot, 0.0), SUN_GLOW_SHARPNESS) * dayFactor;
    
    skyFinal += vec3(1.0, 0.8, 0.6) * sunGlow * SUN_GLOW_INTENSITY;
    skyFinal = mix(skyFinal, vec3(1.0, 0.95, 0.9) * 10.0, sunDisc);

    // --- ЛУНА И ФАЗЫ ---
    float moonDot = dot(rayDir, uMoonDir);
    
    // ОРЕОЛ луны (работает только ночью, настраивается константами)
    float moonGlow = pow(max(moonDot, 0.0), MOON_GLOW_SHARPNESS) * (1.0 - dayFactor);
    skyFinal += vec3(0.2, 0.3, 0.5) * moonGlow * MOON_GLOW_INTENSITY;

    if (moonDot > MOON_SIZE) { 
        // 3D нормаль для правильной фазы
        float r = sqrt(1.0 - MOON_SIZE * MOON_SIZE);
        vec3 planePos = rayDir - uMoonDir * moonDot;
        float d = length(planePos) / r; 
        vec3 sphereNormal = normalize(uMoonDir * sqrt(max(1.0 - d*d, 0.0)) + planePos / r);
        
        // Освещенность и резкость фазы
        float moonLighting = max(dot(sphereNormal, uSunDir), 0.0);
        float phase = smoothstep(MOON_PHASE_EDGE_MIN, MOON_PHASE_EDGE_MAX, moonLighting);
        
        // --- ОСВЕЩЕННАЯ ЧАСТЬ (МЕСЯЦ) ---
        vec3 litColorNight = vec3(0.95, 0.98, 1.0); 
        vec3 litColorDay = mix(baseSky, vec3(1.0), MOON_DAY_BRIGHTNESS); 
        vec3 litColor = mix(litColorNight, litColorDay, dayFactor);
        
        // --- ТЕМНАЯ ЧАСТЬ (ПЕПЕЛЬНЫЙ СВЕТ) ---
        // Слегка подкрашиваем темную часть ночью (чуть-чуть сине-голубым)
        vec3 darkColorNight = baseSky + vec3(0.7, 0.85, 1.0) * MOON_NIGHT_EARTHSHINE; 
        vec3 darkColorDay = baseSky; // Днем темная часть ИДЕАЛЬНО маскируется под небо (для затмений)
        vec3 darkColor = mix(darkColorNight, darkColorDay, dayFactor);
        
        // Собираем луну
        vec3 moonColor = mix(darkColor, litColor, phase);

        // Сглаживание краев диска
        float edgeSoftness = smoothstep(MOON_SIZE, MOON_SIZE + 0.00003, moonDot);
        float moonVisibility = clamp(uMoonDir.y * 10.0 + 1.0, 0.0, 1.0);
        
        skyFinal = mix(skyFinal, moonColor, edgeSoftness * moonVisibility);
    }

    return skyFinal;
}

vec3 ApplyFog(vec3 color, vec3 rayDir, vec3 sunDir, float dist, float maxDist) {
    vec3 dayHorizon = vec3(0.60, 0.75, 0.95);
    vec3 sunsetHorizon = vec3(1.0, 0.4, 0.1);
    vec3 nightHorizon = vec3(0.02, 0.02, 0.05);

    float sunHeight = sunDir.y;
    float dayFactor = clamp(sunHeight * 4.0 + 0.2, 0.0, 1.0);
    float sunsetFactor = clamp(1.0 - abs(sunHeight) * 5.0, 0.0, 1.0);

    vec3 fogBase = mix(mix(nightHorizon, dayHorizon, dayFactor), sunsetHorizon, sunsetFactor);

    float distRatio = dist / maxDist;
    float fogSunDot = dot(rayDir, sunDir);
    float fogSunGlow = pow(max(fogSunDot, 0.0), 16.0) * dayFactor;
    
    vec3 fogColor = fogBase + vec3(1.0, 0.8, 0.6) * fogSunGlow * FOG_SUN_GLOW;

    float fogFactor = 1.0 - exp(-pow(distRatio * FOG_DENSITY, FOG_CURVE));
    return mix(color, fogColor, clamp(fogFactor, 0.0, 1.0));
}