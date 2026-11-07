#version 450

// Facial Animation's face layers on the chibi's head (FaceLayers.cs), lit by IsoRim's sun exactly as the head under
// them is (IsoRimSolid.frag.glsl, _LightMode 1: ModelShade of the facet's direction, snapped to IsoRim's 26 groups), so
// a skin-coloured layer (the eyelid cover, blush) matches the head instead of standing out brighter. The face patch is
// the head's own triangles lifted a little, so each facet is shaded as the one under it. Taken from the IsoRim Nals
// Experimentals Patch (IsoRimNalsFace.frag.glsl).
//
// _FaceMode copies the colour maths of the game shader the layer was made for:
//   0 Transparent, 1 CutoutSkin / CutoutSkinOverride (cut out below alpha 0.1, no shadow tint: the 3D head has none),
//   2 CutoutComplex (mask r = _Color, g = _ColorTwo, cut out below 0.5), 3 CutoutSkinOverlay (times the mask),
//   4 MoteGlow (highlights: unlit, drawn additively by the MarusFaceGlow def),
//   5 the lid cover (CutoutSkinOverlay over Facial Animation's render texture of the sprite head): skin colour times the
//     lid mask (which carries the lashes), the head art's alpha. The art itself is left out: it is the sprite head's own
//     painted shading, run through CutoutSkin with its red shadow tint, which matches the sprite head under it but shows
//     as a darker, warmer mask round the eyes on the plain-coloured 3D head.
//   6 wounds (Wounds.cs): the texture as it is, cut to the wound's own square (vLocal 0 .. 1): the patch it is laid on
//     runs past it, and past it the sheet holds the next wound. A vertex alpha under 1 makes it a scar, its colour the
//     skin's: the sprite's brightness alone is kept, its dark seams a darker skin and the rest pale healed skin, so no
//     red or bruise blue is left and a scar shows on any skin.
//   7 a wound on the eye (FaceLayers.DrawEyeWounds), drawn on the face patch under the lids: the sheet's square at _WoundAt,
//     a _Color alpha under 1 making it a scar as in mode 6, cut to the eye's shape (the alpha of _MaskTex, the iris, or _MaskTwo, the white).
// _Smooth 1 (set per draw for the runtime-skinned chibi, FaceLayers.Patch.smooth): shaded by the patch's own normals,
// rounded to _ShadeBands levels, as MarusSmooth.frag.glsl shades the body under it.
// _EyeCut (set per draw): halves of the face left out, so a missing eye's eyeball and open lid go and Facial Animation's
// closed lid is drawn on that half only.
// Night and lamp light: none here. The layers write no depth, so the head's own light pass (IsoRim, queue 3099)
// multiplies them with the head.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec3 vSmoothNormal;   // the patch's normal, drawn space (squashed), not normalised
layout(location = 4) in vec2 vLocal;   // a wound's place in its own square (mode 6)

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _ColorTwo;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _FaceMode;  // @default = 0.0
    vec4 _IsoSun;     // global, no default: xyz = direction toward IsoRim's sun (or moon)
    vec4 _IsoView;    // global, no default: xyz = true-space direction toward the camera, w = Squash
    float _Smooth;    // @default = 0.0   1: shade by vSmoothNormal (the skinned chibi)
    float _ShadeBands; // @default = 0.0  as MarusSmooth.frag.glsl
    float _EyeCut;    // @default = 0.0   set per draw (FaceLayers.EyeCut): 1 leaves out the texture's left half, 2 its right
    vec4 _WoundAt;     // @default = vec4(0.0, 0.0, 0.0, 0.0)   mode 7, per draw: its middle (face texture places), its way across over its span
    vec4 _WoundCell;   // @default = vec4(0.0, 0.0, 0.0, 0.0)   mode 7, per draw: its square on the sheet, place and size
    float _IsoFlash;   // @default = 0.0   per draw (FaceLayers.Flash): how red a hit figure is now, as MarusSmooth.frag.glsl; not on wounds (modes 6, 7)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex; // @default = black   always set by FaceLayers.LitFor where it is read
layout(set = 2, binding = 3) uniform sampler2D _MaskTwo; // @default = black   mode 7 only (FaceLayers.DrawEyeWounds)

const float DEG = 0.017453292519943295;

// The scar filter (mode 6). The sheet's wounds are dark: their brightness runs from about 0.03 to 0.15.
const float SCAR_DARK = 0.03;    // the sprite's brightness at the seam's colour
const float SCAR_LIGHT = 0.14;   // and at the pale's
const float SCAR_PALE = 0.45;   // how far the healed skin goes from the skin's colour to SCAR_PALE_COLOUR
const vec3 SCAR_PALE_COLOUR = vec3(1.0, 0.86, 0.82);
const vec3 SCAR_SEAM = vec3(0.72, 0.59, 0.59);   // the seam, times the skin's colour

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

