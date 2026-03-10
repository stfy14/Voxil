// --- START OF FILE raycast.frag ---

#version 450 core
layout(location = 0) out vec4 gColor;
layout(location = 1) out vec4 gData;
in vec2 uv;
uniform mat4 uCleanProjection;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"
#include "include/water_impl.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"

float GetConservativeBeamDist(vec2 uv) {
    vec2 texSize = vec2(textureSize(uBeamTexture, 0));
    vec2 pixelCoord = uv * texSize - 0.5;
    vec2 baseCoord = floor(pixelCoord) + 0.5;

    vec2 uv00 = (baseCoord + vec2(0.0, 0.0)) / texSize;
    vec2 uv10 = (baseCoord + vec2(1.0, 0.0)) / texSize;
    vec2 uv01 = (baseCoord + vec2(0.0, 1.0)) / texSize;
    vec2 uv11 = (baseCoord + vec2(1.0, 1.0)) / texSize;

    float d00 = texture(uBeamTexture, uv00).r;
    float d10 = texture(uBeamTexture, uv10).r;
    float d01 = texture(uBeamTexture, uv01).r;
    float d11 = texture(uBeamTexture, uv11).r;

    return min(min(d00, d10), min(d01, d11));
}

float ComputeDepth(vec3 pos) {
    vec4 clip_space_pos = uCleanProjection * uView * vec4(pos, 1.0);
    return (clip_space_pos.z / clip_space_pos.w) * 0.5 + 0.5;
}

void main() {
    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    target.xyz /= target.w;
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);

    if (abs(rayDir.x) < 1e-6) rayDir.x = (rayDir.x < 0.0) ? -1e-6 : 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = (rayDir.y < 0.0) ? -1e-6 : 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = (rayDir.z < 0.0) ? -1e-6 : 1e-6;
    rayDir = normalize(rayDir);

    int steps = 0;

    #ifdef ENABLE_BEAM_OPTIMIZATION
    if (uIsBeamPass == 1) {
        float tStatic = uRenderDistance; uint matStatic = 0u; vec3 normStatic = vec3(0);
        bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, 0.0, tStatic, matStatic, normStatic, steps);

        float finalDepth = uRenderDistance;
        if (hitStatic && tStatic <= uRenderDistance) finalDepth = tStatic;

        gColor = vec4(finalDepth, 0.0, 0.0, 1.0);
        gData  = vec4(0.0);
        return;
    }
    #endif

    float tStart = 0.0;
    bool skipStatic = false;

    #ifdef ENABLE_BEAM_OPTIMIZATION
    if (uIsBeamPass == 0) {
        float beamDist = GetConservativeBeamDist(uv);
        if (beamDist >= uRenderDistance - 1.0) skipStatic = true;
        else tStart = max(0.0, beamDist - 4.0);
    }
    #endif

    float tStatic = uRenderDistance;
    uint matStatic = 0u;
    vec3 normStatic = vec3(0);
    bool hitStatic = false;

    if (!skipStatic) {
        hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, tStart, tStatic, matStatic, normStatic, steps);
    }

    // ИСПРАВЛЕНИЕ: Отменяем любые попадания, если они произошли за границей прорисовки!
    if (hitStatic && tStatic > uRenderDistance) hitStatic = false;

    float tDyn = uRenderDistance;
    int idDyn = -1;
    vec3 normDyn = vec3(0);
    uint dynMatID = 0u;
    bool hitDyn = false;

    if (uObjectCount > 0) {
        hitDyn = TraceDynamicRay(uCamPos, rayDir, min(tStatic, uRenderDistance), tDyn, idDyn, normDyn, dynMatID, steps);
    }

    // ИСПРАВЛЕНИЕ ДИНАМИКИ: То же самое для динамических объектов
    if (hitDyn && tDyn > uRenderDistance) hitDyn = false;

    bool hit = false; float tFinal = uRenderDistance; vec3 normal = vec3(0); vec3 albedo = vec3(0);
    bool isEmissive = false; // Флаг светящегося материала

    if (hitDyn && tDyn < tStatic) {
        hit = true; tFinal = tDyn;
        normal = normalize(normDyn);
        albedo = (dynMatID != 0u) ? GetColor(dynMatID) : dynObjects[idDyn].color.rgb;

        // 7u - это MaterialType.Glow
        if (dynMatID == 7u) isEmissive = true;

        #ifdef EDITOR_MODE
            vec3 localHit = (dynObjects[idDyn].invModel * vec4(uCamPos + rayDir * tDyn, 1.0)).xyz;
        if (all(greaterThanEqual(localHit, uHoverVoxelMin)) &&
        all(lessThan(localHit, uHoverVoxelMax)))
        {
            albedo = mix(albedo, vec3(1.0), 0.25);
        }
        #endif
    }
    else if (hitStatic) {
        hit = true; tFinal = tStatic; normal = normStatic; albedo = GetColor(matStatic);
        if (matStatic == 7u) isEmissive = true;
    }

    if (uShowDebugHeatmap) {
        float val = float(steps) / 300.0;
        vec3 col = vec3(0.0);
        if (val < 0.25)      col = mix(vec3(0,0,0.5), vec3(0,0,1), val * 4.0);
        else if (val < 0.5)  col = mix(vec3(0,0,1),   vec3(0,1,0), (val-0.25)*4.0);
        else if (val < 0.75) col = mix(vec3(0,1,0),   vec3(1,1,0), (val-0.5)*4.0);
        else                 col = mix(vec3(1,1,0),   vec3(1,0,0), (val-0.75)*4.0);
        gColor = vec4(col, uRenderDistance);
        gData  = vec4(0.0, 1.0, 0.0, 0.0);

        if (hit) gl_FragDepth = ComputeDepth(uCamPos + rayDir * tFinal);
        else gl_FragDepth = 0.999999;
        return;
    }

    if (!hit) {
        vec3 skyColor = GetSkyColor(rayDir, uSunDir);
        gColor = vec4(skyColor, uRenderDistance * 2.0);
        gData  = vec4(0.0);
        gl_FragDepth = 0.999999;
        return;
    }

    // ТРЮК: Кодируем свечение в G-buffer делая цвет отрицательным!
    // Формат текстуры (RGBA16F) поддерживает отрицательные числа.
    if (isEmissive) {
        albedo = -max(albedo, vec3(0.001));
    }

    vec3 hitPos = uCamPos + rayDir * tFinal;

    float sunIntensity  = clamp(uSunDir.y  * 5.0, 0.0, 1.0);
    float moonIntensity = clamp(uMoonDir.y * 5.0, 0.0, 1.0) * 0.25
    * clamp(-uSunDir.y * 5.0, 0.0, 1.0);

    vec3  activeLightDir  = vec3(0, 1, 0);
    float lightIntensity  = 0.0;

    if (sunIntensity > 0.05) {
        activeLightDir = uSunDir;
        lightIntensity = sunIntensity;
    } else if (moonIntensity > 0.01) {
        activeLightDir = uMoonDir;
        lightIntensity = moonIntensity;
    }

    float ndotl = max(dot(normal, activeLightDir), 0.0);

    gColor = vec4(albedo, tFinal);
    gData  = vec4(normal, ndotl * lightIntensity);
    gl_FragDepth = ComputeDepth(hitPos);
}