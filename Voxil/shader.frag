#version 330 core
out vec4 FragColor;
in vec3 vColor;
in float vOcclusion;

void main()
{
    float smoothAO = sqrt(vOcclusion);
    
    vec3 finalColor = vColor * smoothAO;
    
    FragColor = vec4(finalColor, 1.0);
}