#version 450 core

layout (location = 0) in vec3 aPos;
layout (location = 1) in vec3 aColor;
layout (location = 2) in float aAo;

out vec3 vFragPos;
out vec3 vColor;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

void main()
{
    vec4 worldPos = model * vec4(aPos, 1.0);
    vFragPos = worldPos.xyz;
    vColor = aColor;
    gl_Position = projection * view * worldPos;
}