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
    float _MarusFaceSharp;   // global, no default (FaceLayers.SetSharpness): 1 reads the art sharper (FaceArt), 0 as it was, the sharpFaceArt mod option
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

// Then each layer's spread from faceShift[4] (FaceGaze's ShiftSlots), four a vec4.
float SpreadOf(int layer)
{
    if (faceShift.length() <= 4 + layer / 4)
        return 0.0;
    return faceShift[4 + layer / 4][layer % 4];
}

// Then each layer's tuning by the player (FaceLayers.TuneOf, FaceTuning.cs) from faceShift[6], a vec4 each: its texture
// places scaled by 1 + x and moved by yz; all zero, as they are.
vec2 TuneOf(int layer, vec2 place)
{
    if (faceShift.length() <= 6 + layer)
        return place;
    vec4 t = faceShift[6 + layer];
    return place * (1.0 + t.x) + t.yz;
}

// How much the player's tuning scales the layer's places (TuneOf's 1 + x).
float TuneScale(int layer)
{
    if (faceShift.length() <= 6 + layer)
        return 1.0;
    return 1.0 + faceShift[6 + layer].x;
}

// Another race's eyes set as far apart as a human's (FaceGaze.RaceEyeSpread): the layer's left half read further right
// and its right half further left by spread (texture widths), so its art moves out, the move easing to none over
// SpreadEase either side of the middle so the strip between the eyes is stretched rather than shown twice.
const float SpreadEase = 0.03;

float SpreadAt(float x, float spread)
{
    return spread * clamp((0.5 - x) / SpreadEase, -1.0, 1.0);
}

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

// Reading the face art (keep in step with MarusFace.frag.glsl). Facial Animation's art is 512 px for a 2D head, and
// the chibi's face spans three head heights of it, so in play a face is drawn smaller than its texture, seen slanted at
// IsoRim's camera, and in a close-up (the freecam, a still) magnified up to four times or more.
//   - Slanted, the GPU picks a blurrier mip than the art needs, so it is read ArtMipBias mips sharper (the gradients
//     scaled by 2^bias, as textureGrad takes no bias).
//   - Magnified, bilinear filtering draws its soft, stair-stepped edges several screen pixels wide. Nearly every set
//     draws its lines as a solid core with an anti-aliased edge, so from SharpFrom texels a pixel the alpha is read as a
//     cubic B-spline (smooth contours, no overshoot; four bilinear taps) and stretched about half its local peak (the
//     most of it a texel round) so the edge is about a screen pixel wide, SharpEdge of the way, at most as at SharpMost.
//     About the local peak rather than 0.5, so a thin or faint line (a mouth a texel or two thick, Kiiro's faint brows)
//     keeps its strength and shape instead of thinning to a dot. Art fainter than SharpFaint is left as it is.
//   Both only with _MarusFaceSharp (the sharpFaceArt mod option); without it the art is read as it always was.
const float ArtMipBias = -0.5;
const float SharpFrom = 1.25;   // screen pixels a texel from which edges start being sharpened
const float SharpFull = 2.0;    // and are wholly
const float SharpEdge = 0.9;
const float SharpMost = 6.0;
const float SharpFaint = 0.04;

// A cubic B-spline read of the top mip at uv (size: the texture's texels), from four bilinear taps.
vec4 BSpline(sampler2D tex, vec2 uv, vec2 size)
{
    vec2 st = uv * size - 0.5;
    vec2 i = floor(st);
    vec2 f = st - i;
    vec2 f2 = f * f;
    vec2 f3 = f2 * f;
    vec2 w0 = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
    vec2 w1 = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
    vec2 w2 = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
    vec2 w3 = f3 / 6.0;
    vec2 g0 = w0 + w1;
    vec2 g1 = w2 + w3;
    vec2 p0 = (i - 0.5 + w1 / g0) / size;   // between texels i - 1 and i (their middles at i - 0.5 and i + 0.5)
    vec2 p1 = (i + 1.5 + w3 / g1) / size;   // between i + 1 and i + 2
    return g0.y * (g0.x * textureLod(tex, vec2(p0.x, p0.y), 0.0) + g1.x * textureLod(tex, vec2(p1.x, p0.y), 0.0))
        + g1.y * (g0.x * textureLod(tex, vec2(p0.x, p1.y), 0.0) + g1.x * textureLod(tex, vec2(p1.x, p1.y), 0.0));
}

