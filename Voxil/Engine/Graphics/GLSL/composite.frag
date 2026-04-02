#version 450 core

layout(location = 0) out vec4 FragColor;
in vec2 uv;

#include "include/common.glsl"
#include "include/tracing.glsl"
#include "include/atmosphere.glsl"
#include "include/postprocess.glsl"

uniform sampler2D uGColor;
uniform sampler2D uGData;
uniform sampler2D uShadowFull;
uniform sampler2D uPointLightFull; // Содержит и PointLights, и GI!

void main() {
    ivec2 fullCoord = ivec2(gl_FragCoord.xy);

    vec4  colorData    = texelFetch(uGColor, fullCoord, 0);
    float directFactor = texelFetch(uGData, fullCoord, 0).a;
    vec3  normal       = texelFetch(uGData, fullCoord, 0).rgb;
    vec2  shadowAoVal  = texelFetch(uShadowFull, fullCoord, 0).rg;
    vec3  pointLightVal = texelFetch(uPointLightFull, fullCoord, 0).rgb;

    vec3  albedo = colorData.rgb;
    bool isEmissive = (albedo.r < 0.0 || albedo.g < 0.0 || albedo.b < 0.0);
    albedo = abs(albedo);

    float tFinal = colorData.a;
    float shadow = shadowAoVal.r;
    float ao     = shadowAoVal.g;

    vec4 target = uInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    target.xyz /= target.w;
    vec3 rayDir = normalize((uInvView * vec4(target.xyz, 0.0)).xyz);

    if (tFinal > uRenderDistance + 1.0) {
        // Вот исправленная строчка! Теперь передаем и цвет, и координаты
        FragColor = vec4(ApplyPostProcess(albedo, gl_FragCoord.xy), 1.0);
        return;
    }

    vec3 hitPos = uCamPos + rayDir * tFinal;

    float sunIntensity  = clamp(uSunDir.y  * 5.0, 0.0, 1.0);
    float moonIntensity = clamp(uMoonDir.y * 5.0, 0.0, 1.0) * clamp(-uSunDir.y * 5.0, 0.0, 1.0);

    vec3 lightColor = vec3(0.0);
    float activeShadow = shadow;

    if (sunIntensity > 0.05) {
        lightColor = vec3(1.0, 0.95, 0.85); // Солнце
    } else if (moonIntensity > 0.01) {
        // Очень слабый, холодный прямой лунный свет
        lightColor = vec3(0.015, 0.03, 0.06); 
        // Делаем тени от луны практически прозрачными (максимум 15% затемнения)
        activeShadow = mix(0.85, 1.0, shadow); 
    }

    vec3 direct = lightColor * directFactor * activeShadow;
    vec3 indirect = vec3(0.0);

    #ifndef ENABLE_GI
    // Фоллбэк: ночью даем легкий синий оттенок, чтобы избежать "коричневой" земли
    float dayF = clamp(uSunDir.y * 3.0 + 0.2, 0.0, 1.0);
    vec3 nightAmb = vec3(0.04, 0.06, 0.12); // Синеватый ночной эмбиент
    vec3 dayAmb   = vec3(0.6, 0.7, 0.9) * 0.4;
    vec3 currentAmbient = mix(nightAmb, dayAmb, dayF);
    indirect = albedo * currentAmbient * max(ao, 0.15);
    #endif

    // pointLightVal содержит апсемпленный GI и PointLights из shadow.frag
    vec3 pointLightContrib = albedo * pointLightVal;

    vec3 finalColor = albedo * direct + indirect + pointLightContrib;

    if (isEmissive) {
        finalColor += albedo * 25.0;
    }

    finalColor = ApplyFog(finalColor, rayDir, uSunDir, tFinal, uRenderDistance);
    // Передаем в пост-обработку координаты пикселя gl_FragCoord.xy
    FragColor = vec4(ApplyPostProcess(finalColor, gl_FragCoord.xy), 1.0);
}