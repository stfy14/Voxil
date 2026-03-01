#version 450 core
out vec4 FragColor;
in vec2 uv;

uniform sampler2D uCurrentColorTexture;
uniform sampler2D uCurrentDepthTexture;
uniform sampler2D uHistoryTexture;
uniform mat4 uInvViewProj;
uniform mat4 uPrevViewProj;

vec3 SafeColor(vec3 c) {
    bvec3 bad = bvec3(isnan(c.x)||isinf(c.x), isnan(c.y)||isinf(c.y), isnan(c.z)||isinf(c.z));
    return clamp(mix(c, vec3(0.0), bad), vec3(0.0), vec3(1.0));
}

void main() {
    vec2 texSize   = vec2(textureSize(uCurrentColorTexture, 0));
    vec2 pixelSize = 1.0 / texSize;

    float depth = texture(uCurrentDepthTexture, uv).r;

    if (depth >= 0.9999) {
        FragColor = vec4(SafeColor(texture(uCurrentColorTexture, uv).rgb), 1.0);
        return;
    }

    vec3 color = SafeColor(texture(uCurrentColorTexture, uv).rgb);

    // --- СТАТИСТИКА СОСЕДЕЙ ---
    vec3 m1 = vec3(0.0);
    vec3 m2 = vec3(0.0);

    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            vec2 sUV = clamp(uv + vec2(x, y) * pixelSize, vec2(0.0), vec2(1.0));
            vec3 s = SafeColor(texture(uCurrentColorTexture, sUV).rgb);
            m1 += s;
            m2 += s * s;
        }
    }

    vec3 mu    = m1 / 9.0;
    vec3 sigma = sqrt(max(m2 / 9.0 - mu * mu, vec3(0.0)));

    // Насколько пиксель находится на границе геометрии (0=однородно, 1=край)
    float sigmaLen = length(sigma);
    float isEdge   = clamp(sigmaLen * 8.0, 0.0, 1.0);

    // На краях — жёсткий clamp (нет ghosting)
    // На плоскостях — мягкий clamp (хорошее сглаживание)
    float gamma = mix(1.0, 0.5, isEdge);
    vec3 minC   = mu - gamma * sigma;
    vec3 maxC   = mu + gamma * sigma;

    // --- РЕПРОЕКЦИЯ ---
    vec4 clip = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 wPos = uInvViewProj * clip;
    wPos /= wPos.w;

    vec4 prevClip = uPrevViewProj * wPos;
    vec2 prevUV   = (prevClip.xy / prevClip.w) * 0.5 + 0.5;

    if (any(lessThan(prevUV, vec2(0.0))) || any(greaterThan(prevUV, vec2(1.0)))) {
        FragColor = vec4(color, 1.0);
        return;
    }

    // --- ИСТОРИЯ ---
    vec3 history = SafeColor(texture(uHistoryTexture, prevUV).rgb);
    history = clamp(history, minC, maxC);

    // --- АДАПТИВНЫЙ BLEND ---
    float motionPx     = length((uv - prevUV) * texSize);
    float motionBlend  = clamp(1.0 - exp(-motionPx * 1.5), 0.08, 0.97);

    // На краях при движении — дополнительно повышаем blend.
    // Это предотвращает "прилипание" истории стены к пикселям неба.
    // При стоянии (motionPx≈0) этот буст не срабатывает — сглаживание сохраняется.
    float realMotion      = max(0.0, motionPx - 0.5);
    float edgeMotionBoost = isEdge * clamp(realMotion * 3.0, 0.0, 0.5);
    float blendFactor     = mix(motionBlend, 0.5, edgeMotionBoost);

    vec3 result = mix(history, color, blendFactor);

    FragColor = vec4(SafeColor(result), 1.0);
}
