// --- START OF FILE include/atmosphere.glsl ---

// =============================================================================
// НАСТРОЙКИ АТМОСФЕРЫ И НЕБА
// =============================================================================
#define FOG_DENSITY 1.5
#define FOG_CURVE 4.0
#define FOG_SUN_GLOW 0.3

// =============================================================================
// НАСТРОЙКИ ЗВЕЗД
// =============================================================================
#define STAR_SCALE 250.0                // Размер сетки (больше = больше звезд)
#define STAR_SIZE 0.30                  // Базовый размер звезды внутри ячейки (0.01 - 0.50)
#define STAR_SOFTNESS 0.15              // Размытие краев (спасет от пропадания пикселей из-за TAA)
#define STAR_WARP_FREQ 24.0             // "Кучность" созвездий (ломает ровные линии). Больше - меньше линий
#define STAR_WARP_AMP 0.3               // Сила хаоса в распределении
#define STAR_THRESHOLD 0.994            // Порог генерации (0.990 = 1% неба покрыто звездами)
#define STAR_BRIGHTNESS 6.0             // Яркость вспышек при мерцании
#define STAR_MACRO_SPEED 2.0            // Скорость смены созвездий при перемотке времени (Time of Day)
#define STAR_MICRO_SPEED 4.0            // Скорость хаотичного мерцания в реальном времени

// =============================================================================
// НАСТРОЙКИ ЛУНЫ
// =============================================================================
#define MOON_SIZE 0.9996                  // Размер диска луны (ближе к 1.0 = меньше)
#define MOON_GLOW_INTENSITY 0.05          // Яркость ореола ночью
#define MOON_GLOW_SHARPNESS 200.0         // Радиус ореола
#define MOON_DAY_BRIGHTNESS 0.15          // Видимость луны днем
#define MOON_NIGHT_EARTHSHINE 0.003       // Подсветка темной стороны луны ночью
#define MOON_PHASE_EDGE_MIN 0.01          // Резкость терминатора (границы света и тени) - нижний порог
#define MOON_PHASE_EDGE_MAX 0.15          // Резкость терминатора - верхний порог

// =============================================================================
// НАСТРОЙКИ СОЛНЦА
// =============================================================================
#define SUN_SIZE 0.9985                   // Размер диска солнца
#define SUN_GLOW_INTENSITY 1.5            // Яркость ореола солнца днем
#define SUN_GLOW_SHARPNESS 64.0           // Радиус ореола солнца

// =============================================================================

// ИСПРАВЛЕНИЕ: Выделяем общий базовый градиент неба для тумана и фона
vec3 GetBaseSkyColor(vec3 rayDir) {
    vec3 dayHorizon = vec3(0.60, 0.75, 0.95);
    vec3 dayTop = vec3(0.3, 0.5, 0.85);
    
    vec3 sunsetHorizon = vec3(1.0, 0.4, 0.1); 
    vec3 sunsetTop = vec3(0.4, 0.3, 0.6);
    
    vec3 nightHorizon = vec3(0.02, 0.02, 0.05);
    vec3 nightTop = vec3(0.005, 0.005, 0.015);

    float sunHeight = uSunDir.y;
    float dayFactor = clamp(sunHeight * 4.0 + 0.2, 0.0, 1.0); 
    float sunsetFactor = clamp(1.0 - abs(sunHeight) * 5.0, 0.0, 1.0); 

    vec3 currentHorizon = mix(mix(nightHorizon, dayHorizon, dayFactor), sunsetHorizon, sunsetFactor);
    vec3 currentTop = mix(mix(nightTop, dayTop, dayFactor), sunsetTop, sunsetFactor);
    
    // Градиент по высоте! Теперь туман будет темнеть, если смотреть вверх
    return mix(currentHorizon, currentTop, max(rayDir.y, 0.0));
}

// ИСПРАВЛЕНИЕ: Общее атмосферное свечение
vec3 GetAtmosphericGlow(vec3 rayDir) {
    float sunHeight = uSunDir.y;
    float dayFactor = clamp(sunHeight * 4.0 + 0.2, 0.0, 1.0);
    
    vec3 glow = vec3(0.0);

    // Ореол солнца
    float sunDot = dot(rayDir, uSunDir);
    float sunGlow = pow(max(sunDot, 0.0), SUN_GLOW_SHARPNESS) * dayFactor;
    glow += vec3(1.0, 0.8, 0.6) * sunGlow * SUN_GLOW_INTENSITY;

    // Ореол луны
    float moonDot = dot(rayDir, uMoonDir);
    float moonGlow = pow(max(moonDot, 0.0), MOON_GLOW_SHARPNESS) * (1.0 - dayFactor);
    glow += vec3(0.2, 0.3, 0.5) * moonGlow * MOON_GLOW_INTENSITY;

    return glow;
}