// The scar filter's colour for a sprite's colour and the skin's.
vec3 ScarColour(vec3 sprite, vec3 skin)
{
    float ridge = smoothstep(SCAR_DARK, SCAR_LIGHT, dot(sprite, vec3(0.3, 0.59, 0.11)));
    return mix(skin * SCAR_SEAM, mix(skin, SCAR_PALE_COLOUR, SCAR_PALE), ridge);
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

void main()
{
    // Derivatives first, in uniform control flow (before any discard).
    float squash = uMaterial._IsoView.w;
    vec3 p = vec3(vWorldPos.x, vWorldPos.y * squash, vWorldPos.z);
    vec3 n = cross(dFdx(p), dFdy(p));
    float len = length(n);
    n = len > 1e-20 ? n / len : vec3(0.0, 1.0, 0.0);
    if (dot(n, uMaterial._IsoView.xyz) < 0.0)
        n = -n;
    vec3 sn = vec3(vSmoothNormal.x, vSmoothNormal.y * squash, vSmoothNormal.z);
    float snLen = length(sn);
    bool smoothed = uMaterial._Smooth > 0.5 && snLen > 1e-6;
    float smoothShade = Banded(ModelShade(smoothed ? sn / snLen : n, uMaterial._IsoSun.xyz), uMaterial._ShadeBands);

    // A missing eye: Facial Animation's face is split down the middle, one eye to each half (vUv.x 0.5).
    int cut = int(uMaterial._EyeCut + 0.5);
    if (((cut & 1) != 0 && vUv.x < 0.5) || ((cut & 2) != 0 && vUv.x >= 0.5))
        discard;

    vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    vec4 t = texture(_MainTex, uv);
    vec4 tint = uMaterial._Color * vColor;
    int mode = int(uMaterial._FaceMode + 0.5);
    vec4 c;
    if (mode == 1)
    {
        if (t.a < 0.1)
            discard;
        c = t * tint;
    }
    else if (mode == 2)
    {
        vec4 m = texture(_MaskTex, uv);
        m.rg *= clamp(m.a * 255.0, 0.0, 1.0);   // the game's MaskGate
        c = t * (m.r * tint + (1.0 - m.r)) * (m.g * uMaterial._ColorTwo + (1.0 - m.g));
        if (c.a < 0.5)
            discard;
    }
    else if (mode == 3)
    {
        c = t * tint * texture(_MaskTex, uv);
    }
    else if (mode == 5)
    {
        c = vec4(tint.rgb, tint.a * t.a) * texture(_MaskTex, uv);
    }
    else if (mode == 6)
    {
        if (vLocal.x < 0.0 || vLocal.x > 1.0 || vLocal.y < 0.0 || vLocal.y > 1.0)
            discard;
        c = t * tint;
        if (vColor.a < 0.99)
            c.rgb = ScarColour(t.rgb, vColor.rgb);
    }
    else if (mode == 7)
    {
        vec2 from = vUv - uMaterial._WoundAt.xy;
        vec2 local = vec2(dot(from, uMaterial._WoundAt.zw), dot(from, vec2(-uMaterial._WoundAt.w, uMaterial._WoundAt.z))) + 0.5;
        if (local.x < 0.0 || local.x > 1.0 || local.y < 0.0 || local.y > 1.0)
            discard;
        vec4 sprite = texture(_MainTex, uMaterial._WoundCell.xy + local * uMaterial._WoundCell.zw);
        float onEye = smoothstep(0.3, 0.7, max(texture(_MaskTex, vUv).a, texture(_MaskTwo, vUv).a));
        c = sprite * tint;
        if (tint.a < 0.99)
            c.rgb = ScarColour(sprite.rgb, tint.rgb);
        c.a *= onEye;
    }
    else if (mode == 4)
    {
        outColor = clamp(t * tint, 0.0, 1.0);
        return;
    }
    else
    {
        c = t * tint;
    }

    // The game's damage flash (HitFlash), as the head under it; the game does not flash its wound overlays.
    if (mode != 6 && mode != 7)
        c.gb *= 1.0 - uMaterial._IsoFlash;
    if (squash > 0.0)
        c.rgb *= smoothed ? smoothShade : ModelShade(BinNormal(n), uMaterial._IsoSun.xyz);
    outColor = c;
}
