#version 450

// The screen-space outline's edge pass (ScreenOutlines.cs, def MarusEdge): a box round a figure or a weapon it holds, only
// so its pixels are the ones looked at. vBoxW is the clip w of the box's middle, which MarusEdge.frag.glsl takes the
// figure's size on screen from (how many pixels a cell is), to know how far round each pixel to look.

layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(location = 0) flat out float vBoxW;

void main()
{
    vBoxW = (uVertex.ViewProj * (push.modelMatrix * vec4(0.0, 0.0, 0.0, 1.0))).w;
    gl_Position = uVertex.ViewProj * (push.modelMatrix * vec4(inPosition, 1.0));
}
