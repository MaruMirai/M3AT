#version 450

// MarusFace.vert.glsl with the pawn's bones at binding 17, for MarusFaceStack.frag.glsl (FaceLayers.DrawStacked). Keep
// the two in step.

// Facial Animation's face layers on the chibi's head (FaceLayers.cs): the game's Transparent.vert.glsl plus the world
// position, which the fragment stage needs to find the head facet's direction (as IsoRimSolid.frag.glsl does), and the
// mesh normal for the smooth shade (as MarusSmooth.vert.glsl; zero-length when the patch has none), and the texture
// place's second pair (the wound's own square, Wounds.cs).
// Taken from the IsoRim Nals Experimentals Patch (IsoRimNalsFace.vert.glsl).

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec3 inNormal;   // @vertex Normal optional @default = vec4(0.0, 0.0, 0.0, 0.0)
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)
// GPU skinning of the face patch (SkinnedFigure's FaceOnGpu), as MarusSmooth.vert.glsl skins the body: up to twelve bones
// a vertex, packed one to a number as bone + weight / 2, in texture coordinates 2 and 3 and the tangent (those of the body
// vertex the face vertex was made from). Zero is no bone; a mesh without them (a wound decal, a patch posed on the CPU,
// the baked frames' faces) is drawn as it is.
layout(location = 5) in vec4 inUv2; // @vertex TexCoord2 optional @default = vec4(0.0, 0.0, 0.0, 0.0)
layout(location = 6) in vec4 inUv3; // @vertex TexCoord3 optional @default = vec4(0.0, 0.0, 0.0, 0.0)
layout(location = 7) in vec4 inTangent; // @vertex Tangent optional @default = vec4(0.0, 0.0, 0.0, 0.0)

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

// The pawn's bones: each the top three rows of its matrix, model units (FaceLayers.BlockFor, from SkinnedFigure.BlockOf's).
// Binding 17, after MarusFaceStack.frag.glsl's sixteen textures; declared there too, where the engine reads the material layout from.
layout(std430, set = 2, binding = 17) readonly buffer _MarusBones
{
    vec4 marusBones[];
};

// One packed bone of a vertex: its share of where the bone takes the rest place at and the normal (MarusSmooth.vert.glsl's).
void Influence(float packed, vec4 at, vec3 normal, inout vec3 moved, inout vec3 turned, inout float total)
{
    if (packed <= 0.0)
    {
        return;
    }
    float bone = floor(packed);
    float w = (packed - bone) * 2.0;
    int o = 3 * int(bone);
    vec4 r0 = marusBones[o], r1 = marusBones[o + 1], r2 = marusBones[o + 2];
    moved += vec3(dot(r0, at), dot(r1, at), dot(r2, at)) * w;
    turned += vec3(dot(r0.xyz, normal), dot(r1.xyz, normal), dot(r2.xyz, normal)) * w;
    total += w;
}

// Poses the vertex and its normal by its bones, as SkinnedFigure does the patch on the CPU; left as it is with none.
void Skin(inout vec3 pos, inout vec3 normal)
{
    vec4 at = vec4(pos, 1.0);
    vec3 moved = vec3(0.0), turned = vec3(0.0);
    float total = 0.0;
    Influence(inUv2.x, at, normal, moved, turned, total);
    Influence(inUv2.y, at, normal, moved, turned, total);
    Influence(inUv2.z, at, normal, moved, turned, total);
    Influence(inUv2.w, at, normal, moved, turned, total);
    Influence(inUv3.x, at, normal, moved, turned, total);
    Influence(inUv3.y, at, normal, moved, turned, total);
    Influence(inUv3.z, at, normal, moved, turned, total);
    Influence(inUv3.w, at, normal, moved, turned, total);
    Influence(inTangent.x, at, normal, moved, turned, total);
    Influence(inTangent.y, at, normal, moved, turned, total);
    Influence(inTangent.z, at, normal, moved, turned, total);
    Influence(inTangent.w, at, normal, moved, turned, total);
    if (total > 0.0)
    {
        pos = moved;
        normal = turned;
    }
}

void main()
{
    vec3 pos = inPosition;
    vec3 normal = inNormal;
    Skin(pos, normal);
    vec4 world = push.modelMatrix * vec4(pos, 1.0);
    gl_Position = uVertex.ViewProj * world;
    vColor = inColor;
    vUv = inUv0.xy;
    vWorldPos = world.xyz;
    vSmoothNormal = mat3(push.modelMatrix) * normal;
    vLocal = inUv0.zw;
}
