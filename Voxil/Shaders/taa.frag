#version 450 core
out vec4 FragColor;
in vec2 uv;

uniform sampler2D uCurrentColorTexture; // Результат рейкастинга этого кадра
uniform sampler2D uCurrentDepthTexture; // Глубина этого кадра
uniform sampler2D uHistoryTexture;      // Сглаженный результат прошлого кадра

uniform mat4 uInvViewProj;              // Матрица для репроекции (из экрана в мир)
uniform mat4 uPrevViewProj;             // Матрица проекции прошлого кадра

// YCoCg цветовое пространство - лучше для смешивания, чем RGB
vec3 RGBtoYCoCg(vec3 c) {
    return vec3(
        dot(c, vec3(0.25, 0.5, 0.25)),
        dot(c, vec3(0.5, 0.0, -0.5)),
        dot(c, vec3(-0.25, 0.5, -0.25))
    );
}

vec3 YCoCgtoRGB(vec3 c) {
    return vec3(
        c.x + c.y - c.z,
        c.x + c.z,
        c.x - c.y - c.z
    );
}

void main() {
    vec2 texSize = vec2(textureSize(uCurrentColorTexture, 0));
    vec2 pixelSize = 1.0 / texSize;

    // 1. Читаем текущий ("дрожащий") цвет и глубину
    vec3 color = texture(uCurrentColorTexture, uv).rgb;
    float depth = texture(uCurrentDepthTexture, uv).r;
    
    // Если глубина максимальная (небо), не используем историю
    if (depth >= 0.99999) {
        FragColor = vec4(color, 1.0);
        return;
    }

    // 2. Репроекция: вычисляем, где этот пиксель был в прошлом кадре
    vec4 clipPos = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 worldPos = uInvViewProj * clipPos;
    worldPos /= worldPos.w;

    vec4 prevClip = uPrevViewProj * worldPos;
    vec2 prevUV = (prevClip.xy / prevClip.w) * 0.5 + 0.5;

    // 3. Если пиксель ушел за экран, не используем историю
    if (any(lessThan(prevUV, vec2(0.0))) || any(greaterThan(prevUV, vec2(1.0)))) {
        FragColor = vec4(color, 1.0);
        return;
    }

    // 4. Читаем цвет из истории по вычисленным старым координатам
    vec3 history = texture(uHistoryTexture, prevUV).rgb;

    // 5. Neighborhood Clamping (борьба со шлейфами)
    // Ограничиваем цвет из истории диапазоном цветов соседей текущего пикселя
    vec3 minColor = vec3(100.0);
    vec3 maxColor = vec3(-100.0);
    for(int x = -1; x <= 1; ++x) {
        for(int y = -1; y <= 1; ++y) {
            vec3 c = RGBtoYCoCg(texture(uCurrentColorTexture, uv + vec2(x, y) * pixelSize).rgb);
            minColor = min(minColor, c);
            maxColor = max(maxColor, c);
        }
    }
    vec3 historyYCoCg = RGBtoYCoCg(history);
    historyYCoCg = clamp(historyYCoCg, minColor, maxColor);
    history = YCoCgtoRGB(historyYCoCg);

    // 6. Смешивание: 90% истории + 10% нового кадра
    vec3 result = mix(history, color, 0.1);
    
    FragColor = vec4(result, 1.0);
}