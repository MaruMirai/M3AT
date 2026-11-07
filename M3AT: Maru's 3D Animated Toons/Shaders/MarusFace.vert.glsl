#version 450

// Facial Animation's face layers on the chibi's head (FaceLayers.cs): the game's Transparent.vert.glsl plus the world
// position, which the fragment stage needs to find the head facet's direction (as IsoRimSolid.frag.glsl does), and the
// mesh normal for the smooth shade (as MarusSmooth.vert.glsl; zero-length when the patch has none), and the texture
// place's second pair (the wound's own square, Wounds.cs).
// Taken from the IsoRim Nals Experimentals Patch (IsoRimNalsFace.vert.glsl).

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 0.0, 0.0, 0.0)
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec3 vSmoothNormal;   // drawn space (squashed); not normalised
layout(location = 4) out vec2 vLocal;   // a wound's place in its own square, 0 .. 1 (zero for the face layers)

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;
    vColor = inColor;
    vUv = inUv0.xy;
    vWorldPos = world.xyz;
    vSmoothNormal = mat3(push.modelMatrix) * inNormal;
    vLocal = inUv0.zw;
}
