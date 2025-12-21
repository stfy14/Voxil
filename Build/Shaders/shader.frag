#version 330 core
out vec4 FragColor;

in vec3 vColor;
uniform vec3 lightDir; 

void main()
{
    vec3 xTangent = dFdx(vColor); 

    vec3 ambient = vec3(0.5);
    vec3 light = normalize(lightDir);

    FragColor = vec4(vColor, 1.0);
}