vec3 GetSkyColor(vec3 rayDir, vec3 sunDir) {
    float sunHeight = uSunDir.y;
    float dayFactor = clamp(sunHeight * 4.0 + 0.2, 0.0, 1.0); 

    vec3 baseSky = GetBaseSkyColor(rayDir);
    vec3 skyFinal = baseSky + GetAtmosphericGlow(rayDir);

    // --- ЗВЕЗДЫ ---
    // Убираем проверку rayDir.y > 0.0. Теперь звезды генерируются по всей сфере!
    if (dayFactor < 0.6) {
        // 1. Искривление пространства (Domain Warping)
        vec3 warp = vec3(
            sin(rayDir.y * STAR_WARP_FREQ) * cos(rayDir.z * STAR_WARP_FREQ),
            sin(rayDir.z * STAR_WARP_FREQ) * cos(rayDir.x * STAR_WARP_FREQ),
            sin(rayDir.x * STAR_WARP_FREQ) * cos(rayDir.y * STAR_WARP_FREQ)
        ) * STAR_WARP_AMP;
        
        vec3 coord = (rayDir + warp) * STAR_SCALE;
        vec3 starGrid = floor(coord);
        vec3 starFract = fract(coord); 
        
        float starHash = fract(sin(dot(starGrid, vec3(12.9898, 78.233, 54.539))) * 43758.5453);
        
        // ХАРАКТЕР ЗВЕЗДЫ
        float starPhase       = fract(starHash * 133.733);
        float starBlinkFactor = fract(starHash * 283.123);
        float baseLuma        = 0.1 + fract(starHash * 73.123) * 0.9;
        starBlinkFactor = pow(starBlinkFactor, 3.0); 
        
        // ФОРМА И РАЗМЕР
        float dist = length(starFract - vec3(0.5));
        float currentSize = STAR_SIZE * (0.5 + baseLuma * 0.5);
        float starShape = smoothstep(currentSize + STAR_SOFTNESS, currentSize, dist);
        float isStar = step(STAR_THRESHOLD, starHash) * starShape; 
        
        // МАКРО-ВРЕМЯ (Смена созвездий)
        float gameTime = atan(uSunDir.y, uSunDir.x); 
        float macroFade = sin(gameTime * STAR_MACRO_SPEED + starPhase * 6.28318);
        macroFade = smoothstep(0.0, 0.8, macroFade); 
        
        // МИКРО-МЕРЦАНИЕ
        float t = uTime * STAR_MICRO_SPEED * (0.5 + starPhase * 0.5) + starPhase * 100.0;
        float blink = sin(t) * sin(t * 1.414) * sin(t * 1.732);
        float twinkle = baseLuma + pow(max(0.0, blink), 2.0) * STAR_BRIGHTNESS * starBlinkFactor;
        
        // 7. ВИДИМОСТЬ
        // Оставляем только затухание от дневного света. 
        // Горизонт теперь будет прятаться за настоящим туманом, а не исчезать по формуле.
        float starVisibility = (1.0 - dayFactor / 0.6);
        
        skyFinal += vec3(isStar * macroFade * twinkle) * starVisibility;
    }

    // --- СОЛНЦЕ (Сам диск) ---
    float sunDot = dot(rayDir, uSunDir);
    float sunDisc = step(SUN_SIZE, sunDot) * clamp(sunHeight * 10.0 + 1.0, 0.0, 1.0);
    skyFinal = mix(skyFinal, vec3(1.0, 0.95, 0.9) * 10.0, sunDisc);

    // --- ЛУНА И ФАЗЫ ---
    float moonDot = dot(rayDir, uMoonDir);
    
    if (moonDot > MOON_SIZE) { 
        float r = sqrt(1.0 - MOON_SIZE * MOON_SIZE);
        vec3 planePos = rayDir - uMoonDir * moonDot;
        float d = length(planePos) / r; 
        vec3 sphereNormal = normalize(uMoonDir * sqrt(max(1.0 - d*d, 0.0)) + planePos / r);
        
        float moonLighting = max(dot(sphereNormal, uSunDir), 0.0);
        float phase = smoothstep(MOON_PHASE_EDGE_MIN, MOON_PHASE_EDGE_MAX, moonLighting);
        
        vec3 litColorNight = vec3(0.95, 0.98, 1.0); 
        vec3 litColorDay = mix(baseSky, vec3(1.0), MOON_DAY_BRIGHTNESS); 
        vec3 litColor = mix(litColorNight, litColorDay, dayFactor);
        
        vec3 darkColorNight = baseSky + vec3(0.7, 0.85, 1.0) * MOON_NIGHT_EARTHSHINE; 
        vec3 darkColorDay = baseSky; 
        vec3 darkColor = mix(darkColorNight, darkColorDay, dayFactor);
        
        vec3 moonColor = mix(darkColor, litColor, phase);

        float edgeSoftness = smoothstep(MOON_SIZE, MOON_SIZE + 0.00003, moonDot);
        float moonVisibility = clamp(uMoonDir.y * 10.0 + 1.0, 0.0, 1.0);
        
        skyFinal = mix(skyFinal, moonColor, edgeSoftness * moonVisibility);
    }

    return skyFinal;
}

vec3 ApplyFog(vec3 color, vec3 rayDir, vec3 sunDir, float dist, float maxDist) {
    // ИСПРАВЛЕНИЕ 1: Идеальное совпадение цвета тумана с градиентом неба позади!
    vec3 fogBase = GetBaseSkyColor(rayDir);
    vec3 fogGlow = GetAtmosphericGlow(rayDir);
    
    // Оставляем фактор свечения солнца для художественности
    vec3 fogColor = fogBase + fogGlow * FOG_SUN_GLOW;

    // ИСПРАВЛЕНИЕ 2: Форсируем 100% густоту тумана ровно на 95% дистанции.
    // Это полностью спрячет границы загрузки дальних чанков
    float distRatio = clamp(dist / (maxDist * 0.95), 0.0, 1.0);
    
    float fogFactor = 1.0 - exp(-pow(distRatio * FOG_DENSITY, FOG_CURVE));
    return mix(color, fogColor, clamp(fogFactor, 0.0, 1.0));
}