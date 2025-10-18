#version 330 core
in vec3 vertexColor;
out vec4 FragColor;

void main()
{
    vec3 color = vertexColor;
    float ambient = 0.3;
    float diffuse = 0.7;
    vec3 finalColor = color * (ambient + diffuse);
    FragColor = vec4(finalColor, 1.0);
}