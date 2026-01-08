#version 450 core
out vec4 FragColor;
in vec2 uv;

// Defines and Uniforms
uniform mat4 uInvView;
uniform mat4 uInvProjection;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/lighting.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"
#include "include/water_impl.glsl"

void main() {
    vec3 safeSunDir = normalize(length(uSunDir) < 0.01 ? vec3(0.2, 0.4, 0.8) : uSunDir);

    // 1. Генерация луча
    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);
    if (abs(rayDir.x) < 1e-6) rayDir.x = 1e-6;
    if (abs(rayDir.y) < 1e-6) rayDir.y = 1e-6;
    if (abs(rayDir.z) < 1e-6) rayDir.z = 1e-6;
    rayDir = normalize(rayDir);

    // 2. Рендер неба (по умолчанию)
    vec3 finalColor = GetSkyColor(rayDir, safeSunDir);
    float finalDepth = 0.999999;

    // 3. Трассировка
    float tDynamicHit = uRenderDistance;
    int bestObjID = -1;
    vec3 bestLocalNormal = vec3(0);
    bool hitDyn = false;

    if (uObjectCount > 0) {
        hitDyn = TraceDynamicRay(uCamPos, rayDir, uRenderDistance, tDynamicHit, bestObjID, bestLocalNormal);
    }

    float tStaticHit = uRenderDistance;
    uint staticHitMat = 0u;
    vec3 finalNormal = vec3(0);
    bool hitStatic = TraceStaticRay(uCamPos, rayDir, uRenderDistance, tStaticHit, staticHitMat, finalNormal);

    float tEnd = min(tStaticHit, tDynamicHit);

    // 4. Расчет освещения поверхности
    if (tEnd < uRenderDistance) {
        bool isDynamic = tDynamicHit < tStaticHit;
        vec3 hitPos = uCamPos + rayDir * tEnd;
        vec3 normal, albedo;
        float ao = 1.0;

        if (isDynamic) {
            normal = normalize(transpose(inverse(mat3(dynObjects[bestObjID].model))) * bestLocalNormal);
            albedo = dynObjects[bestObjID].color.rgb;
        } else {
            normal = finalNormal;
            albedo = GetColor(staticHitMat);
            #ifdef ENABLE_AO
                ao = CalculateAO(hitPos + normal * 0.001, normal);
            #endif
        }

        float ndotl = max(dot(normal, safeSunDir), 0.0);
        float shadowFactor = (ndotl > 0.0) ? CalculateShadow(hitPos, normal, safeSunDir) : 0.0;

        vec3 sunLightColor = vec3(1.0, 0.98, 0.95);
        vec3 direct = albedo * sunLightColor * 1.2 * ndotl * shadowFactor;
        vec3 ambient = albedo * mix(vec3(0.15, 0.15, 0.2), vec3(0.3, 0.5, 0.8), 0.5 + 0.5 * normal.y) * 0.7 * ao;
        finalColor = direct + ambient;

        // --- ВОДА ---
        if (!isDynamic && staticHitMat == 4u) {
            vec3 viewDir = -rayDir;
            vec3 tangentViewDir = vec3(viewDir.x, viewDir.z, viewDir.y);
            vec2 parallaxUV = get_water_parallax_coord(tangentViewDir, hitPos.xz, vec2(0), false);
            vec3 waterNormalTangent = get_water_normal_photon(hitPos, finalNormal, parallaxUV, vec2(0), 1.0, false);
            vec3 waterNormal = normalize(vec3(waterNormalTangent.x, waterNormalTangent.z, waterNormalTangent.y));
            float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(waterNormal, viewDir), 0.0), 5.0);

            vec3 refDir = reflect(rayDir, waterNormal);
            if (refDir.y < 1e-4) { refDir.y = 1e-4; refDir = normalize(refDir); }
            vec3 refOrigin = hitPos + finalNormal * 0.002;

            float tRefStatic = 150.0; uint matRef = 0u; vec3 refNormStatic = vec3(0);
            float tRefDyn = 150.0; int idRef = -1; vec3 refNormDyn = vec3(0);

            bool hitRefStatic = TraceStaticRay(refOrigin, refDir, 150.0, tRefStatic, matRef, refNormStatic);
            bool hitRefDyn = uObjectCount > 0 ? TraceDynamicRay(refOrigin, refDir, 150.0, tRefDyn, idRef, refNormDyn) : false;

            vec3 reflectionColor = GetSkyColor(refDir, safeSunDir);
            float tRefFinal = min(tRefStatic, tRefDyn);

            if (tRefFinal < 150.0) {
                vec3 refAlbedo, refN;
                if(tRefDyn < tRefStatic) {
                    refAlbedo = dynObjects[idRef].color.rgb;
                    refN = normalize(transpose(inverse(mat3(dynObjects[idRef].model))) * refNormDyn);
                } else {
                    refAlbedo = GetColor(matRef);
                    refN = refNormStatic;
                }
                float refLight = max(dot(refN, safeSunDir), 0.2);
                reflectionColor = refAlbedo * refLight * sunLightColor;
                reflectionColor = mix(reflectionColor, GetSkyColor(refDir, safeSunDir), pow(clamp(tRefFinal / 150.0, 0.0, 1.0), 2.0));
            }

            vec3 refractionColor = vec3(0.05, 0.1, 0.15);
            #ifdef ENABLE_WATER_TRANSPARENCY
                vec3 refrOrigin = hitPos - finalNormal * 0.002;
            float tBot = 40.0;
            uint matBot = 0u;
            if (TraceRefractionRay(refrOrigin, rayDir, 40.0, tBot, matBot)) {
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

            vec3 halfVec = normalize(safeSunDir - viewDir);
            float specular = pow(max(dot(waterNormal, halfVec), 0.0), 200.0) * shadowFactor;
            finalColor = mix(refractionColor, reflectionColor, fresnel) + specular * sunLightColor;
        }

        finalColor = ApplyFog(finalColor, rayDir, safeSunDir, tEnd, uRenderDistance);
        vec4 clipPos = uProjection * uView * vec4(hitPos, 1.0);
        finalDepth = (clipPos.z / clipPos.w) * 0.5 + 0.5;
    }

    finalColor = ApplyPostProcess(finalColor);
    FragColor = vec4(finalColor, 1.0);
    gl_FragDepth = finalDepth;
}