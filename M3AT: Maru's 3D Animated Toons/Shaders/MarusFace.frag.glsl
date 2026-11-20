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
//     a _Color alpha under 1 making it a scar as in mode 6, cut to the eye's shape (the alpha of _MaskTex: the white, or
//     for a prosthetic eye Facial Animation's iris, moved with it by the shift). With _Recolour (a prosthetic eye or a
//     clouded one on a measured iris, FaceLayers.DrawEyeWounds) the cell is not art but a recolouring
//     (make_prosthetic_decals.py's RAMPS): the iris's own art, blurred by the cell's settings, looked up by its brightness
//     in the cell's ramp, plus the cell's ring by the distance from the iris's middle, so it keeps that eye style's
//     drawing.
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
    // (Beside the other vec4s: after the run of floats below they read as nothing in the game, and no eye wound showed.)
    vec4 _WoundAt;     // @default = vec4(0.0, 0.0, 0.0, 0.0)   mode 7, per draw: its middle (face texture places), its way across over its span
    vec4 _WoundCell;   // @default = vec4(0.0, 0.0, 0.0, 0.0)   mode 7, per draw: its square on the sheet, place and size
    float _FaceMode;  // @default = 0.0
    vec4 _IsoSun;     // global, no default: xyz = direction toward IsoRim's sun (or moon)
    vec4 _IsoView;    // global, no default: xyz = true-space direction toward the camera, w = Squash
    vec4 _IsoEye;     // global, no default: xyz = the eye in drawn space, w = 1 with a perspective camera (IsoRim's figure shader contract)
    float _Smooth;    // @default = 0.0   1: shade by vSmoothNormal (the skinned chibi)
    float _ShadeBands; // @default = 0.0  as MarusSmooth.frag.glsl
    float _EyeCut;    // @default = 0.0   set per draw (FaceLayers.EyeCut): 1 leaves out the texture's left half, 2 its right
    float _IsoFlash;   // @default = 0.0   per draw (FaceLayers.Flash): how red a hit figure is now, as MarusSmooth.frag.glsl; not on wounds (modes 6, 7)
    float _Recolour;   // @default = 0.0   mode 7: 1 redraws the iris under it (_MaskTex) in its cell's colours rather than drawing the cell's art
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex; // @default = black   always set by FaceLayers.LitFor where it is read
layout(set = 2, binding = 3) uniform sampler2D _MaskTwo; // @default = black   mode 7 only (FaceLayers.DrawEyeWounds)

// The pawn's bones, read only by the vertex stage (the face patch skinned on the graphics card, MarusFace.vert.glsl);
// declared here as the engine reads the material layout from the fragment stage.
layout(std430, set = 2, binding = 6) readonly buffer _MarusBones
{
    vec4 marusBones[];
};

// Facial Animation's offset of this layer now (FaceLayers.ShiftOf, FaceGaze.cs): face texture places it is moved by, in the
// first slot's xy (the stacked shader's layout, two layers a vec4), and the player's tuning in the second (FaceTuning.cs).
// Set on every block these draws are given; none: none.
layout(std430, set = 2, binding = 7) readonly buffer _MarusFaceShift
{
    vec4 faceShift[];
};

// Another race's eyes set as far apart as a human's (FaceGaze.RaceEyeSpread): the layer's left half read further right
// and its right half further left by spread (texture widths), so its art moves out, the move easing to none over
// SpreadEase either side of the middle so the strip between the eyes is stretched rather than shown twice.
const float SpreadEase = 0.03;

float SpreadAt(float x, float spread)
{
    return spread * clamp((0.5 - x) / SpreadEase, -1.0, 1.0);
}

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

// The direction from a fragment at p (true space) toward the camera, as MarusSmooth.frag.glsl's (IsoRim's M229): toward
// the eye with a perspective camera, else the camera's own direction.
vec3 ToEye(vec3 p)
{
    vec4 eye = uMaterial._IsoEye;
    if (eye.w > 0.5)
        return vec3(eye.x, eye.y * uMaterial._IsoView.w, eye.z) - p;
    return uMaterial._IsoView.xyz;
}

// Mode 7's recolouring cells (make_prosthetic_decals.py's RAMPS; keep in step): the iris's radius over the art's cell,
// FaceGaze.cs's IrisArtShare, and the blur in iris radii at a settings red of 1.
const float IrisArtShare = 34.0 / 128.0;
const float BlurMost = 0.5;
const float OwnIris = 2.5;   // iris radii from its middle that a recolouring reaches

// A place on one of a cell's strips: across (0 .. 1, inset), up its height (0 .. 1 from its bottom), on the sheet as
// the engine turns it.
vec2 Strip(vec4 cell, float across, float up)
{
    vec2 at = cell.xy + vec2(mix(0.02, 0.98, clamp(across, 0.0, 1.0)), up) * cell.zw;
    return vec2(at.x, 1.0 - at.y);
}

