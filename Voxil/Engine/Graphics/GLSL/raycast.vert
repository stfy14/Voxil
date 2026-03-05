#version 450 core

out vec2 uv;

const vec2 positions[4] = vec2[](
vec2(-1.0, -1.0),
vec2( 1.0, -1.0),
vec2(-1.0,  1.0),
vec2( 1.0,  1.0)
);

void main() {
    vec2 pos = positions[gl_VertexID];
    gl_Position = vec4(pos, 0.0, 1.0);
    
    uv = pos * 0.5 + 0.5;
}