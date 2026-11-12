#version 450

// Facial Animation's face layers on the chibi's head, up to eight in one draw (FaceLayers.DrawStacked): each layer as
// MarusFace.frag.glsl draws it on its own (its _FaceMode colour maths 0, 1, 3 or 5, its missing-eye cut, the mouth's
// zoomed texture places), laid over the ones before it in order as the separate draws' alpha blending laid them, then the
// hit flash and the sun's shade once over the lot, as they are the same for every layer. Drawn one at a time the layers
// took about ten draws a face; the face changes on few frames (a blink, a new mouth), so each mix of layers gets its
// material once and keeps it. Highlights (mode 4, added on after the light pass) and wounds on the eye are drawn as before.
//
// The engine takes at most 16 textures and 512 bytes of uniforms a material: layer i reads _Ti and its mask _Mi, each
// through its _ST (filled in by the engine: Facial Animation's textures are tiles of an atlas, which read without it put
// the eyes on the forehead), its colour _Ci, and its mode, eye cut and whether it is on the mouth's texture places packed
// into one number of _Q0/_Q1. CutoutComplex layers (a second colour) are drawn on their own (FaceLayers.Solo). The
// textures take bindings 1 .. 16, the pawn's bones 17 (MarusFaceStack.vert.glsl) and the layers' shifts 18.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec3 vSmoothNormal;   // the patch's normal, drawn space (squashed), not normalised
layout(location = 4) in vec2 vLocal;   // a wound's place in its own square (mode 6)

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _IsoSun;     // global, no default: xyz = direction toward IsoRim's sun (or moon)
    vec4 _IsoView;    // global, no default: xyz = true-space direction toward the camera, w = Squash
    vec4 _IsoEye;     // global, no default: xyz = the eye in drawn space, w = 1 with a perspective camera (IsoRim's figure shader contract)
    vec4 _MouthMap;   // @default = vec4(1.0, 1.0, 0.0, 0.0)   the mouth's texture places from the face's: xy scale, zw offset (FaceLayers.Zoomed)
    vec4 _Q0;         // @default = vec4(0.0, 0.0, 0.0, 0.0)   layers 0 .. 3: each 0 (not in use) or 1 + mode + 8 cut + 32 on the mouth's places
    vec4 _Q1;         // @default = vec4(0.0, 0.0, 0.0, 0.0)   layers 4 .. 7
    vec4 _C0;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _T0_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)   its texture's place (the engine's: a tile of an atlas)
    vec4 _M0_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _C1;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _T1_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)   its texture's place (the engine's: a tile of an atlas)
    vec4 _M1_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _C2;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _T2_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)   its texture's place (the engine's: a tile of an atlas)
    vec4 _M2_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _C3;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _T3_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)   its texture's place (the engine's: a tile of an atlas)
    vec4 _M3_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _C4;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _T4_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)   its texture's place (the engine's: a tile of an atlas)
    vec4 _M4_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _C5;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _T5_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)   its texture's place (the engine's: a tile of an atlas)
    vec4 _M5_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _C6;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _T6_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)   its texture's place (the engine's: a tile of an atlas)
    vec4 _M6_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _C7;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _T7_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)   its texture's place (the engine's: a tile of an atlas)
    vec4 _M7_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _Smooth;    // @default = 0.0   1: shade by vSmoothNormal (the skinned chibi)
    float _ShadeBands; // @default = 0.0  as MarusSmooth.frag.glsl
    float _IsoFlash;   // @default = 0.0   per draw (FaceLayers.Flash): how red a hit figure is now
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _T0; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _M0; // @default = white
layout(set = 2, binding = 3) uniform sampler2D _T1; // @default = black
layout(set = 2, binding = 4) uniform sampler2D _M1; // @default = white
layout(set = 2, binding = 5) uniform sampler2D _T2; // @default = black
layout(set = 2, binding = 6) uniform sampler2D _M2; // @default = white
layout(set = 2, binding = 7) uniform sampler2D _T3; // @default = black
layout(set = 2, binding = 8) uniform sampler2D _M3; // @default = white
layout(set = 2, binding = 9) uniform sampler2D _T4; // @default = black
layout(set = 2, binding = 10) uniform sampler2D _M4; // @default = white
layout(set = 2, binding = 11) uniform sampler2D _T5; // @default = black
layout(set = 2, binding = 12) uniform sampler2D _M5; // @default = white
layout(set = 2, binding = 13) uniform sampler2D _T6; // @default = black
layout(set = 2, binding = 14) uniform sampler2D _M6; // @default = white
layout(set = 2, binding = 15) uniform sampler2D _T7; // @default = black
layout(set = 2, binding = 16) uniform sampler2D _M7; // @default = white

