#version 450

// The screen-space outline (ScreenOutlines.cs, def MarusEdge): drawn over a box round each figure (and each weapon it
// holds) after its parts have written their codes into the picture's alpha (defs MarusOutlineId*, queue 2455). It reads
// the picture as it is then (scene grab MarusEdge) and the scene depth IsoRim copies at its ambient occlusion's grab
// (_IsoSceneDepth, reverse-Z: 1 near, 0 nothing drawn; copied at 2450, and nothing between writes depth).
//
// A part's code: 2 + 2 * the figure's slot this frame (0..63) + 1 for a weapon, over 255; anything else (alpha 1, as the
// picture is everywhere else) is no part. A pixel is outline where, within the line's width round it, there is a pixel of
// another part (another figure, or the weapon over the body, or the body over the weapon) nearer the camera than this
// pixel: so the line goes round the outside of each part, open meshes and thin ones too, with no shell to push out, and
// over the part's own faces only with the self-outline on (_EdgeTune.z), where one place of it is that much nearer than
// the one beside it (an arm across the chest), as IsoRim's outlines go over its figures. Its width is the part's kind's (cells, as many pixels as a cell is at that part's
// depth), inside one figure the thinner of the two kinds', and at least one pixel. Its colour is those nearer pixels' mean
// colour times the line's shade, as the shell outline's is the part's colour times it; off the figure its depth theirs, so
// what is drawn later behind the figure (a plant card, a sprite) stays behind its line, and over a part that part's own,
// so the face layers still go over it. Every other pixel is written back as it was
// with alpha 1 and its own depth; the pass also clears stencil bit 64, which the part pass set.

layout(location = 0) flat in float vBoxW;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _EdgeProj;   // @default = vec4(1.0, 1.0, 0.01, 0.5)   the camera's projection: m00, m11, m22, m23
    vec4 _EdgeWidth;  // @default = vec4(0.03, 0.015, 1.0, 0.3)   cells: the figure's line, a weapon's; perspective (1); the line's shade
    vec4 _EdgeShape;  // @default = vec4(8.0, 2.0, 10.0, 1.0)   the quick look's points a ring and rings; the full search's most rings; 1: the quick look counts only nearer parts
    vec4 _EdgeTune;   // @default = vec4(10.0, 0.01, 0.0, 0.0)   the most pixels looked round; cells nearer a part must be to draw over another; cells nearer a part must be to draw over itself (0: never, the silhouette); 1: no quick look first
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabMarusEdge;
layout(set = 2, binding = 2) uniform sampler2D _IsoSceneDepth;

ivec2 gSize;

// The part a pixel's alpha names: 0 none, else its code (2..129).
int PartOf(float a)
{
    int v = int(round(a * 255.0));
    return v >= 2 && v < 130 ? v : 0;
}

bool IsWeapon(int code)
{
    return (code & 1) == 1;
}

// Camera-space z (cells; the camera looks down -z, so nearer is larger), as IsoRim's ambient occlusion works it out.
float ViewZ(float d)
{
    if (d <= 0.0)
    {
        return -1e6;
    }
    if (u._EdgeWidth.z > 0.5)
    {
        return -u._EdgeProj.w / (d + u._EdgeProj.z);
    }
    return (d - u._EdgeProj.w) / u._EdgeProj.z;
}

// Pixels a cell is across, at camera-space z (perspective) or anywhere (orthographic).
float PixelsPerCell(float z)
{
    float across = 0.5 * float(gSize.y) * abs(u._EdgeProj.y);
    return u._EdgeWidth.z > 0.5 ? across / max(-z, 0.05) : across;
}

float WidthOf(int code)
{
    return IsWeapon(code) ? u._EdgeWidth.y : u._EdgeWidth.x;
}

