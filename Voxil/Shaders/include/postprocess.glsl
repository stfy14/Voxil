vec3 ApplyPostProcess(vec3 color) {
    // Tone mapping (Reinhard-ish + Custom curve)
    color = color / (color + 1.0);

    // Saturation boost
    color = mix(vec3(dot(color, vec3(0.2126, 0.7152, 0.0722))), color, 1.5);

    // Gamma correction
    color = pow(color, vec3(0.4545));

    // Contrast
    color = (color - 0.5) * 1.05 + 0.5;

    return color;
}