// The pawn's bones, read only by the vertex stage (MarusFaceStack.vert.glsl); declared here as the engine reads the material
// layout from the fragment stage.
layout(std430, set = 2, binding = 17) readonly buffer _MarusBones
{
    vec4 marusBones[];
};

// Each layer's Facial Animation offset now (FaceLayers.SetStackShifts, FaceGaze.cs): face texture places it is moved by,
// layer i in faceShift[i / 2], xy for even i and zw for odd. In a buffer, as it changes every frame and the materials
// are kept for each mix of layers, and the uniforms are full.
layout(std430, set = 2, binding = 18) readonly buffer _MarusFaceShift
{
    vec4 faceShift[];
};

vec2 ShiftOf(int layer)
{
    if (faceShift.length() <= layer / 2)
        return vec2(0.0);
    vec4 two = faceShift[layer / 2];
    return (layer & 1) == 0 ? two.xy : two.zw;
}

const float DEG = 0.017453292519943295;
vec3 Dir(float azimuthDeg, float elevationDeg)
{
    float az = azimuthDeg * DEG;
    float el = elevationDeg * DEG;
    return vec3(sin(az) * cos(el), sin(el), cos(az) * cos(el));
}

// IsoRimSolid.frag.glsl BinNormal (keep in step with IsoRim).
vec3 BinNormal(vec3 n)
{
    if (n.y >= 0.90)
        return vec3(0.0, 1.0, 0.0);
    if (n.y <= -0.485)
        return vec3(0.0, -1.0, 0.0);
    float elevation = n.y < 0.235 ? 0.0 : (n.y < 0.635 ? 35.0 : 62.0);
    float az = atan(n.x, n.z) / DEG;
    az = az - 360.0 * floor(az / 360.0);
    float sector = mod(floor((az + 1.5) / 45.0 + 0.5), 8.0);
    return Dir(sector * 45.0, elevation);
}

// IsoRimSolid.frag.glsl ModelShade: 1 on top by day, 0.5 facing away from the sun.
float ModelShade(vec3 n, vec3 L)
{
    float d = max(0.0, dot(n, L));
    return 0.5 + 0.5 * clamp(d / max(0.2, L.y), 0.0, 1.0);
}

// MarusSmooth.frag.glsl Banded (keep in step): the shade rounded to bands levels, called before any discard.
float Banded(float shade, float bands)
{
    if (bands < 1.5)
        return shade;
    float x = (shade - 0.5) * 2.0 * (bands - 1.0);
    float w = max(fwidth(x) * 0.5, 1e-4);
    float i = floor(x);
    return 0.5 + 0.5 * min(1.0, (i + smoothstep(0.5 - w, 0.5 + w, x - i)) / (bands - 1.0));
}

// The direction from a fragment at p (true space) toward the camera, as MarusSmooth.frag.glsl's (IsoRim's M229): toward
// the eye with a perspective camera, else the camera's own direction.
vec3 ToEye(vec3 p)
{
    vec4 eye = uMaterial._IsoEye;
    if (eye.w > 0.5)
        return vec3(eye.x, eye.y * uMaterial._IsoView.w, eye.z) - p;
    return uMaterial._IsoView.xyz;
}