void main()
{
    gSize = textureSize(_GrabMarusEdge, 0);
    ivec2 p = clamp(ivec2(gl_FragCoord.xy), ivec2(0), gSize - 1);
    vec4 here = texelFetch(_GrabMarusEdge, p, 0);
    float depthHere = texelFetch(_IsoSceneDepth, p, 0).r;
    int partHere = PartOf(here.a);
    float zHere = ViewZ(depthHere);

    // How far round to look: the wider kind's width at the box's middle, with room for parts nearer than that.
    float boxPixels = u._EdgeWidth.z > 0.5 ? 0.5 * float(gSize.y) * abs(u._EdgeProj.y) / max(vBoxW, 0.05)
        : 0.5 * float(gSize.y) * abs(u._EdgeProj.y);
    // The most is _EdgeTune.x pixels at 1080 rows, as many more as the picture is taller. Past Rings rings they are
    // spaced out (and the directions round each with them), so the lines' edges step by that much on a large screen
    // close up, but a pixel never looks at more than about Rings * 32 others.
    int Rings = max(1, int(u._EdgeShape.z + 0.5));
    float most = u._EdgeTune.x * max(1.0, float(gSize.y) / 1080.0);
    int reach = int(clamp(ceil(max(u._EdgeWidth.x, u._EdgeWidth.y) * boxPixels * 1.25), 1.0, most));
    int step = max(1, (reach + Rings - 1) / Rings);

    // Most pixels of a box are well inside one part or well clear of every part, and the full search below (up to
    // Rings * 32 pixels looked at) finds nothing there; close up that was most of the screen and nearly doubled the
    // frame. So first a few pixels on rings out to the reach (_EdgeShape: two of eight, turned against each other): only if
    // one of them is another part (or none, beside a part), or with the self-outline on the same part at a depth that far
    // off, is the full search made. A part small and thin enough to fall between them all (the very tip of a barrel)
    // can lose a little of its line. _EdgeTune.w 1 makes the full search everywhere (the autotest's comparison).
    bool look = u._EdgeTune.w > 0.5;
    if (!look)
    {
        int ProbeDirs = max(4, int(u._EdgeShape.x + 0.5));
        bool nearerOnly = u._EdgeShape.w > 0.5;
        int probeRings = max(1, int(u._EdgeShape.y + 0.5));
        for (int k = 0; k < probeRings && !look; k++)
        {
            // from the reach in to a quarter of it
            float radius = max(1.0, float(reach) * (probeRings == 1 ? 1.0 : 1.0 - 0.75 * float(k) / float(probeRings - 1)));
            for (int i = 0; i < ProbeDirs && !look; i++)
            {
                float a = 6.2831853 * (float(i) + float(k) / float(probeRings)) / float(ProbeDirs);
                ivec2 q = clamp(p + ivec2(round(vec2(cos(a), sin(a)) * radius)), ivec2(0), gSize - 1);
                int part = PartOf(texelFetch(_GrabMarusEdge, q, 0).a);
                if (nearerOnly)
                {
                    // Only what can put a line here: a part nearer than this pixel (never the ground or sky, so the band
                    // just inside a part's own edge, where the line is outside, has no full search), or with the
                    // self-outline the same part nearer by half its step.
                    if (part != 0 && (part != partHere || u._EdgeTune.z > 0.0))
                    {
                        float z = ViewZ(texelFetch(_IsoSceneDepth, q, 0).r);
                        look = part != partHere ? z > zHere : z - zHere >= 0.5 * u._EdgeTune.z;
                    }
                }
                else if (part != partHere)
                {
                    look = true;
                }
                else if (part != 0 && u._EdgeTune.z > 0.0
                    && abs(ViewZ(texelFetch(_IsoSceneDepth, q, 0).r) - zHere) >= 0.5 * u._EdgeTune.z)
                {
                    look = true;   // a step a self-outline may be drawn for
                }
            }
        }
    }

    vec3 sum = vec3(0.0);
    float count = 0.0;
    float nearest = -1e9, nearestDepth = depthHere;
    for (int r = 1; look && r <= reach && count == 0.0; r = (r == 1 && step > 1 ? step : r + step))
    {
        int dirs = clamp(int(6.2831853 * float(r) / float(step) + 0.5), 8, 32);
        for (int i = 0; i < dirs; i++)
        {
            float a = 6.2831853 * (float(i) + 0.5 * float((r / step) & 1)) / float(dirs);
            ivec2 q = p + ivec2(round(vec2(cos(a), sin(a)) * float(r)));
            if (q.x < 0 || q.y < 0 || q.x >= gSize.x || q.y >= gSize.y)
            {
                continue;
            }
            vec4 there = texelFetch(_GrabMarusEdge, q, 0);
            int part = PartOf(there.a);
            bool self = part == partHere;
            if (part == 0 || (self && u._EdgeTune.z <= 0.0))
            {
                continue;
            }
            float d = texelFetch(_IsoSceneDepth, q, 0).r;
            float z = ViewZ(d);
            if (z < zHere + (self ? u._EdgeTune.z : u._EdgeTune.y))
            {
                continue;   // not in front of this pixel (a part over itself by the self-outline's step): its own line is drawn on the other side
            }
            float width = WidthOf(part);
            if (partHere != 0 && (partHere >> 1) == (part >> 1))
            {
                width = min(width, WidthOf(partHere));   // inside one figure: the thinner kind's
            }
            if (float(r) > max(width * PixelsPerCell(z), 1.0) + 0.5 * float(step - 1) + 0.25)
            {
                continue;
            }
            sum += there.rgb;
            count += 1.0;
            if (z > nearest)
            {
                nearest = z;
                nearestDepth = d;
            }
        }
    }
    if (count > 0.0)
    {
        outColor = vec4(sum / count * u._EdgeWidth.w, 1.0);
        // Over a part (a weapon's line over the body, a self-outline) the pixel keeps its own depth, so what is drawn on
        // that part later (the face layers, wounds) still goes over the line; outside the figure the line takes the depth
        // of the part it outlines, so what is drawn later behind it stays behind.
        gl_FragDepth = partHere != 0 ? depthHere : nearestDepth;
    }
    else
    {
        outColor = vec4(here.rgb, 1.0);
        gl_FragDepth = depthHere;
    }
}