void main()
{
    // Derivatives first, in uniform control flow (before any discard).
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

    // The player's tuning of the layer's mod (FaceLayers.TuneOf, FaceTuning.cs), in the second slot: its texture places
    // scaled by 1 + x and moved by yz, all zero (the wounds, ridges and flat beard, SetNoShift) leaving them as they are.
    vec2 tuned = faceShift.length() > 1 ? vUv * (1.0 + faceShift[1].x) + faceShift[1].yz : vUv;

    // A missing eye: Facial Animation's face is split down the middle, one eye to each half (0.5 across).
    int cut = int(uMaterial._EyeCut + 0.5);
    if (((cut & 1) != 0 && tuned.x < 0.5) || ((cut & 2) != 0 && tuned.x >= 0.5))
        discard;

    // Where the layer is read: moved by Facial Animation's offset (where its eyes look, its brows and mouth shift).
    vec2 place = faceShift.length() > 0 ? tuned - faceShift[0].xy : tuned;
    if (faceShift.length() > 0)
        place.x += SpreadAt(tuned.x, faceShift[0].z);
    vec2 uv = place * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
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
        vec2 from = place - uMaterial._WoundAt.xy;   // a prosthetic eye's iris moves with the iris's shift
        vec2 local = vec2(dot(from, uMaterial._WoundAt.zw), dot(from, vec2(-uMaterial._WoundAt.w, uMaterial._WoundAt.z))) + 0.5;
        // The eye's shape read where the face layers read their art (uv, by _MainTex_ST, through which the engine turns its
        // textures the right way up), not at vUv, which read the masks upside down and clear.
        vec4 shape = texture(_MaskTex, uv);
        float onEye = smoothstep(0.3, 0.7, max(shape.a, texture(_MaskTwo, uv).a));
        if (uMaterial._Recolour > 0.5)
        {
            // The cell's strips, read at their middles (a sixth, a half and five sixths up), at the top mip, as they are
            // thin; across inset a little, so the next cell doesn't bleed in. The engine turns the sheet as for the art.
            // Only round its own iris: Facial Animation's iris texture holds both eyes' (each eye's layer takes its half by
            // its mask), so cut to its alpha alone this would cover the other eye too. They are some 4.7 radii apart, and an
            // iris's soft edge reaches about 1.5.
            float radii = length(local - 0.5) / IrisArtShare;
            if (radii > OwnIris)
                discard;
            vec4 cell = uMaterial._WoundCell;
            float blur = textureLod(_MainTex, Strip(cell, 0.5, 5.0 / 6.0), 0.0).r * BlurMost;
            // The iris's radius in face texture places: _WoundAt.z is IrisArtShare over it (FaceLayers.DrawEyeWounds).
            float radius = IrisArtShare / max(uMaterial._WoundAt.z, 1e-4);
            vec2 reach = blur * radius * uMaterial._MainTex_ST.zw;
            vec4 iris = shape;
            if (blur > 0.0)
            {
                iris += texture(_MaskTex, uv + vec2(reach.x, 0.0)) + texture(_MaskTex, uv - vec2(reach.x, 0.0))
                    + texture(_MaskTex, uv + vec2(0.0, reach.y)) + texture(_MaskTex, uv - vec2(0.0, reach.y))
                    + texture(_MaskTex, uv + reach * 0.7) + texture(_MaskTex, uv - reach * 0.7)
                    + texture(_MaskTex, uv + vec2(reach.x, -reach.y) * 0.7) + texture(_MaskTex, uv + vec2(-reach.x, reach.y) * 0.7);
                iris /= 9.0;
            }
            // Brightness of the art alone, not darkened by its own soft edge (the art's colours are not premultiplied).
            float bright = clamp(dot(iris.rgb, vec3(1.0 / 3.0)), 0.0, 1.0);
            vec4 ramp = textureLod(_MainTex, Strip(cell, bright, 1.0 / 6.0), 0.0);
            vec4 ring = textureLod(_MainTex, Strip(cell, radii * 0.5, 0.5), 0.0);
            c = vec4(ramp.rgb + ring.rgb * ring.a, ramp.a) * tint;
        }
        else
        {
            if (local.x < 0.0 || local.x > 1.0 || local.y < 0.0 || local.y > 1.0)
                discard;
            // The engine turns a texture upside down against a place worked out here (a mesh's UVs are turned with it), so
            // the square's place on the sheet is turned back; without it the eye took the sheet's top row, a stab wound.
            vec2 at = uMaterial._WoundCell.xy + local * uMaterial._WoundCell.zw;
            vec4 sprite = texture(_MainTex, vec2(at.x, 1.0 - at.y));
            c = sprite * tint;
            if (tint.a < 0.99)
                c.rgb = ScarColour(sprite.rgb, tint.rgb);
        }
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
