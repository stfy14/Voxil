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
    vec3 sunColor = vec3(1.0, 0.9, 0.8) * 100.0;
    vec3 sunGlowColor = vec3(1.0, 0.9, 0.8);

    float distRatio = dist / maxDist;
    float fogSunDot = dot(rayDir, sunDir);
    float fogSunCore = step(0.998, fogSunDot);
    float fogSunDisc = step(0.993, fogSunDot);
    float fogSunGlow = pow(max(fogSunDot, 0.0), 32.0);

    vec3 fogSkyBase = mix(horizonColor, skyTopColor, max(rayDir.y, 0.0));
    vec3 fogColor = fogSkyBase + sunGlowColor * fogSunGlow * 0.6;
    fogColor = mix(fogColor, sunColor, fogSunDisc);
    fogColor = mix(fogColor, vec3(1.0), fogSunCore);

    float fogFactor = pow(clamp(distRatio, 0.0, 1.0), 3.0);

    // Mix with distance fog
    color = mix(color, mix(color, vec3(dot(color, vec3(0.3, 0.59, 0.11))), fogFactor * 0.7), 1.0); // Desaturation at distance
    return mix(color, fogColor, fogFactor);
}