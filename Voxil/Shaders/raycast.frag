#version 450 core
out vec4 FragColor;
in vec2 uv;

// Defines
uniform mat4 uInvView;
uniform mat4 uInvProjection;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"
#include "include/water_impl.glsl"

void main() {
    vec3 safeSunDir = normalize(length(uSunDir) < 0.01 ? vec3(0.0, 0.1, 0.8) : uSunDir);

    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);
    if (abs(rayDir.x) < 1e-6) rayDir.x = 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = 1e-6;
    rayDir = normalize(rayDir);

    HitResult hit = TraceWorld(uCamPos, rayDir, uRenderDistance);

    if (!hit.isHit) {
        vec3 sky = GetSkyColor(rayDir, safeSunDir);
        FragColor = vec4(ApplyPostProcess(sky), 1.0);
        gl_FragDepth = 0.999999;
        return;
    }

    vec3 finalColor = vec3(0);
    vec3 hitPos = uCamPos + rayDir * hit.t;
    vec3 normal = hit.normal;
    vec3 albedo = vec3(1.0);
    float ao = 1.0;

    if (hit.isDynamic) {
        DynamicObject obj = dynObjects[hit.objID];
        normal = normalize(transpose(inverse(mat3(obj.model))) * hit.normal);
        albedo = obj.color.rgb;
        ao = 0.8;
    } else {
        albedo = GetColor(hit.materialID);
        // AO для статики по умолчанию 1.0, но ниже может быть перезаписан
    }

    // === ФЛАГ AO (ТЕПЕРЬ ТУТ) ===
    // Считаем AO ПЕРЕД тем как использовать его в освещении
    #ifdef ENABLE_AO
        if (!hit.isDynamic) {
        ao = CalculateAO(hitPos + normal * 0.001, normal);
    }
    #endif

    // --- 4. ОСВЕЩЕНИЕ ---
    float ndotl = max(dot(normal, safeSunDir), 0.0);
    float shadowFactor = (ndotl > 0.0) ? CalculateShadow(hitPos, normal, safeSunDir) : 0.0;

    vec3 sunLightColor = vec3(1.0, 0.98, 0.95);
    vec3 direct = albedo * sunLightColor * 1.2 * ndotl * shadowFactor;

    // Теперь ao содержит актуальное значение (либо 1.0, либо рассчитанное)
    vec3 ambient = albedo * mix(vec3(0.15, 0.15, 0.2), vec3(0.3, 0.5, 0.8), 0.5 + 0.5 * normal.y) * 0.7 * ao;

    finalColor = direct + ambient;

    // --- 5. ВОДА ---
    if (!hit.isDynamic && hit.materialID == 4u) {
        vec3 viewDir = -rayDir;
        vec3 tangentViewDir = vec3(viewDir.x, viewDir.z, viewDir.y);
        bool flowing = false;
        vec2 flowDir = vec2(1.0, 0.0);
        vec2 parallaxUV = hitPos.xz;

        #ifdef WATER_PARALLAX
             parallaxUV = get_water_parallax_coord(tangentViewDir, hitPos.xz, flowDir, flowing);
        #endif

        vec3 waterNormalTangent = get_water_normal_photon(hitPos, normal, parallaxUV, flowDir, 1.0, flowing);
        vec3 waterNormal = normalize(vec3(waterNormalTangent.x, waterNormalTangent.z, waterNormalTangent.y));
        float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(waterNormal, viewDir), 0.0), 5.0);

        vec3 refDir = reflect(rayDir, waterNormal);
        if (refDir.y < 1e-4) { refDir.y = 1e-4; refDir = normalize(refDir); }
        vec3 refOrigin = hitPos + normal * 0.002;

        HitResult refHit = TraceWorld(refOrigin, refDir, 150.0);
        vec3 reflectionColor = GetSkyColor(refDir, safeSunDir);

        if (refHit.isHit) {
            vec3 refAlbedo = (refHit.isDynamic) ? dynObjects[refHit.objID].color.rgb : GetColor(refHit.materialID);
            float refLight = max(dot(refHit.normal, safeSunDir), 0.2);
            reflectionColor = refAlbedo * refLight * sunLightColor;
            reflectionColor = mix(reflectionColor, GetSkyColor(refDir, safeSunDir), clamp(refHit.t / 150.0, 0.0, 1.0));
        }

        vec3 refractionColor = vec3(0.1, 0.3, 0.5);

        #ifdef ENABLE_WATER_TRANSPARENCY
            vec3 refrOrigin = hitPos - normal * 0.002;
        float tBot = 40.0;
        uint matBot = 0u;

        if (TraceRefractionRay(refrOrigin, rayDir, tBot, tBot, matBot)) {
            vec3 botPos = refrOrigin + rayDir * tBot;
            vec3 botAlbedo = GetColor(matBot);
            float caustics = GetCaustics(botPos, uTime);
            float causticFade = exp(-tBot * 0.3);
            vec3 causticLight = sunLightColor * caustics * causticFade * 1.5;
            float botDiff = max(dot(vec3(0,1,0), safeSunDir), 0.0) * shadowFactor + 0.2;
            vec3 botFinal = botAlbedo * (sunLightColor * botDiff + causticLight);
            vec3 absorb = vec3(WATER_ABSORPTION_R_UNDERWATER, WATER_ABSORPTION_G_UNDERWATER, WATER_ABSORPTION_B_UNDERWATER) * 2.5;
            vec3 transmission = exp(-absorb * tBot);
            refractionColor = botFinal * transmission + vec3(WATER_SCATTERING_UNDERWATER) * (1.0 - transmission);
        }
        #endif

        vec3 halfVec = normalize(safeSunDir - rayDir);
        float specular = pow(max(dot(waterNormal, halfVec), 0.0), 200.0) * shadowFactor;
        finalColor = mix(refractionColor, reflectionColor, fresnel) + specular * sunLightColor;
    }

    finalColor = ApplyFog(finalColor, rayDir, safeSunDir, hit.t, uRenderDistance);
    finalColor = ApplyPostProcess(finalColor);

    FragColor = vec4(finalColor, 1.0);
    vec4 clipPos = uProjection * uView * vec4(hitPos, 1.0);
    gl_FragDepth = (clipPos.z / clipPos.w) * 0.5 + 0.5;
}