// The art at uv, dx and dy its texture places' change a screen pixel across and down.
vec4 FaceArt(sampler2D tex, vec2 uv, vec2 dx, vec2 dy)
{
    float on = clamp(uMaterial._MarusFaceSharp, 0.0, 1.0);
    float bias = exp2(ArtMipBias * on);
    vec4 t = textureGrad(tex, uv, dx * bias, dy * bias);
    vec2 size = vec2(textureSize(tex, 0));
    float texels = max(length(dx * size), length(dy * size));
    float strength = on * smoothstep(SharpFrom, SharpFull, 1.0 / max(texels, 1e-4));
    if (strength > 0.0)
    {
        float b = BSpline(tex, uv, size).a;
        vec2 o = 1.0 / size;
        float peak = max(max(t.a, b), max(max(textureLod(tex, uv + vec2(o.x, 0.0), 0.0).a, textureLod(tex, uv - vec2(o.x, 0.0), 0.0).a),
            max(textureLod(tex, uv + vec2(0.0, o.y), 0.0).a, textureLod(tex, uv - vec2(0.0, o.y), 0.0).a)));
        if (peak > SharpFaint)
        {
            float k = min(1.0 / max(texels, 1e-4), SharpMost) * SharpEdge;
            float sharp = clamp((b - 0.5 * peak) * k / peak + 0.5, 0.0, 1.0) * peak;
            t.a = mix(t.a, sharp, strength);
        }
    }
    return t;
}

// A mask read at the art's sharper mip.
vec4 FaceMask(sampler2D tex, vec2 uv, vec2 dx, vec2 dy)
{
    float bias = exp2(ArtMipBias * clamp(uMaterial._MarusFaceSharp, 0.0, 1.0));
    return textureGrad(tex, uv, dx * bias, dy * bias);
}

// One layer's colour (MarusFace.frag.glsl's modes 0, 1, 3 and 5), false where it draws nothing (cut away or discarded).
// dx, dy: the face's texture places' (vUv's) change a screen pixel across and down, taken in main in uniform control flow.
bool LayerColour(sampler2D tex, vec4 texST, sampler2D mask, vec4 maskST, vec4 colour, float code, int layer, vec2 dx, vec2 dy, out vec4 c)
{
    c = vec4(0.0);
    int packed = int(code + 0.5);
    if (packed <= 0)
        return false;
    packed -= 1;
    int mode = packed % 8;
    int cut = (packed / 8) % 4;
    bool mouth = packed >= 32;
    vec2 place = TuneOf(layer, mouth ? vUv * uMaterial._MouthMap.xy + uMaterial._MouthMap.zw : vUv);
    if (((cut & 1) != 0 && place.x < 0.5) || ((cut & 2) != 0 && place.x >= 0.5))
        return false;
    float across = place.x;
    place -= ShiftOf(layer);
    place.x += SpreadAt(across, SpreadOf(layer));
    // The layer's places are vUv's scaled (the mouth's zoom, the player's tuning, the texture's tile; the spread's
    // stretch about the middle left out), so their derivatives are vUv's scaled as much.
    vec2 scale = (mouth ? uMaterial._MouthMap.xy : vec2(1.0)) * TuneScale(layer);
    vec2 uv = place * texST.zw + texST.xy;
    vec4 t = FaceArt(tex, uv, dx * scale * texST.zw, dy * scale * texST.zw);
    vec4 tint = colour * vColor;
    if (mode == 1)
    {
        if (t.a < 0.1)
            return false;
        c = t * tint;
    }
    else if (mode == 3)
    {
        c = t * tint * FaceMask(mask, place * maskST.zw + maskST.xy, dx * scale * maskST.zw, dy * scale * maskST.zw);
    }
    else if (mode == 5)
    {
        c = vec4(tint.rgb, tint.a * t.a) * FaceMask(mask, place * maskST.zw + maskST.xy, dx * scale * maskST.zw, dy * scale * maskST.zw);
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

    vec2 uvDx = dFdx(vUv);
    vec2 uvDy = dFdy(vUv);

    // Laid one over another in order, premultiplied: what the separate draws' SrcAlpha, OneMinusSrcAlpha blending gave.
    vec3 rgb = vec3(0.0);
    float alpha = 0.0;
    vec4 c;
    if (LayerColour(_T0, uMaterial._T0_ST, _M0, uMaterial._M0_ST, uMaterial._C0, uMaterial._Q0.x, 0, uvDx, uvDy, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T1, uMaterial._T1_ST, _M1, uMaterial._M1_ST, uMaterial._C1, uMaterial._Q0.y, 1, uvDx, uvDy, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T2, uMaterial._T2_ST, _M2, uMaterial._M2_ST, uMaterial._C2, uMaterial._Q0.z, 2, uvDx, uvDy, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T3, uMaterial._T3_ST, _M3, uMaterial._M3_ST, uMaterial._C3, uMaterial._Q0.w, 3, uvDx, uvDy, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T4, uMaterial._T4_ST, _M4, uMaterial._M4_ST, uMaterial._C4, uMaterial._Q1.x, 4, uvDx, uvDy, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T5, uMaterial._T5_ST, _M5, uMaterial._M5_ST, uMaterial._C5, uMaterial._Q1.y, 5, uvDx, uvDy, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T6, uMaterial._T6_ST, _M6, uMaterial._M6_ST, uMaterial._C6, uMaterial._Q1.z, 6, uvDx, uvDy, c))
    {
        rgb = c.rgb * c.a + rgb * (1.0 - c.a);
        alpha = c.a + alpha * (1.0 - c.a);
    }
    if (LayerColour(_T7, uMaterial._T7_ST, _M7, uMaterial._M7_ST, uMaterial._C7, uMaterial._Q1.w, 7, uvDx, uvDy, c))
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
