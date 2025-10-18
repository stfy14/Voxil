#version 330 core
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aColor;
layout (location = 2) in float aOcclusion; 

out vec3 vColor;
out float vOcclusion; 

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

void main()
{
    gl_Position = projection * view * model * vec4(aPosition, 1.0);
    vColor = aColor;
    vOcclusion = aOcclusion; 
}