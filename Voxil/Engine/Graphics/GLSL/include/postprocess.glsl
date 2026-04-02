float Dither(vec2 screenPos) {
    // Interleaved Gradient Noise - простой и быстрый псевдослучайный шум на основе координат пикселя
    return fract(52.9829189 * fract(dot(screenPos, vec2(0.06711056, 0.00583715))));
}

vec3 ApplyPostProcess(vec3 color, vec2 screenPos) {
    // Tone mapping (Reinhard-ish + Custom curve)
    color = color / (color + 1.0);

    // Saturation boost
    color = mix(vec3(dot(color, vec3(0.2126, 0.7152, 0.0722))), color, 1.5);

    // Gamma correction
    color = pow(color, vec3(0.4545));

    // Contrast
    color = (color - 0.5) * 1.05 + 0.5;

    // ---> ДОБАВИТЬ ЭТИ 3 СТРОКИ <---
    // DITHERING: Добавляем шум, равный по силе одному 8-битному шагу.
    // Это "разбивает" резкие границы градиента на незаметную рябь.
    float dither_val = Dither(screenPos);
    color += (dither_val - 0.5) / 255.0;

    return color;
}