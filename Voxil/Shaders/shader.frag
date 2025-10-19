#version 330 core

out vec4 FragColor;

in vec3 VertexColor; 
in float AoValue;

void main()
{
    FragColor = vec4(VertexColor * AoValue, 1.0);
}