// One layer's colour (MarusFace.frag.glsl's modes 0, 1, 3 and 5), false where it draws nothing (cut away or discarded).
bool LayerColour(sampler2D tex, vec4 texST, sampler2D mask, vec4 maskST, vec4 colour, float code, int layer, out vec4 c)
{
    c = vec4(0.0);
    int packed = int(code + 0.5);
    if (packed <= 0)
        return false;
    packed -= 1;
    int mode = packed % 8;
    int cut = (packed / 8) % 4;
    bool mouth = packed >= 32;
    vec2 place = mouth ? vUv * uMaterial._MouthMap.xy + uMaterial._MouthMap.zw : vUv;
    if (((cut & 1) != 0 && place.x < 0.5) || ((cut & 2) != 0 && place.x >= 0.5))
        return false;
    place -= ShiftOf(layer);
    vec4 t = texture(tex, place * texST.zw + texST.xy);
    vec4 tint = colour * vColor;
    if (mode == 1)
    {
        if (t.a < 0.1)
            return false;
        c = t * tint;
    }
    else if (mode == 3)
    {
        c = t * tint * texture(mask, place * maskST.zw + maskST.xy);
    }
    else if (mode == 5)
    {
        c = vec4(tint.rgb, tint.a * t.a) * texture(mask, place * maskST.zw + maskST.xy);
    }
    else
    {
        c = t * tint;
    }
    c = clamp(c, 0.0, 1.0);
    return true;
}

void main()
{
    // Derivatives first, in uniform control flow (before any discard), as MarusFace.frag.glsl.
    float squash = uMaterial._IsoView.w;
    vec3 p = vec3(vWorldPos.x, vWorldPos.y * squash, vWorldPos.z);
    vec3 n = cross(dFdx(p), dFdy(p));
    float len = length(n);
    n = len > 1e-20 ? n / len : vec3(0.0, 1.0, 0.0);
    if (dot(n, ToEye(p)) < 0.0)
        n = -n;
    vec3 sn = vec3(vSmoothNormal.x, vSmoothNormal.y * squash, vSmoothNormal.z);
    float snLen = length(sn);
    bool smoothed = uMaterial._Smooth > 0.5 && snLen > 1e-6;
    float smoothShade = Banded(ModelShade(smoothed ? sn / snLen : n, uMaterial._IsoSun.xyz), uMaterial._ShadeBands);

    // Laid one over another in order, premultiplied: what the separate draws' SrcAlpha, OneMinusSrcAlpha blending gave.
    vec3 rgb = vec3(0.0);
    float alpha = 0.0;
    vec4 c;
    if (LayerColour(_T0, uMaterial._T0_ST, _M0, uMaterial._M0_ST, uMaterial._C0, uMaterial._Q0.x, 0, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T1, uMaterial._T1_ST, _M1, uMaterial._M1_ST, uMaterial._C1, uMaterial._Q0.y, 1, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T2, uMaterial._T2_ST, _M2, uMaterial._M2_ST, uMaterial._C2, uMaterial._Q0.z, 2, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T3, uMaterial._T3_ST, _M3, uMaterial._M3_ST, uMaterial._C3, uMaterial._Q0.w, 3, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T4, uMaterial._T4_ST, _M4, uMaterial._M4_ST, uMaterial._C4, uMaterial._Q1.x, 4, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T5, uMaterial._T5_ST, _M5, uMaterial._M5_ST, uMaterial._C5, uMaterial._Q1.y, 5, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T6, uMaterial._T6_ST, _M6, uMaterial._M6_ST, uMaterial._C6, uMaterial._Q1.z, 6, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T7, uMaterial._T7_ST, _M7, uMaterial._M7_ST, uMaterial._C7, uMaterial._Q1.w, 7, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (alpha <= 0.0)
        discard;
    vec3 colour = rgb / alpha;
    colour.gb *= 1.0 - uMaterial._IsoFlash;   // the game's damage flash (HitFlash), as the head under it
    if (squash > 0.0)
        colour *= smoothed ? smoothShade : ModelShade(BinNormal(n), uMaterial._IsoSun.xyz);
    outColor = vec4(colour, alpha);
}
