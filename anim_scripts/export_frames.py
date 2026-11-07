# Exports the walk cycle as one posed OBJ per frame (plus a standing pose) for the game.
#   blender -b Models/pawnHumanRig.blend --python anim_scripts/export_frames.py -- "M3AT: Maru's 3D Animated Toons/Models/chibi" [garment .glb files or folders] [--fit]
# Garments in Models/garments/*.glb are always brought in (import_garments), as well as any named.
# Writes walk_00.obj .. walk_23.obj, run1_00.obj .. run6_23.obj (walk.RUN blended in by sixths: run6 is the full run),
# idle.obj, <folder>.mtl and clip.txt into that folder, and for each of them a face_*.obj (face_idle.obj, face_walk_00.obj,
# ...): the front of the head as posed in that frame, with texture coordinates for the game's face layers (FaceLayers.cs).
#
# IsoRim's model space: one unit = one map cell, +Y up, the figure facing -Z in the file (north once IsoRim flips z).
# Blender's model faces -Y with +Z up, so (x, y, z) -> (-x, z, y): a half turn as well as the axis swap. IsoRim's loader
# mirrors z and takes its face normals from the winding, so the triangles are written in reverse order to come out facing out.
import bpy, json, math, numpy, os, sys
from mathutils import Euler, Matrix, Vector, kdtree
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

FIGURE_HEIGHT = 1.25   # top of IsoRim's person_head.obj, cells (its People.FigureHeight, 1.12, falls short of its own head)
MATERIAL = 'stuff_skin'   # "stuff..." faces take the colour IsoRim is given when drawing (the skin colour)
RUN_LEVELS = 6   # steps from the walk to the run, so the game can ease between them
WORK_FRAMES = 24   # frames a work stroke is baked with (the blow is a seventh of it, so a few frames of its own)

# The face. The game draws Facial Animation's face layers (eyes, lids, brows, mouth) on the front of the head, each
# layer's south texture projected straight from the front. The projection is made once, in the standing pose and in the
# head bone's own frame, and kept with the vertices, so the face turns and bobs with the head in every frame.
FACE_GROUP = 'face'      # a vertex group on the body that picks the face by hand; without one, the head's front is used
FACE_FACING = 0.05       # least share of a triangle's normal that points forward to count as the front of the head
FACE_SPAN = 3.0         # the face texture's width and height, in head heights (IsoRim's own head: 2.9)
FACE_EYES = 0.31         # the eye line's height above the chin, in head heights
EYE_V = 1 - 70.6 / 128   # where the eyes are in Facial Animation's textures (pixel row 70.6 of 128), v from the bottom
FACE_LIFT = 0.002        # cells the face stands off the head, along the normals

# Body types and garments. The body is walk.BODY_PARTS, joined into one mesh if it is split into parts. Its shape keys
# (Male, Female, Thin, Fat, Hulk: the game's body type names; Shrinkhead, a smaller head) are written as moves of the vertices they change, and the skeleton is moved with them: each joint is
# tied to the body vertices nearest it, so a key moves it as it moves the flesh round it. Every other mesh on the rig is
# a garment. Its keys are fitted from the body: each garment vertex follows the body surface under it, unless the garment
# has its own key of that name with something in it (a hand fix).
JOINT_NEAREST = 16       # body vertices each joint is tied to
# Which apparel each garment is drawn for. A garment named X is drawn for the apparel Apparel_X, one named after a mod's
# prefix (DEF_PREFIXES) as CE_X for that mod's CE_Apparel_X, and one with Apparel_ in its name for the apparel of that very
# name; this goes for a mesh, a .glb's file name and a dashed vertex group alike. Its custom property "defs" (defNames,
# comma separated) names others instead, such as a variant of the same shape. A garment listed here (or given custom
# properties "layer" and "groups", comma separated) also stands in for any apparel with no garment of its own that is
# worn on that layer and covers one of those body part groups.
GARMENTS = {'BasicShirt': ('OnSkin', 'Torso'), 'Pants': ('OnSkin', 'Legs')}
DEF_PREFIXES = ('CE_',)   # Combat Extended's

def default_defs(name):
    """The defName of the apparel a garment of this name is drawn for, with no "defs" property to say otherwise."""
    if 'Apparel_' in name:
        return name
    for prefix in DEF_PREFIXES:
        if name.startswith(prefix):
            return prefix + 'Apparel_' + name[len(prefix):]
    return 'Apparel_' + name

# Custom properties for garments kept here rather than on the object in the .blend (an object's own win).
GARMENT_PROPS = {
    # The three sealed armour families, each with its helmet, also drawn for their variants (by the game's own parent defs):
    # recon for the prestige recon, cataphract for the prestige cataphract. The marine family's variants have garments of
    # their own: the prestige marine armour (prestige_paint.py) and the grenadier armour, with a launcher over its shoulder
    # (make_grenadier.py); so do the phoenix armour, a cataphract base with a flamebolt launcher (make_grenadier.py
    # --phoenix), the locust armour, a recon base, with boosters on its boots (make_locust.py), and Biotech's mechlord
    # suit, a recon base with fins, blades and a core (make_mechlord.py). Marine armour's helmet also stands in for
    # Odyssey's vacsuit helmet (its suit has a garment of its own, make_vacsuit.py).
    'PowerArmor': {'hide_body': True, 'hard': True, 'solid_parts': 'marine_backpack',
                   'defs': 'Apparel_PowerArmor'},
    # The grenadier armour (make_grenadier.py): the marine armour with a launcher tube over the left shoulder, weighted as
    # one piece to the chest in the file.
    'ArmorMarineGrenadier': {'hide_body': True, 'hard': True, 'part_weights': True, 'solid_parts': 'marine_backpack'},
    # Prestige marine armour: the marine armour's mesh with gold scrollwork painted on (prestige_paint.py), its own file.
    'PowerArmorPrestige': {'hide_body': True, 'hard': True, 'solid_parts': 'marine_backpack',
                           'defs': 'Apparel_ArmorMarinePrestige'},
    # The vacsuit helmet has a garment of its own (VacsuitHelmet), so this is the marine helmet's only.
    'PowerArmorHelmet': {'hard': True, 'defs': 'Apparel_PowerArmorHelmet'},
    'ArmorRecon': {'hide_body': True, 'hard': True, 'solid_parts': 'recon_backpack',
                   'defs': 'Apparel_ArmorRecon'},
    # Prestige recon armour: the recon armour's mesh with gold scrollwork painted on (prestige_paint.py), its own file.
    'ArmorReconPrestige': {'hide_body': True, 'hard': True, 'solid_parts': 'recon_backpack',
                           'defs': 'Apparel_ArmorReconPrestige'},
    # The locust armour (make_locust.py): the recon armour with a booster on the outside of each boot, weighted whole to
    # its shin in the file.
    'ArmorLocust': {'hide_body': True, 'hard': True, 'part_weights': True, 'solid_parts': 'recon_backpack'},
    # The mechlord suit (make_mechlord.py): the recon armour with a fin up from each shoulder, a blade from each side of
    # the waist, a core on the belly and a disc on the back, each weighted as one piece in the file.
    'MechlordSuit': {'hide_body': True, 'hard': True, 'part_weights': True, 'solid_parts': 'recon_backpack'},
    # The recon helmet has a glass visor (VISOR), so the head is not hidden whole: the face shows through it.
    'ArmorHelmetRecon': {'hard': True, 'hide_hair': True, 'hide_face': False},
    # Biotech's mechanitor helmets, children of the recon helmet in the game (make_mech_helmets.py): the recon helmet with
    # a signal fin over each ear (the mechcommander's) or a ribbed fan behind the head and a chevron on the brow (the
    # mechlord's), its visor kept.
    'ArmorHelmetMechCommander': {'hard': True, 'hide_hair': True, 'hide_face': False},
    'ArmorHelmetMechlordHelmet': {'hard': True, 'hide_hair': True, 'hide_face': False},
    'ArmorCataphract': {'hide_body': True, 'hard': True, 'solid_parts': 'cataphract_backpack',
                        'defs': 'Apparel_ArmorCataphract'},
    # Prestige cataphract armour: the cataphract armour's mesh with gold scrollwork painted on (prestige_paint.py).
    'ArmorCataphractPrestige': {'hide_body': True, 'hard': True, 'solid_parts': 'cataphract_backpack',
                                'defs': 'Apparel_ArmorCataphractPrestige'},
    # The phoenix armour (make_grenadier.py --phoenix): the cataphract armour with a flamebolt launcher behind the left
    # shoulder, weighted as one piece to the chest in the file.
    'ArmorCataphractPhoenix': {'hide_body': True, 'hard': True, 'part_weights': True, 'solid_parts': 'cataphract_backpack'},
    'ArmorHelmetCataphract': {'hide_head': True, 'hard': True},
    # The prestige helmets (make_prestige_helmets.py): the plain ones with gold added after the game's sprites, a crest
    # across the marine's, wings on the recon's, the cataphract's face gilded with a fin on top. Each is drawn for the
    # apparel of its own name.
    'ArmorMarineHelmetPrestige': {'hard': True},
    'ArmorHelmetReconPrestige': {'hard': True, 'hide_hair': True, 'hide_face': False},
    'ArmorHelmetCataphractPrestige': {'hide_head': True, 'hard': True},
    'PlateArmor': {'hide_body': True, 'hard': True},
    # Odyssey's vacsuit, gloves and boots included (make_vacsuit.py), so it hides the body whole; soft, so it takes the keys.
    'Vacsuit': {'hide_body': True},
    'CE_PlateHelmet': {'hide_hair': 'tuck'},
}
# The hats anim_scripts/make_hats.py makes: on the head bone, keeping their shape, the hair cut at their rim.
for _hat in ('SimpleHelmet', 'AdvancedHelmet', 'Tuque', 'CowboyHat', 'BowlerHat', 'HatHood', 'TribalHeaddress', 'PsychicFoilHelmet',
             'Beret', 'HatTop', 'HatLadies', 'AuthorityCap', 'Flophat', 'Headwrap', 'Shadecone', 'Slicecap', 'Tailcap'):
    GARMENT_PROPS[_hat] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': 'tuck'}
# The hood's cowl hangs to the shoulders, which a whole hat on the head bone would sink into as the head bows, so it takes
# the body's weights: the head's over the head, the neck's and chest's round the neck.
GARMENT_PROPS['HatHood'] = {'hide_hair': 'tuck'}
# The headwrap is wound over all the hair, so the hair is left off under it rather than tucked.
GARMENT_PROPS['Headwrap'] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': True}
# The crowns: the crown and the stellic crown are closed over the top, so the hair is tucked under them as under a hat;
# the coronet is an open band stood off the head over the hair, which it leaves on.
GARMENT_PROPS['Crown'] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': 'tuck'}
GARMENT_PROPS['CrownStellic'] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': 'tuck'}
GARMENT_PROPS['Coronet'] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': False, 'hide_face': False}
# The torture crown is an open spiked band stood off the head over the hair, as the coronet.
GARMENT_PROPS['TortureCrown'] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': False, 'hide_face': False}
# The eltex helmet and skullcap (Royalty's psychic headgear) are cut in arches over the eyes and stop above them, so the
# face layers stay; the hair is tucked under them as under a hat (it shows through the skullcap's cut-outs).
for _hat in ('PsyfocusHelmet', 'EltexSkullcap'):
    GARMENT_PROPS[_hat] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': 'tuck', 'hide_face': False}
# The gunlink (make_hats.py) is a unit over the right ear with an arm round to the eye: it leaves the hair and the face on.
GARMENT_PROPS['Gunlink'] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': False, 'hide_face': False}
# So is the airwire headset (make_hats.py): a unit over the right ear, an antenna up from it and a boom round the temple.
GARMENT_PROPS['AirwireHeadset'] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': False, 'hide_face': False}
# And Biotech's other two headsets (make_hats.py): the array headset, a visor over the eyes with an antenna up from each
# end, which stands in front of the face rather than leaving it off, and the integrator headset, a bar over the right of
# the crown down to the ear and a boom round the jaw to a microphone.
for _set in ('ArrayHeadset', 'IntegratorHeadset'):
    GARMENT_PROPS[_set] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': False, 'hide_face': False}
# The masks and the veil make_hats.py makes ride the head as the hats do, but the hair and the face stay: the mesh is over
# the mouth and nose and the eyes show (through the war mask's eye holes). They take the Nose_Pig key from the body (as a
# hard garment's keys are otherwise only its bones' moves), so the muzzle pushes the mask out as it does the face.
# So do the visage mask (a rigid shield over the face with eye holes) and the gas mask (its lenses glass, VISOR, so the
# eyes show behind them), and Anomaly's ritual mask (a skull's face with fangs, scowling eye sockets and a nose hole).
for _mask in ('WarMask', 'WarVeil', 'ClothMask', 'Blindfold', 'VisageMask', 'GasMask', 'CultistMask'):
    GARMENT_PROPS[_mask] = {'bone': walk.HEAD, 'hard': True, 'hide_hair': False, 'hide_face': False, 'fit': 'nose_pig'}
# Combat Extended's gas mask is drawn as the gas mask, and its improvised gas mask (a cloth over the mouth and nose) as
# the cloth mask.
GARMENT_PROPS['GasMask']['defs'] = 'Apparel_GasMask, CE_Apparel_GasMask'
GARMENT_PROPS['ClothMask']['defs'] = 'Apparel_ClothMask, CE_Apparel_ImprovGasMask'
# The broadwrap (make_hats.py) is a cloth over the head, the neck and the shoulders with an opening for the face: as the
# hood's cowl, it takes the body's weights, so it bends at the neck and lies on the shoulders, and it leaves the hair off
# under it (none of it shows round the face in the game's sprite) but the face on.
GARMENT_PROPS['Broadwrap'] = {'hide_hair': True, 'hide_face': False}
# Anomaly's ceremonial hood (make_hats.py) is the broadwrap's cloth drawn up into a point and open down the front, with a
# dark mask in the opening whose eye sockets the pawn's eyes look out of: soft, as the broadwrap, the hair left off.
GARMENT_PROPS['CeremonialCultistMask'] = {'hide_hair': True, 'hide_face': False}
# The burka (make_duster.py --burka) has the broadwrap's cloth joined on as its head piece, closed but for a slit over the
# eyes: the hair left off, the face on, so the eyes look out of the slit (the rest of the face is behind the cloth).
GARMENT_PROPS['Burka'] = {'hide_hair': True, 'hide_face': False}
# The slave collar (make_hats.py) is a band round the neck: it takes the body's weights rather than riding a bone, so it
# bends with the neck and the body keys fatten it with the neck, and it leaves the hair and the face on.
GARMENT_PROPS['Collar'] = {'hide_hair': False, 'hide_face': False}
# The Furskin gene's tufts (anim_scripts/make_fur_edges.py): drawn for the gene, not for apparel (its def is no apparel's, and
# SkinnedFigure.FurPiece finds it by that name). "fur": the body stays whole under it, as its skin is the undercoat, and
# it is written with the normals of the skin under each vertex, so the tufts are lit as the skin is and their roots melt into it.
GARMENT_PROPS['Furskin'] = {'defs': 'Gene_Furskin', 'fur': True}
# The shoes (make_from_groups.py's BELOW: the feet below the trousers): the game has no footwear, so "with_legwear" has
# them worn with any apparel that covers the legs and nothing else (trousers, not a duster or a tribal wrap), and
# "colour" (r, g, b, 0 to 1) is theirs in place of that apparel's. Apparel_Shoes, if a mod has it, also gets them.
# The robe (make_duster.py --robe) is also drawn for Royalty's eltex robe, the same long robe. The prestige robe is the
# same mesh with an image of its own (RobeRoyal: Robe.glb's mesh retextured by anim_scripts/generation/textures/prompts.py).
GARMENT_PROPS['Robe'] = {'defs': 'Apparel_Robe, Apparel_PsyfocusRobe'}
GARMENT_PROPS['RobeRoyal'] = {'defs': 'Apparel_RobeRoyal'}
# The collared shirt is also drawn for Royalty's eltex shirt.
GARMENT_PROPS['CollarShirt'] = {'defs': 'Apparel_CollarShirt, Apparel_PsyfocusShirt'}
GARMENT_PROPS['Shoes'] = {'with_legwear': True, 'colour': (0.30, 0.21, 0.15)}
# The baby romper (make_baby_romper.py: the kid romper sleeveless and short in the leg) is apparel of no def: "baby_romper"
# has the game put it on a baby a few hours old wearing nothing over its trunk or legs (WalkFigures.BabyRomperOf), in its
# own colour.
GARMENT_PROPS['BabyRomper'] = {'baby_romper': True, 'colour': (0.95, 0.88, 0.64)}
# Combat Extended's shields: placed on the back in their .glb, riding the pack's bone whole, and "held": the game
# carries one there, pushed out past everything else on the back (SkinnedFigure.ShieldPush), and takes it into the left
# fist while CE would draw it (drafted, or a job or duty that shows the weapon), laying the guard (walk.shield_at) over
# the rest meanwhile; it hides nothing of the body or the clothes, nor counts in a holster's or pack's clearance. The
# fist's place is the held_at line (shield_held_at): standoff is how far behind the back of the shield's middle the
# grip is, armature units, as make_shields.py's STANDOFF.
for _shield, _standoff in (('CE_MeleeShield', 1.25), ('CE_BallisticShield', 1.75)):
    GARMENT_PROPS[_shield] = {'bone': walk.GEAR['pack']['bone'], 'hard': True, 'held': True, 'standoff': _standoff}
# Garments that keep the weights on them: loose ones standing well off the skin, which on the body's weights stretch
# badly (a long skirt would follow each thigh and tear between the legs). Every other garment's are ignored.
KEEP_WEIGHTS = ('Duster', 'TribalA', 'Parka', 'Cape', 'Robe', 'RobeRoyal', 'Burka', 'LabCoat')
# Textured garments: a garment with a UV map and an image on its material's Base Color is drawn with that image. It is
# written as garment_<name>.png, at most TEXTURE_SIZE pixels across, opaque unless the garment has the custom property
# "cutout" (then cut out below alpha 0.5, as the hair is). In the game it is tinted by the apparel's colour if the apparel
# is made from stuff or has been dyed, else drawn as it is (power armour); the custom property "tint" (true or false)
# decides instead.
TEXTURE_SIZE = 1024
# Hiding whole parts of the body (armour, full helmets): the garment's custom property "hide_groups" names vertex groups
# of the body (comma separated; bone groups work too). A body triangle whose three corners are all in one of them at
# HIDE_GROUP_WEIGHT or more is left out while it is worn, as well as what lies under it. The game also leaves the hair off
# when a worn garment hides most of the scalp, and the face layers when it hides most of the face; the custom properties
# "hide_hair" and "hide_face" (true or false) decide instead; "hide_hair" can also be "tuck": the hair is cut where the
# garment ends and comes out from under it (Source/HairUnderHat.cs), for what rides the head bone. The custom properties "hide_body" and "hide_head" (true)
# hide the body's mesh parts HIDE_BODY and HIDE_HEAD whole (walk.BODY_PARTS, told apart once joined by their
# walk.PART_GROUP groups; body armour, a full helmet). A garment with "hide_body" also
# leaves out the garments worn under it (on a lower apparel layer, covering a body part group it covers), so a shirt
# can't poke through armour; the custom property "hide_clothes" (true or false) decides instead.
HIDE_GROUP_WEIGHT = 0.5
HIDE_BODY = 'Body'
HIDE_HEAD = 'Head'
# Hard armour: a garment with the custom property "hard" keeps its own shape through the body types and genders. Each
# vertex only moves as its bones do (the middle of each bone, by its weights), so a plate stays on a limb that a key
# moves (a Hulk's wider shoulders) without growing or shrinking with the flesh. Keys in HARD_FITTED are fitted from the
# body as on any garment (a smaller head takes a smaller helmet).
HARD_FITTED = ('shrinkhead',)   # lower case: matched without regard to case, as the rig's key has been spelt both ways
# Glass visors: a garment's triangles on a material named VISOR (any case, ".001" and so on after it allowed) are its
# glass, written as visor lines and drawn see-through in the game (SkinnedFigure.DrawVisor). They hide nothing of the
# body, so the face behind them is drawn (and the face layers, which go by how much of the face is hidden).
VISOR = 'visor'
HIDE_REACH = 0.04        # cells: the body under a garment this close (outside it, or inside where it already pokes through) is left out while it is worn
HIDE_LOOK = 0.25         # cells: how far out from the skin the garment must lie in front of it to hide it, so an opening (an open coat's chest, a collar) shows skin

out = os.path.abspath(sys.argv[sys.argv.index('--') + 1])
os.makedirs(out, exist_ok=True)
MTL = os.path.basename(out) + '.mtl'
# --fit (anywhere after the output folder): also check how far each garment sinks into the body in every frame (the FIT
# lines). It writes nothing the game reads and takes a couple of minutes, so it is left out unless asked for: run it after
# changing a garment or the body.
CHECK_FITS = '--fit' in sys.argv[sys.argv.index('--') + 2:]
rig = walk.arm

def import_garments(paths):
    """Brings in garments kept as their own .glb files: each mesh in them is put on the rig as one drawn in the .blend
    is (an Armature modifier on it, so it takes the body's weights, whatever weights the file has, unless it is in KEEP_WEIGHTS). A file with
    one mesh names it after the file, its first letter made a capital (duster.glb is the garment Duster, for
    Apparel_Duster); one with more keeps their own names. One already in the .blend by that name is replaced. Each must
    be exported from the rest pose, where it sits on the body in the .blend, with its shape keys if it has its own."""
    for path in paths:
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)   # else a skinned file brings a bone shape mesh
        new = [o for o in bpy.data.objects if o not in before]
        meshes = [o for o in new if o.type == 'MESH']
        for o in meshes:
            name = os.path.splitext(os.path.basename(path))[0] if len(meshes) == 1 else o.name
            name = name[:1].upper() + name[1:]
            old = bpy.data.objects.get(name)
            if old is not None and old not in new:
                bpy.data.objects.remove(old)
            o.name = name
            world = o.matrix_world.copy()
            o.parent = rig
            o.matrix_world = world
            for m in [m for m in o.modifiers if m.type == 'ARMATURE']:   # a rig the file brought along
                o.modifiers.remove(m)
            o.modifiers.new('Armature', 'ARMATURE').object = rig
            print('GLB %s from %s' % (name, path))
        for o in new:
            if o.type != 'MESH':
                bpy.data.objects.remove(o)

# The garment .glb files: those in GARMENT_FOLDER, and any named (or any folder of them) after the output folder.
GARMENT_FOLDER = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Models', 'garments')
glbs = []
for a in [GARMENT_FOLDER] + sys.argv[sys.argv.index('--') + 2:]:
    if os.path.isdir(a):
        glbs += sorted(os.path.join(a, f) for f in os.listdir(a) if f.lower().endswith('.glb'))
    elif a.lower().endswith('.glb'):
        glbs.append(os.path.abspath(a))
# The body is picked before the garments come in, as an imported garment can have more vertices than the body.
body = walk.join_body()
import_garments(glbs)

skinned = walk.skinned_meshes()
garments = [o for o in skinned if o is not body]
for o in skinned:
    if o.mode != 'OBJECT':   # a mesh saved in Edit Mode skips its Armature modifier: every frame would come out in the rest pose
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.mode_set(mode='OBJECT')
    if o.data.shape_keys:   # exported in the base shape; the keys go out as moves
        for k in o.data.shape_keys.key_blocks:
            k.value = 0.0
sc = bpy.context.scene

def posed_triangles():
    dg = bpy.context.evaluated_depsgraph_get()
    ev = body.evaluated_get(dg)
    me = ev.to_mesh()
    me.calc_loop_triangles()
    mw = body.matrix_world
    verts = [mw @ v.co for v in me.vertices]
    normals = [(mw.to_3x3() @ v.normal).normalized() for v in me.vertices]
    tris = [tuple(t.vertices) for t in me.loop_triangles]
    ev.to_mesh_clear()
    return verts, normals, tris

def head_frame():
    """The head bone's place and turn as posed now (world units, the rig's scale left out): x the figure's left, y up
    the head, z the way it faces."""
    loc, turn, _ = (rig.matrix_world @ rig.pose.bones[walk.HEAD].matrix).decompose()
    return loc, turn

def in_head(p, frame):
    loc, turn = frame
    return turn.inverted() @ (p - loc)

def write_obj(path, verts, tris, scale):
    with open(path, 'w') as f:
        f.write('# M3AT: %s. One unit = one map cell, +Y up, -Z = the way the figure faces.\n' % os.path.basename(path))
        f.write('mtllib %s\nusemtl %s\n' % (MTL, MATERIAL))
        for v in verts:
            f.write('v %.4f %.4f %.4f\n' % (-v.x * scale, v.z * scale, v.y * scale))
        for a, b, c in tris:
            f.write('f %d %d %d\n' % (a + 1, c + 1, b + 1))   # reversed: IsoRim's z flip on loading reverses it back

def find_face(verts, tris):
    """The triangles of the face (standing pose), their texture coordinates by vertex, and the head's middle in the
    head frame."""
    group = body.vertex_groups.get(FACE_GROUP)
    head_group = body.vertex_groups[walk.HEAD].index
    bone_groups = {g.index for g in body.vertex_groups if g.name in rig.data.bones}
    def on_head(i):
        # The bone that moves the vertex most is the head's (groups that are not bones, such as selections, left out).
        gs = [g for g in body.data.vertices[i].groups if g.group in bone_groups]
        return bool(gs) and max(gs, key=lambda g: g.weight).group == head_group
    def chosen(i):
        if group is not None:
            return any(g.group == group.index and g.weight > 0.5 for g in body.data.vertices[i].groups)
        return on_head(i)
    frame = head_frame()
    local = [in_head(v, frame) for v in verts]
    head = [local[i] for i in range(len(verts)) if on_head(i)]
    chin, top = min(p.y for p in head), max(p.y for p in head)
    height = top - chin
    face = []
    for t in tris:
        if not all(chosen(i) for i in t):
            continue
        a, b, c = (local[i] for i in t)
        n = (b - a).cross(c - a)
        if n.length > 1e-9 and n.normalized().z > FACE_FACING:
            face.append(t)
    # Seen from the front, the viewer's right is the figure's left (+x).
    uvs = {}
    for t in face:
        for i in t:
            p = local[i]
            uvs[i] = (0.5 + p.x / (FACE_SPAN * height), EYE_V + (p.y - chin - FACE_EYES * height) / (FACE_SPAN * height))
    return face, uvs, Vector((0.0, (chin + top) / 2, 0.0))

def write_face(path, verts, normals, scale):
    """The face patch as posed now, lifted off the head, and the head's middle (the game grows each face layer a
    little further out from it) on a "# centre" line."""
    loc, turn = head_frame()
    centre = loc + turn @ face_middle
    used = sorted(face_uvs)
    index = {v: n + 1 for n, v in enumerate(used)}
    lift = FACE_LIFT / scale
    with open(path, 'w') as f:
        f.write('# M3AT: %s, the face patch of the frame of the same name (FaceLayers.cs).\n' % os.path.basename(path))
        f.write('# centre %.4f %.4f %.4f\n' % (-centre.x * scale, centre.z * scale, centre.y * scale))
        for i in used:
            v = verts[i] + normals[i] * lift
            f.write('v %.4f %.4f %.4f\n' % (-v.x * scale, v.z * scale, v.y * scale))
        for i in used:
            f.write('vt %.4f %.4f\n' % face_uvs[i])
        for a, b, c in face_tris:
            f.write('f %d/%d %d/%d %d/%d\n' % (index[a], index[a], index[c], index[c], index[b], index[b]))

def write_frame(name, scale, files=True):
    """Checks the pose now against the skinning and the garments and keeps its bone moves for anim.txt; with files, also
    writes it as <name>.obj and face_<name>.obj for the baked figure."""
    global skin_worst
    verts, normals, tris = posed_triangles()
    if files:
        write_obj(os.path.join(out, name + '.obj'), verts, tris, scale)
        write_face(os.path.join(out, 'face_' + name + '.obj'), verts, normals, scale)
    ks = skin_matrices()
    skin_worst = max(skin_worst, skin_error(ks, verts))
    if CHECK_FITS:
        check_fits(ks, verts)
    clip_frames.append(ks)
    return tris

# The skinned figure (SkinnedFigure.cs): the body once, in its rest pose with its bone weights (skin.txt), and every frame
# as one move per bone (anim.txt), which the game blends and applies itself. So a new garment needs one mesh, not one per
# frame. Both files are in the game's model space: Blender's (x, y, z) -> (-x, z, -y), the OBJ files' axes with z already
# flipped, and the triangles in the same order as the OBJ files.
GAME = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
deform = [b.name for b in rig.data.bones if b.use_deform]
bone_index = {n: i for i, n in enumerate(deform)}
skin_worst = 0.0
clip_frames = []

def to_game(v):
    return Vector((-v.x, v.z, -v.y)) * scale

def skin_matrices():
    """Each deforming bone's move from the rest pose to the pose now (world space), as the Armature modifier applies it."""
    bpy.context.view_layer.update()
    return [(rig.matrix_world @ rig.pose.bones[n].matrix) @ (rig.matrix_world @ rig.data.bones[n].matrix_local).inverted()
            for n in deform]

def skin_error(ks, verts):
    """The furthest any vertex skinned with ks is from where Blender put it, in cells."""
    worst = 0.0
    for i, ws in enumerate(weights):
        p = Vector()
        for b, w in ws:
            p += (ks[b] @ rest[i]) * w
        worst = max(worst, (p - verts[i]).length)
    return worst * scale

def write_skin(path):
    lift = FACE_LIFT / scale
    head = bone_index[walk.HEAD]
    # The head's middle as it is in the rest pose, so the game can move it with the head bone.
    loc, turn = head_frame()
    centre = skin_matrices()[head].inverted() @ (loc + turn @ face_middle)
    with open(path, 'w') as f:
        f.write('# M3AT: the chibi in its rest pose with its bone weights (SkinnedFigure.cs), game model space.\n')
        f.write('# v x y z: a vertex. w bone weight ...: its weights (bones as in anim.txt). f a b c: a triangle.\n')
        f.write('# fv i u v x y z: a face patch vertex, made from vertex i, its texture place and its rest place lifted off the head.\n')
        f.write('# ff a b c: a face patch triangle (vertex numbers). centre x y z: the head\'s middle. head b: the head bone.\n')
        for v in rest:
            f.write('v %.5f %.5f %.5f\n' % tuple(to_game(v)))
        for ws in weights:
            f.write('w %s\n' % ' '.join('%d %.5f' % bw for bw in ws))
        for a, b, c in rest_tris:
            f.write('f %d %d %d\n' % (a, c, b))
        for i in sorted(face_uvs):
            p = to_game(rest[i] + rest_normals[i] * lift)
            f.write('fv %d %.5f %.5f %.5f %.5f %.5f\n' % ((i,) + face_uvs[i] + tuple(p)))
        for a, b, c in face_tris:
            f.write('ff %d %d %d\n' % (a, c, b))
        f.write('centre %.5f %.5f %.5f\n' % tuple(to_game(centre)))
        f.write('head %d\n' % head)
        # Each key: the vertices it moves, the joints (bone heads) moved with them, and the head's middle.
        f.write('# key name: then d i x y z (vertex i moves), db b x y z (bone b\'s head moves), dc x y z (the head\'s middle), cells.\n')
        joint_anchors = [anchor(rig.matrix_world @ rig.data.bones[n].head_local) for n in deform]
        centre_anchor = anchor(centre)
        for name, deltas in body_keys.items():
            f.write('key %s\n' % name)
            for i, d in enumerate(deltas):
                if d.length > 1e-7:
                    f.write('d %d %.5f %.5f %.5f\n' % ((i,) + tuple(game_vector(d))))
            for b, a in enumerate(joint_anchors):
                d = moved(a, deltas)
                if d.length > 1e-7:
                    f.write('db %d %.5f %.5f %.5f\n' % ((b,) + tuple(game_vector(d))))
            f.write('dc %.5f %.5f %.5f\n' % tuple(game_vector(moved(centre_anchor, deltas))))
        f.write('# garment name file: a garment mesh on the same rig (its own file).\n')
        for g in garments:
            f.write('garment %s garment_%s.txt\n' % (g.name, g.name))

def game_vector(d):
    """A move (world units) in the game's axes and cells."""
    return Vector((-d.x, d.z, -d.y)) * scale

def shield_held_at(name, g_rest, standoff):
    """A shield placed on the back (its rest places, world): the matrix (game model space) from there to its place at
    rest in the left fist, as walk.shield_rest() holds a shield made in its own frame (the grip's middle at the origin, x
    to the figure's left, y back, z up, its face toward -y). That frame is found from the shield itself: its face's way
    is its thinnest, the one pointing away from the back (+y), so -y in its frame; up is the world's up laid in its
    plane; the grip is behind the middle of its outline, standoff (armature units) back from the back of its middle."""
    MW = rig.matrix_world
    pts = numpy.array([tuple(MW.inverted() @ p) for p in g_rest])
    c = pts.mean(0)
    w, v = numpy.linalg.eigh(numpy.cov((pts - c).T))
    n = Vector(v[:, 0]).normalized()   # the thinnest way
    if n.y < 0:
        n = -n
    y = -n
    z = (Vector((0, 0, 1)) - n * n.z).normalized()
    x = y.cross(z)
    R = Matrix((x, y, z)).transposed()   # its frame's axes as columns, in armature space
    q = (pts - c) @ numpy.array(R)       # each place in its frame, from c
    lo, hi = q.min(0), q.max(0)
    mid = (lo + hi) / 2
    half = (hi - lo) / 2
    near = (abs(q[:, 0] - mid[0]) < 0.25 * half[0]) & (abs(q[:, 2] - mid[2]) < 0.25 * half[2])
    back = q[near, 1].max() if near.any() else hi[1]
    grip = Vector(c) + R @ Vector((mid[0], back + standoff, mid[2]))
    frame = Matrix.Translation(grip) @ R.to_4x4()   # its frame to armature space
    held, _ = walk.shield_rest()
    world = MW @ held @ frame.inverted() @ MW.inverted()
    print('SHIELD %s: on the back, its face toward (%.2f, %.2f, %.2f), %.2f wide, %.2f high, %.2f deep; grip at (%.2f, %.2f, %.2f)'
          % ((name,) + tuple(n) + (2 * half[0], 2 * half[2], 2 * half[1]) + tuple(grip)))
    return Matrix.Scale(scale, 4) @ GAME @ world @ GAME.inverted() @ Matrix.Scale(1.0 / scale, 4)

# Solid parts: a garment whose custom property "solid_parts" names sets in SOLID_PARTS (comma separated) has the vertices at
# those rest places (within SOLID_REACH, so every copy split at a texture seam) weighted alike, the mean of their own
# weights less the arm's, the neck's and the head's (SOLID_NOT: a backpack's top edge is by the neck, where the skin
# is mostly the head's, which would turn it with the head), so the part moves as one piece with the chest rather than
# bending with the body under it: the armours' backpacks.
SOLID_PARTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'solid_parts.json')
SOLID_REACH = 1e-3
SOLID_NOT = ('upper_arm', 'forearm', 'hand', 'spine.004', 'spine.005', 'spine.006')

def solid_weights(name, part, rest, weights):
    """Gives the vertices of rest at the places of SOLID_PARTS' set part one set of weights, in place in weights."""
    with open(SOLID_PARTS) as f:
        places = json.load(f).get(part)
    if not places:
        print('WARNING: %s names the solid part %s, which %s has not' % (name, part, SOLID_PARTS))
        return
    tree = kdtree.KDTree(len(places))
    for i, p in enumerate(places):
        tree.insert(p, i)
    tree.balance()
    piece, found = [], set()
    for i, p in enumerate(rest):
        _, k, d = tree.find(p)
        if d <= SOLID_REACH:
            piece.append(i)
            found.add(k)
    if len(found) < len(places):
        print('WARNING: %s has only %d of the %d places of its solid part %s (moved since they were taken?)'
              % (name, len(found), len(places), part))
    if not piece:
        return
    names = {i: n for n, i in bone_index.items()}
    mean = {}
    for i in piece:
        for b, w in weights[i]:
            if not names[b].startswith(SOLID_NOT):
                mean[b] = mean.get(b, 0.0) + w
    total = sum(mean.values())
    mean = [(b, w / total) for b, w in sorted(mean.items(), key=lambda bw: -bw[1]) if w / total > 0.01]
    total = sum(w for _, w in mean)
    mean = [(b, w / total) for b, w in mean]
    for i in piece:
        weights[i] = list(mean)
    print('SOLID %s %s: %d vertices at %d places, weights %s'
          % (name, part, len(piece), len(found), ', '.join('%s %.2f' % (names[b], w) for b, w in mean)))

def base_shape(o):
    """The mesh's vertices in its base shape (world), whatever its keys are set to."""
    keys = o.data.shape_keys
    co = [k.co for k in keys.reference_key.data] if keys else [v.co for v in o.data.vertices]
    return [o.matrix_world @ c for c in co]

def key_deltas(o):
    """Each shape key's move of every vertex (world units), by key name."""
    keys = o.data.shape_keys
    if not keys:
        return {}
    ref = keys.reference_key
    m3 = o.matrix_world.to_3x3()
    return {k.name: [m3 @ (k.data[i].co - ref.data[i].co) for i in range(len(o.data.vertices))]
            for k in keys.key_blocks if k != ref}

def anchor(p):
    """Ties the point p to the body vertices nearest it: weights adding up to 1 whose weighted mean of those vertices
    is exactly p, as close to plain inverse-distance weights as that allows. A key then moves p with the flesh round it,
    and a key that scales or shifts the whole mesh scales or shifts p exactly the same."""
    near = rest_tree.find_n(p, JOINT_NEAREST)
    w0 = [1.0 / (d * d + 1e-6) for _, _, d in near]
    total = sum(w0)
    w0 = [w / total for w in w0]
    # In doubles, not mathutils' floats: this matrix is badly conditioned for a small cluster of vertices far from the
    # origin, and in floats the weights for the fingertip joint added up to nearly 10, which threw that joint a tenth of
    # a cell or more in the Male, Fat and Babylimbs keys and stretched the hand wherever the fingers curled.
    cs = numpy.array([(co.x, co.y, co.z, 1.0) for co, _, _ in near])
    start = numpy.array(w0)
    try:
        w = start + cs @ numpy.linalg.solve(cs.T @ cs, numpy.array((p.x, p.y, p.z, 1.0)) - cs.T @ start)
        if abs(w.sum() - 1.0) < 1e-6 and numpy.abs(w).max() < 3.0:
            return [(i, x) for (_, i, _), x in zip(near, w.tolist())]
    except numpy.linalg.LinAlgError:   # the nearest vertices lie in a plane
        pass
    return [(i, x) for (_, i, _), x in zip(near, w0)]

def moved(a, deltas):
    return sum((deltas[i] * w for i, w in a), Vector())

def hidden_under(g_rest, tris):
    """The body triangles (numbered as in skin.txt) under the garment, which the game leaves out while it is worn so the
    skin can't show through it. Tested once, at rest: the garment follows the skin, so what it covers stays covered.
    A body vertex is under it if the nearest point of the garment is within HIDE_REACH (outside the skin, or inside
    where the skin already pokes through) and the garment also lies straight out from the skin there: a line along the
    skin's normal, from HIDE_REACH inside it to HIDE_LOOK out, meets it. Nearness alone hid the skin in an opening (an
    open coat's chest, a collar, past a hem), which the folded lapel or hem beside it is near but nothing is in front of.
    A triangle is hidden if its corners and middle are all under it."""
    tree = BVHTree.FromPolygons(g_rest, tris)
    reach = HIDE_REACH / scale
    look = HIDE_LOOK / scale
    def covered(p, n):
        if tree.find_nearest(p, reach)[0] is None:
            return False
        return tree.ray_cast(p - n * reach, n, reach + look)[0] is not None
    under = [covered(p, n) for p, n in zip(rest, rest_normals)]
    hide = []
    for k, t in enumerate(rest_tris):
        if all(under[i] for i in t):
            middle = sum((rest[i] for i in t), Vector()) / 3
            normal = sum((rest_normals[i] for i in t), Vector()).normalized()
            if covered(middle, normal):
                hide.append(k)
    return hide

def base_colour_image(g):
    """The image feeding the Base Color of the garment's material (through any nodes between), or None."""
    def upstream(socket, seen):
        for link in socket.links:
            n = link.from_node
            if n in seen:
                continue
            seen.add(n)
            if n.type == 'TEX_IMAGE' and n.image:
                return n.image
            for s in n.inputs:
                found = upstream(s, seen)
                if found:
                    return found
        return None
    for slot in g.material_slots:
        m = slot.material
        if not m or not m.use_nodes:
            continue
        for n in m.node_tree.nodes:
            if n.type == 'BSDF_PRINCIPLED':
                found = upstream(n.inputs['Base Color'], set())
                if found:
                    return found
    return None

written_textures = {}   # image name: the file it was written to, so garments sharing an image share the file

def write_texture(image, path, opaque):
    """Writes the image as a PNG no more than TEXTURE_SIZE across (the pixels as stored, so sRGB stays sRGB)."""
    if image.name in written_textures:
        return written_textures[image.name]
    copy = image.copy()
    w, h = copy.size
    if max(w, h) > TEXTURE_SIZE:
        copy.scale(max(1, w * TEXTURE_SIZE // max(w, h)), max(1, h * TEXTURE_SIZE // max(w, h)))
    w, h = copy.size
    px = list(copy.pixels)
    if opaque:
        px[3::4] = [1.0] * (w * h)
    out_image = bpy.data.images.new(os.path.basename(path), w, h, alpha=True)
    out_image.pixels = px
    out_image.filepath_raw = path
    out_image.file_format = 'PNG'
    out_image.save()
    bpy.data.images.remove(out_image)
    bpy.data.images.remove(copy)
    written_textures[image.name] = os.path.basename(path)
    return written_textures[image.name]

class Props:
    """A garment's custom properties: its own, else GARMENT_PROPS'."""
    def __init__(self, g):
        self.g, self.table = g, GARMENT_PROPS.get(g.name, {})
    def __contains__(self, k):
        return k in self.g or k in self.table
    def __getitem__(self, k):
        return self.g[k] if k in self.g else self.table[k]
    def get(self, k, default=None):
        return self[k] if k in self else default

bone_anchor_list = None

def bone_anchors():
    """Each deforming bone's middle tied to the body vertices round it (anchor), so a key moves it as the flesh round it."""
    global bone_anchor_list
    if bone_anchor_list is None:
        bone_anchor_list = [anchor(rig.matrix_world @ ((rig.data.bones[n].head_local + rig.data.bones[n].tail_local) / 2))
                            for n in deform]
    return bone_anchor_list

def write_garment(g, path):
    """A garment: its base shape, its bone weights, its triangles, and a move per vertex for every body key."""
    props = Props(g)
    g_rest = base_shape(g)
    own = key_deltas(g)
    defs = [x.strip() for x in props.get('defs', default_defs(g.name)).split(',') if x.strip()]
    layer, groups = GARMENTS.get(g.name, (None, ''))
    layer = props.get('layer', layer)
    groups = [x.strip() for x in props.get('groups', ','.join(groups) if not isinstance(groups, str) else groups).split(',') if x.strip()]
    rigid = bool(props.get('rigid', False))   # moved whole by a key (a helmet or a plate), not reshaped with the body
    hard = bool(props.get('hard', False))     # moved with its bones by a key, not reshaped with the body (hard armour)
    fit = [x.strip().lower() for x in props.get('fit', '').split(',') if x.strip()]   # keys a hard garment still takes from the body
    # Where each vertex sits over the body: the nearest triangle and the place on it.
    over, gap = [], []
    for p in g_rest:
        loc, _, ti, dist = body_tree.find_nearest(p)
        tri = rest_tris[ti]
        over.append((tri, poly_3d_calc([rest[i] for i in tri], loc)))
        gap.append(dist)
    g.data.calc_loop_triangles()
    tris = [tuple(t.vertices) for t in g.data.loop_triangles]
    glass = {i for i, slot in enumerate(g.material_slots) if slot.material and slot.material.name.lower().split('.')[0] == VISOR}
    visor = [k for k, t in enumerate(g.data.loop_triangles) if t.material_index in glass]
    glass_tris = set(visor)
    # The texture, if it has one. A vertex with more than one place on it (a UV seam) is written once per place; copy i
    # is of the garment's vertex order[i], and the game welds the copies back together by place for the shading.
    image = base_colour_image(g) if g.data.uv_layers.active else None
    order, uvs, out_tris = list(range(len(g_rest))), None, tris
    if image:
        uv_data = g.data.uv_layers.active.data
        uvs = [None] * len(g_rest)
        copies = {}
        out_tris = []
        for t in g.data.loop_triangles:
            tri = []
            for li in t.loops:
                vi = g.data.loops[li].vertex_index
                uv = tuple(round(c, 6) for c in uv_data[li].uv)
                if uvs[vi] is None:
                    uvs[vi] = uv
                    copies[(vi, uv)] = vi
                elif (vi, uv) not in copies:
                    copies[(vi, uv)] = len(order)
                    order.append(vi)
                    uvs.append(uv)
                tri.append(copies[(vi, uv)])
            out_tris.append(tuple(tri))
        uvs = [uv if uv is not None else (0.0, 0.0) for uv in uvs]
        texture = write_texture(image, os.path.join(os.path.dirname(path), 'garment_%s.png' % g.name), not props.get('cutout', False))
    # The weights: any on the garment are ignored, but for KEEP_WEIGHTS. They are the body's, blended from the place under
    # each vertex, so the garment bends exactly as the skin under it does and can't sink into it at a bent knee or elbow.
    # A garment with the custom property "bone" (a hat on the head bone) rides that bone whole instead: the skin under
    # a helmet's back edge is partly the neck's, which would bend it.
    own_weights = g.name in KEEP_WEIGHTS
    if own_weights and not any(g.vertex_groups[x.group].name in bone_index for v in g.data.vertices for x in v.groups if x.weight > 0):
        # A .glb written with no skin has lost them. Left on the hips, the whole garment would stand still as the figure moves.
        print('WARNING: %s is in KEEP_WEIGHTS but has no weights on it, so it takes the body\'s' % g.name)
        own_weights = False
    g_weights = []
    pieces = {}   # part_weights: the vertices that kept their own weights, by those weights (each one a solid piece)
    if props.get('bone'):
        g_weights = [[(bone_index[props['bone']], 1.0)] for _ in g_rest]
    elif own_weights:
        for v in g.data.vertices:
            ws = [(bone_index[g.vertex_groups[x.group].name], x.weight) for x in v.groups
                  if x.weight > 0 and g.vertex_groups[x.group].name in bone_index]
            total = sum(w for _, w in ws)
            g_weights.append([(b, w / total) for b, w in ws] if total > 0 else [(bone_index[walk.HIPS], 1.0)])
    else:
        # A garment with the custom property "part_weights" keeps the weights on the vertices that have any (the
        # locust armour's boosters, whole on their shins, so they stay straight as the knee and ankle bend); the rest
        # take the body's as below.
        part = bool(props.get('part_weights', False))
        for vi, (v, (tri, bw)) in enumerate(zip(g.data.vertices, over)):
            if part:
                ws = [(bone_index[g.vertex_groups[x.group].name], x.weight) for x in v.groups
                      if x.weight > 0 and g.vertex_groups[x.group].name in bone_index]
                total = sum(w for _, w in ws)
                if total > 0:
                    g_weights.append([(b, w / total) for b, w in ws])
                    pieces.setdefault(tuple(sorted((b, round(w / total, 4)) for b, w in ws)), []).append(vi)
                    continue
            ws = {}
            for i, t in zip(tri, bw):
                for b, w in weights[i]:
                    ws[b] = ws.get(b, 0.0) + w * t
            ws = [(b, w) for b, w in ws.items() if w > 1e-4]
            total = sum(w for _, w in ws)
            g_weights.append([(b, w / total) for b, w in ws])
    for part in [x.strip() for x in props.get('solid_parts', '').split(',') if x.strip()]:
        solid_weights(g.name, part, g_rest, g_weights)
    garment_fits.append({'name': g.name, 'rest': g_rest, 'weights': g_weights, 'over': over, 'gap': gap, 'sink': 0.0})
    hide = [] if props.get('fur', False) or props.get('held', False) else hidden_under(g_rest, [t for k, t in enumerate(tris) if k not in glass_tris])
    hide_groups = [x.strip() for x in props.get('hide_groups', '').split(',') if x.strip()]
    hide_parts = [x for flag, x in (('hide_body', HIDE_BODY), ('hide_head', HIDE_HEAD)) if props.get(flag, False)]
    hide_groups += [walk.PART_GROUP % x for x in hide_parts if walk.PART_GROUP % x not in hide_groups]
    if hide_groups:
        missing = [x for x in hide_groups if x not in body.vertex_groups]
        if missing:
            print('WARNING: %s hides the body groups %s, which the body has not (hide_body and hide_head need the body '
                  'split into Body and Head)' % (g.name, ', '.join(missing)))
        index = {body.vertex_groups[x].index for x in hide_groups if x in body.vertex_groups}
        inside = [any(e.group in index and e.weight >= HIDE_GROUP_WEIGHT for e in v.groups) for v in body.data.vertices]
        hide = sorted(set(hide) | {k for k, t in enumerate(rest_tris) if all(inside[i] for i in t)})
    stretch, hand = 0.0, []
    with open(path, 'w') as f:
        f.write('# M3AT: the garment %s on the chibi (SkinnedFigure.cs), game model space, as skin.txt.\n' % g.name)
        f.write('# defs: the apparel it is drawn for. layer and groups (if any): it also stands in for apparel with no garment of\n')
        f.write('# its own worn on that layer and covering one of those body part groups.\n')
        f.write('defs %s\n' % ' '.join(defs))
        if layer:
            f.write('layer %s\ngroups %s\n' % (layer, ' '.join(groups)))
        if image:
            f.write('# texture: its image (t lines, a place on it per vertex); tint: whether the apparel\'s colour tints it.\n')
            f.write('texture %s\n' % texture)
            if 'tint' in props:
                f.write('tint %s\n' % ('yes' if props['tint'] else 'no'))
        for prop in ('hide_hair', 'hide_face'):
            if prop in props:
                f.write('%s %s\n' % (prop, props[prop] if props[prop] == 'tuck' else 'yes' if props[prop] else 'no'))
        if props.get('hide_clothes', props.get('hide_body', False)):
            f.write('hide_clothes yes\n')
        if props.get('with_legwear', False):
            f.write('# with_legwear: also drawn under apparel covering only the legs; colour: its own, not the apparel\'s.\n')
            f.write('with_legwear yes\n')
        if props.get('baby_romper', False):
            f.write('# baby_romper: worn by a baby a few hours old with nothing over its trunk or legs.\n')
            f.write('baby_romper yes\n')
        if 'colour' in props:
            f.write('colour %.3f %.3f %.3f\n' % tuple(props['colour'])[:3])
        if props.get('held', False):
            f.write('# held: a shield, carried on the back as placed here and in the hand while the game shows the weapon; it\n')
            f.write('# hides nothing. held_at: the matrix (top three rows) from its place here to its place at rest in the left\n')
            f.write('# fist, which hand.L then moves.\n')
            f.write('held yes\n')
            m = shield_held_at(g.name, g_rest, props.get('standoff', 1.5))
            f.write('held_at %s\n' % ' '.join('%.5f' % (m[r][c] + 0.0) for r in range(3) for c in range(4)))
        if visor:
            f.write('# visor k ...: its own triangles (its f lines, from 0) that are glass, drawn see-through.\n')
            for k in range(0, len(visor), 32):
                f.write('visor %s\n' % ' '.join(str(x) for x in visor[k:k + 32]))
        f.write('# hide k ...: body triangles (skin.txt\'s f lines, from 0) left out while it is worn.\n')
        for k in range(0, len(hide), 32):
            f.write('hide %s\n' % ' '.join(str(x) for x in hide[k:k + 32]))
        for i in order:
            f.write('v %.5f %.5f %.5f\n' % tuple(to_game(g_rest[i])))
        for i in order:
            f.write('w %s\n' % ' '.join('%d %.5f' % bw for bw in g_weights[i]))
        if uvs:
            for uv in uvs:
                f.write('t %.6f %.6f\n' % uv)
        if props.get('fur', False):
            for i in order:
                tri, bw = over[i]
                f.write('n %.5f %.5f %.5f\n' % tuple(game_vector(sum((rest_normals[k] * b for k, b in zip(tri, bw)), Vector()).normalized())))
        for a, b, c in out_tris:
            f.write('f %d %d %d\n' % (a, c, b))
        for name, deltas in body_keys.items():
            mine = own.get(name)
            if mine and any(d.length > 1e-6 for d in mine):
                fitted = mine
                hand.append(name)
            elif hard and (props.get('held', False) or (name.lower() not in HARD_FITTED and name.lower() not in fit)):
                # (a held one, a shield, takes no key from the body at all: at rest the hand hangs by the hip and the
                # head, whose skin under it would tear it)
                bone_moves = [moved(a, deltas) for a in bone_anchors()]
                fitted = [sum((bone_moves[b] * w for b, w in g_weights[i]), Vector()) for i in range(len(g_rest))]
            else:
                fitted = [sum((deltas[i] * w for i, w in zip(tri, bw)), Vector()) for tri, bw in over]
                if rigid:
                    mean = sum(fitted, Vector()) / len(fitted)
                    fitted = [mean] * len(fitted)
                else:
                    # Where the body under a vertex changed which way it faces (between the thighs), following the
                    # nearest triangle can leave it under the reshaped skin: pushed back out to the gap it had.
                    shaped = [p + d for p, d in zip(rest, deltas)]
                    tree = BVHTree.FromPolygons(shaped, rest_tris)
                    for i, p in enumerate(g_rest):
                        q = p + fitted[i]
                        loc, _, ti, dist = tree.find_nearest(q)
                        a, b, c = (shaped[j] for j in rest_tris[ti])
                        n = (b - a).cross(c - a).normalized()
                        if n.dot(q - loc) < 0:
                            fitted[i] = loc + n * gap[i] - p
                # A piece with its own weights (a launcher, a booster) is solid: each key moves it whole, by the mean of
                # what the skin under it does, as fitting each vertex bent the grenadier's tube near the shrunk head.
                for piece in pieces.values():
                    mean = sum((fitted[i] for i in piece), Vector()) / len(piece)
                    for i in piece:
                        fitted[i] = mean
            f.write('key %s\n' % name)
            for i, vi in enumerate(order):
                d = fitted[vi]
                if d.length > 1e-7:
                    f.write('d %d %.5f %.5f %.5f\n' % ((i,) + tuple(game_vector(d))))
            for e in g.data.edges:   # how far the fit pulls neighbours apart or together (a tear shows as a big one)
                a, b = e.vertices
                before = (g_rest[a] - g_rest[b]).length
                stretch = max(stretch, abs(((g_rest[a] + fitted[a]) - (g_rest[b] + fitted[b])).length - before) * scale)
    print('GARMENT %s: %d vertices (%d written), for %s%s%s, %s weights%s, hides %d of %d body triangles, keys fitted from the body%s; edge lengths change by at most %.4f cells'
          % (g.name, len(g_rest), len(order), '+'.join(defs), (' and in for %s %s' % (layer, '+'.join(groups))) if layer else '',
             ((', textured %s' % texture) if image else '') + ((', %d glass' % len(visor)) if visor else ''), props['bone'] if props.get('bone') else 'own' if own_weights else 'body',
             ', hard (only %s fitted)' % ', '.join(list(HARD_FITTED) + fit) if hard else '', len(hide), len(rest_tris),
             (' (own: %s)' % ', '.join(hand)) if hand else '', stretch))

garment_fits = []
fit_arrays = None

def fit_setup():
    """Every garment vertex of garment_fits as numpy arrays, all garments in one run (the order of garment_fits): its rest
    place, its weight on each bone, the body triangle under it and its place in that triangle, and where each garment
    starts."""
    rest_at, weight, tri, bary, starts = [], [], [], [], []
    for fit in garment_fits:
        starts.append(len(rest_at))
        for p, ws, (t, bw) in zip(fit['rest'], fit['weights'], fit['over']):
            rest_at.append((p.x, p.y, p.z, 1.0))
            row = numpy.zeros(len(deform))
            for b, w in ws:
                row[b] += w
            weight.append(row)
            tri.append(t)
            bary.append(bw)
    return (numpy.array(rest_at).reshape(-1, 4), numpy.array(weight).reshape(-1, len(deform)),
            numpy.array(tri, dtype=numpy.int64).reshape(-1, 3), numpy.array(bary).reshape(-1, 3), numpy.array(starts))

def check_fits(ks, verts):
    """For each garment, skins it with ks and keeps the furthest any vertex gets below the body triangle it was over at
    rest (cells, along that triangle's posed normal; nothing if it stays outside). verts: the body as Blender posed it.
    In numpy over every garment at once, as it runs on every frame of every clip."""
    global fit_arrays
    if not garment_fits:
        return
    if fit_arrays is None:
        fit_arrays = fit_setup()
    rest_at, weight, tri, bary, starts = fit_arrays
    if len(rest_at) == 0:
        return
    bones = numpy.array([[m[r][c] for r in range(3) for c in range(4)] for m in ks])   # each bone's top three rows
    blended = (weight @ bones).reshape(-1, 3, 4)   # each vertex's own blend of them, as linear skinning makes it
    q = numpy.einsum('nij,nj->ni', blended, rest_at)
    body_at = numpy.array([(v.x, v.y, v.z) for v in verts])
    a, b, c = body_at[tri[:, 0]], body_at[tri[:, 1]], body_at[tri[:, 2]]
    n = numpy.cross(b - a, c - a)
    length = numpy.linalg.norm(n, axis=1)
    usable = length >= 1e-12
    on = a * bary[:, 0:1] + b * bary[:, 1:2] + c * bary[:, 2:3]
    depth = numpy.full(len(q), -numpy.inf)
    depth[usable] = -numpy.einsum('ni,ni->n', n[usable] / length[usable, None], (q - on)[usable]) * scale
    ends = list(starts[1:]) + [len(q)]
    for fit, s, e in zip(garment_fits, starts, ends):
        if e > s:
            fit['sink'] = max(fit['sink'], float(depth[s:e].max()))

def write_anim(path, clips):
    """Each clip's frames, one line per bone: the rotation (w x y z) and where the bone's rest head ends up, in cells."""
    flip = GAME.inverted()
    with open(path, 'w') as f:
        f.write('# M3AT: the clips for the skinned chibi (SkinnedFigure.cs), game model space.\n')
        f.write('# bone name x y z: a deforming bone and its rest head. clip name frames: then frames x bones lines of\n')
        f.write('# k qw qx qy qz x y z: the bone\'s turn from rest and where its head goes, in cells.\n')
        f.write('# (bone lines end with the parent bone\'s number, -1 for none)\n')
        f.write('# A clip line ends with its length in game seconds when it is timed by the clock (0: by the ground covered).\n')
        f.write('# over name w ...: how much of each bone the clip takes when laid over another (the drafted arms over the walk).\n')
        for n in deform:
            b = rig.data.bones[n]
            parent = b.parent
            while parent is not None and parent.name not in bone_index:
                parent = parent.parent
            f.write('bone %s %.5f %.5f %.5f %d\n' % ((n,) + tuple(to_game(rig.matrix_world @ b.head_local))
                                                     + (bone_index[parent.name] if parent is not None else -1,)))
        for name, frames, seconds in clips:
            f.write('clip %s %d %.3f\n' % (name, len(frames), seconds))
            for ks in frames:
                for n, k in zip(deform, ks):
                    g = GAME @ k @ flip
                    q = g.to_quaternion()
                    h = to_game(k @ (rig.matrix_world @ rig.data.bones[n].head_local))
                    f.write('k %.6f %.6f %.6f %.6f %.5f %.5f %.5f\n' % (q.w, q.x, q.y, q.z, h.x, h.y, h.z))
        for name, _, _ in clips:
            mask = masks.get(name.split('~')[0])   # a variant takes its clip's
            if mask is not None:
                f.write('over %s %s\n' % (name, ' '.join('%.3f' % mask[n] for n in deform)))
        f.write('# weapon_hold grip scale lift out forward: walk.HAND_GRIP, WEAPON_SCALE, WEAPON_LIFT, WEAPON_OUT, WEAPON_FORWARD.\n')
        f.write('# aim_pitch degrees: walk.AIM_PITCH, how far up and down the aim_, fire_ and nock_ clips\' frames reach.\n')
        f.write('aim_pitch %.3f\n' % walk.AIM_PITCH)
        f.write('# weapon kind hand line behind drop roll x y z ax ay az scale: walk.WEAPONS, the aim and at turned into model space,\n')
        f.write('# scale its type\'s (walk.weapon_scale).\n')
        f.write('# weapon_grip side x y z: the way that fist closes from the wrist at rest (along the hand bone), model space;\n')
        f.write('# a weapon on the "hand" line is held there and turns with the hand.\n')
        f.write('weapon_hold %.4f %.4f %.4f %.4f %.4f\n' % (walk.HAND_GRIP, walk.WEAPON_SCALE, walk.WEAPON_LIFT, walk.WEAPON_OUT,
                                                    walk.WEAPON_FORWARD))
        for side in ('L', 'R'):
            bone = rig.data.bones['hand.' + side]
            d = (rig.matrix_world.to_3x3() @ (bone.tail_local - bone.head_local)).normalized()
            f.write('weapon_grip %s %.4f %.4f %.4f\n' % (side, -d.x + 0.0, d.z + 0.0, -d.y + 0.0))
        for kind, w in walk.WEAPONS.items():
            a, at = w.get('aim', (0.0, 0.0, 0.0)), w.get('at', (0.0, 0.0, 0.0))
            f.write('weapon %s %s %s %.4f %.4f %.3f %.4f %.4f %.4f %.4f %.4f %.4f %.4f\n'
                    % (kind, w['hand'], w['line'], w['behind'], w['drop'], w['roll'], -a[0] + 0.0, a[2] + 0.0, -a[1] + 0.0,
                       -at[0] + 0.0, at[2] + 0.0, -at[1] + 0.0, walk.weapon_scale(kind)))
        # The sprite slab's holds (walk.SLAB_WEAPONS) where they differ, as '<name>_slab', its type's scale.
        for kind, w in getattr(walk, 'SLAB_WEAPONS', {}).items():
            if w == walk.WEAPONS.get(kind):
                continue
            a, at = w.get('aim', (0.0, 0.0, 0.0)), w.get('at', (0.0, 0.0, 0.0))
            f.write('weapon %s %s %s %.4f %.4f %.3f %.4f %.4f %.4f %.4f %.4f %.4f %.4f\n'
                    % (kind + walk.SLAB, w['hand'], w['line'], w['behind'], w['drop'], w['roll'], -a[0] + 0.0, a[2] + 0.0,
                       -a[1] + 0.0, -at[0] + 0.0, at[2] + 0.0, -at[1] + 0.0, walk.weapon_scale(kind)))
        if hasattr(walk, 'WEAPON_TYPES'):
            f.write('# weapon_type name type: walk.WEAPON_TYPES (by defName or drafted kind). type_scale type scale holster: each\n')
            f.write('# type\'s size against the floor (walk.TYPE_SCALE, the default of the mod option) and the holster slots it takes (walk.TYPE_HOLSTER).\n')
            for name, kind in walk.WEAPON_TYPES.items():
                f.write('weapon_type %s %s\n' % (name, kind))
            for kind, scale in walk.TYPE_SCALE.items():
                f.write('type_scale %s %.4f %s\n' % (kind, scale, walk.TYPE_HOLSTER.get(kind, 'big')))
        if hasattr(walk, 'HOLSTERS'):
            f.write('# holster slot size bone x y z ax ay az roll behind ox oy oz: walk.HOLSTERS, where a\n')
            f.write('# weapon not held is drawn: its place at rest (model space, model units) and its line, out the way the game\n')
            f.write('# pushes it clear of garments.\n')
            f.write('# holster_melee slot x y z ax ay az roll: its melee_ place, aim and roll, for a melee weapon.\n')
            f.write('# holster_pack slot yes|no: the slot is used only with, or only without, a pack worn. holster_packs def ...: the\n')
            f.write('# apparel that counts as a pack (walk.HOLSTER_PACKS). holster_belt slot yes|no: the same with a belt.\n')
            if getattr(walk, 'HOLSTER_PACKS', None):
                f.write('holster_packs %s\n' % ' '.join(walk.HOLSTER_PACKS))
            hips = to_game(rig.matrix_world @ rig.data.bones[walk.HIPS].head_local)
            def game_dir(v):
                return (-v[0] + 0.0, v[2] + 0.0, -v[1] + 0.0)
            for slot, h in walk.HOLSTERS.items():
                line = [slot, h['size'], h['bone']]
                at, aim, roll = walk.holster_place(slot)
                p = hips + Vector(game_dir(at))
                nums = [p.x, p.y, p.z] + list(game_dir(aim)) + [roll, h.get('behind', 0.0)] + list(game_dir(h.get('out', (0.0, 1.0, 0.0))))
                line += ['%.4f' % (v + 0.0) for v in nums]
                f.write('holster %s\n' % ' '.join(line))
                if any(k in h for k in ('melee_at', 'melee_aim', 'melee_roll')):
                    at, aim, roll = walk.holster_place(slot, True)
                    p = hips + Vector(game_dir(at))
                    f.write('holster_melee %s %s\n' % (slot, ' '.join('%.4f' % (v + 0.0) for v in [p.x, p.y, p.z] + list(game_dir(aim)) + [roll])))
                if 'pack' in h:
                    f.write('holster_pack %s %s\n' % (slot, 'yes' if h['pack'] else 'no'))
                if 'belt' in h:
                    f.write('holster_belt %s %s\n' % (slot, 'yes' if h['belt'] else 'no'))
            if hasattr(walk, 'GEAR'):
                f.write('# gear mount bone x y z ox oy oz scale depth qx qy qz qw: walk.GEAR, where IsoRim\'s worn packs, belts and hip tools go: the\n')
                f.write('# model\'s reference point at rest (model space, model units), the way out past garments, the model\'s size (a share of\n')
                f.write('# IsoRim\'s), a belt\'s extra depth stretch and the model\'s turn at rest about its reference point (model space).\n')
                axes = Matrix(((-1, 0, 0), (0, 0, 1), (0, -1, 0)))   # as game_dir
                for mount, g in walk.GEAR.items():
                    p = hips + Vector(game_dir(g['at']))
                    q = (axes @ Euler([math.radians(v) for v in g.get('turn', (0.0, 0.0, 0.0))], 'XYZ').to_matrix() @ axes.transposed()).to_quaternion()
                    nums = [p.x, p.y, p.z] + list(game_dir(g['out'])) + [g['scale'], g.get('depth', 1.0), q.x, q.y, q.z, q.w]
                    f.write('gear %s %s %s\n' % (mount, g['bone'], ' '.join('%.4f' % (v + 0.0) for v in nums)))
            f.write('# draw_timing at: walk.DRAW_AT, where a draw clip\'s hand closes on the handle. draw_slide clip share: the share\n')
            f.write('# of the clip the game swings the weapon to the hand over, ending there (walk.DRAW_SIZES).\n')
            f.write('draw_timing %.4f\n' % walk.DRAW_AT)
            if hasattr(walk, 'HIP_GRAB'):
                f.write('# hip_timing grab slide: walk.HIP_GRAB, where hip_draw\'s hand closes on the hip gear in its slot, and\n')
                f.write('# walk.HIP_SLIDE, the share of the clip after that the game moves it from the slot into the hand over.\n')
                f.write('hip_timing %.4f %.4f\n' % (walk.HIP_GRAB, walk.HIP_SLIDE))
            for clip, (slot, _) in walk.draw_names().items():
                f.write('draw_slide %s %.4f\n' % (clip, walk.DRAW_SIZES[walk.HOLSTERS[slot]['size']]['slide']))
        f.write('# tool kind model side m00 m01 m02 m03 m10 .. m23: the work_<kind> clip\'s tool, IsoRim\'s Models/<model>.obj, riding\n')
        f.write('# that hand bone: the rows of its place in the rest pose (model space, model units), moved by the bone as posed.\n')
        for kind, model, side, m in tools:
            f.write('tool %s %s %s %s\n' % (kind, model, side, ' '.join('%.5f' % (m[r][c] + 0.0) for r in range(3) for c in range(4))))

def upper_mask(hands, upper=None):
    """walk.UPPER (or upper) for every deforming bone: its own share, or its nearest listed parent's, or none; none either for
    the arm of a side hands (a walk.DRAFTED or walk.CARRY entry) leaves free (shoulder to hand), so it keeps the gait's
    swing."""
    free = [side for side in ('L', 'R') if side not in hands]
    upper = upper or walk.UPPER
    mask = {}
    for n in deform:
        b = rig.data.bones[n]
        while b is not None and b.name not in upper:
            b = b.parent
        mask[n] = upper[b.name] if b is not None else 0.0
        if any(n.split('.')[0] in ('shoulder', 'upper_arm', 'forearm', 'hand') and n.endswith('.' + side) for side in free):
            mask[n] = 0.0
    return mask

# Scale from the standing pose, so the figure is as tall as IsoRim's.
rig.animation_data_create()
rig.animation_data.action = None
walk.apply_pose(walk.stand_pose())
verts, normals, tris = posed_triangles()
top = max(v.z for v in verts)
scale = FIGURE_HEIGHT / top
face_tris, face_uvs, face_middle = find_face(verts, tris)

# The rest pose the weights are bound to: the mesh's own vertices (the Armature modifier is the body's only one).
if any(m.type != 'ARMATURE' for m in body.modifiers):
    raise SystemExit('the body has modifiers besides its Armature; apply them so the skin matches the frames')
if any(m.type == 'ARMATURE' and m.use_deform_preserve_volume for m in body.modifiers):
    print('WARNING: Preserve Volume is on; the game skins linearly, so the skinned figure will differ a little')
deforming = {g.index for g in body.vertex_groups if g.name in rig.data.bones and rig.data.bones[g.name].use_deform}
unweighted = [v.index for v in body.data.vertices if sum(g.weight for g in v.groups if g.group in deforming) <= 1e-6]
if unweighted:   # Blender leaves these at rest while the body moves, so the SKIN check below fails by far
    print('WARNING: %d body vertices have no bone weights (%s)' % (len(unweighted), ', '.join(map(str, unweighted[:20]))))
rest = base_shape(body)
rest_normals = [(body.matrix_world.to_3x3() @ v.normal).normalized() for v in body.data.vertices]
rest_tris = tris
weights = []
for v in body.data.vertices:
    ws = [(bone_index[body.vertex_groups[g.group].name], g.weight) for g in v.groups
          if g.weight > 0 and body.vertex_groups[g.group].name in bone_index]
    total = sum(w for _, w in ws)
    weights.append([(b, w / total) for b, w in ws] if total > 0 else [(bone_index[walk.HIPS], 1.0)])
body_keys = key_deltas(body)
rest_tree = kdtree.KDTree(len(rest))
for i, p in enumerate(rest):
    rest_tree.insert(p, i)
rest_tree.balance()
body_tree = BVHTree.FromPolygons(rest, rest_tris)
write_skin(os.path.join(out, 'skin.txt'))
for g in garments:
    write_garment(g, os.path.join(out, 'garment_%s.txt' % g.name))
# The cat ears and tail: parts with armatures of their own, not garments (export_cat.py).
import export_cat
export_cat.write(out, scale)

# The horns, made from the head in skin.txt (make_horns.py).
import make_horns
make_horns.write(out)

clips = []
masks = {}
write_frame('idle', scale)   # the still pose: idle.obj for the baked figure; the skinned one takes the loop below

def export_cycle(prefix, gait):
    global clip_frames
    walk.set_gait(gait)
    act, worst = walk.bake(prefix)
    clip_frames = []
    for f in range(walk.FRAMES):
        sc.frame_set(f + 1)
        tris = write_frame('%s_%02d' % (prefix, f), scale)
    clips.append((prefix, clip_frames, 0.0))
    # cells an adult covers in one cycle (two steps): armature units -> world -> cells
    return walk.ground_per_cycle() * rig.matrix_world.to_scale().y * scale, worst, len(tris)

ground, worst, count = export_cycle('walk', 0.0)
grounds = [ground]
for level in range(1, RUN_LEVELS + 1):
    g, w, _ = export_cycle('run%d' % level, level / RUN_LEVELS)
    grounds.append(g)
    worst = max(worst, w)


def export_loop(name, at, n, seconds):
    """A clip for the skinned figure only (no OBJ frames): n frames of at round one loop, timed by the clock (seconds) or
    by the ground covered (0). Then each of its walk.VARIANTS as name~2, name~3 and so on, posed with that variant's
    numbers (n and seconds may be functions of nothing, so a variant with another mocap clip has its own). Returns the
    worst overreach."""
    global clip_frames
    worst = 0.0
    for i, numbers in enumerate([{}] + walk.variants(name)):
        clip = name if i == 0 else '%s~%d' % (name, i + 1)
        with walk.varied(numbers):
            frames = n() if callable(n) else n
            walk.set_gait(0.0)
            _, w = walk.bake_loop(clip, at, frames)
            worst = max(worst, w)
            clip_frames = []
            for f in range(frames):
                sc.frame_set(f + 1)
                write_frame(clip, scale, files=False)
            clips.append((clip, clip_frames, seconds() if callable(seconds) else seconds))
    return worst

# Sitting, lying (also the dead) and crawling: the game lays the lying and crawling clips down itself.
# Standing: the idle, and one drafted loop per weapon kind, each with the bones it takes when laid over a gait. A weapon
# posed just as its kind is gets none (the game falls back to the kind's clip).
pose_worst = export_loop('idle', walk.stand_at, walk.loop_frames(walk.IDLE_SECONDS), walk.IDLE_SECONDS)
drafted_clips = [k for k in walk.DRAFTED
                 if 'kind' not in walk.DRAFTED[k] or walk.drafted_entry(k) != walk.drafted_entry(walk.DRAFTED[k]['kind'])]
for kind in drafted_clips:
    pose_worst = max(pose_worst, export_loop('drafted_' + kind, lambda t, k=kind: walk.stand_at(t, k),
                                             walk.loop_frames(walk.DRAFT_SECONDS), walk.DRAFT_SECONDS))
    masks['drafted_' + kind] = upper_mask(walk.drafted_entry(kind))
# Aiming: per aimed weapon (and kind), aim_ and fire_ (and a bow's nock_), one frame per pitch from the lowest aim to the
# highest, laid over the gait from the waist up with both hands taken; the game picks between frames by the pitch.
aim_worst = 0.0
for name in walk.aimed_names():
    for stage in walk.aim_stages(name):
        clip = '%s_%s' % (stage, name)
        aim_worst = max(aim_worst, export_loop(clip, lambda t, n=name, s=stage: walk.aim_at(t, n, s), walk.AIM_PITCHES, 0.0))
        masks[clip] = upper_mask('LR', walk.AIM_UPPER)
# The sprite slab's stances and aims (walk.SLAB_DRAFTED, SLAB_AIMED: as they were before the holds were redone for IsoRim's
# weapon models), as '<clip>_slab'. The game takes a weapon's own slab clip, else its kind's, else the clip it plays with
# the model (WalkFigures.SlabClip), so one is written only where that chain would pose it otherwise. Unarmed is never a
# slab.
if getattr(walk, 'SLAB_DRAFTED', None) is not None:
    def drafted_key(name):
        return repr(walk.drafted_entry(name))

    def aim_key(name):
        return repr((walk.aimed_entry(name), walk.WEAPONS[name]))

    def kinds_first(names):
        return sorted(names, key=lambda k: 'kind' in walk.DRAFTED.get(k, {}))

    model_drafted = {k: drafted_key(k) for k in walk.DRAFTED}
    model_aimed = walk.aimed_names()
    model_aim = {k: aim_key(k) for k in model_aimed}
    slab_drafted, slab_aims = {}, {}
    with walk.varied(walk.slab_numbers()):
        for name in kinds_first(walk.DRAFTED):
            kind = walk.DRAFTED[name].get('kind', name)
            if kind == 'unarmed':
                continue
            own = drafted_key(name)
            fallback = slab_drafted.get(kind) or model_drafted[name if name in drafted_clips else kind]
            if own != fallback:
                slab_drafted[name] = own
                pose_worst = max(pose_worst, export_loop('drafted_' + name + walk.SLAB, lambda t, k=name: walk.stand_at(t, k),
                                                         walk.loop_frames(walk.DRAFT_SECONDS), walk.DRAFT_SECONDS))
                masks['drafted_' + name + walk.SLAB] = upper_mask(walk.drafted_entry(name))
        for name in kinds_first(walk.aimed_names()):
            kind = walk.DRAFTED.get(name, {}).get('kind', name)
            own = aim_key(name)
            fallback = slab_aims.get(kind) or model_aim.get(name if name in model_aimed else kind)
            if own != fallback:
                slab_aims[name] = own
                for stage in walk.aim_stages(name):
                    clip = '%s_%s%s' % (stage, name, walk.SLAB)
                    aim_worst = max(aim_worst, export_loop(clip, lambda t, n=name, s=stage: walk.aim_at(t, n, s),
                                                           walk.AIM_PITCHES, 0.0))
                    masks[clip] = upper_mask('LR', walk.AIM_UPPER)
    print('SLAB drafted %s; aimed %s' % (', '.join(slab_drafted), ', '.join(slab_aims)))
# Striking: one melee blow per kind (and per weapon held too differently to swing in them) and one throw per hand, from
# the guard round to it again, laid over the gait from the waist up with both hands taken. The game plays each by the
# blow's timing, not on the clock.
strikes = walk.strike_names()
for clip in strikes:
    aim_worst = max(aim_worst, export_loop(clip, lambda t, c=clip: walk.strike_at(t, c), walk.STRIKE_FRAMES, 0.0))
    masks[clip] = upper_mask('LR', walk.STRIKE_UPPER)
# Drawing from each holster slot, for each type of weapon that goes in it, laid on from the waist up with only the hand
# that draws taken (the fingers of the other are its own too). The game plays each by how far through the draw it is, and
# backwards to holster it.
draws = walk.draw_names()
draw_worst = {}
for clip in draws:
    draw_worst[clip] = export_loop(clip, lambda t, c=clip: walk.draw_at(t, c), walk.DRAW_FRAMES,
                                   walk.DRAW_SIZES[walk.HOLSTERS[draws[clip][0]]['size']]['seconds'])
    side = walk.draw_keys(clip)['side']
    free = 'L' if side == 'R' else 'R'
    masks[clip] = {n: 0.0 if n.split('.')[0] in ('hand', 'thumb') and n.split('.')[1] == free else v
                   for n, v in upper_mask(side, walk.DRAW_UPPER).items()}
# Carrying: one loop per load, laid over the gait as the drafted stance is (the fireman's left arm keeps the gait's
# swing); the carried person has a loop of its own, in the carrier's rest space.
for kind in walk.CARRY:
    seconds = walk.carry_seconds(kind)   # the play loop (rocking a baby) is longer
    pose_worst = max(pose_worst, export_loop('carry_' + kind, lambda t, k=kind: walk.carry_at(t, k),
                                             walk.loop_frames(seconds), seconds))
    masks['carry_' + kind] = upper_mask(walk.CARRY[kind].get('hands', walk.CARRY[kind]))
    if kind != 'item':
        export_loop('carried_' + kind, lambda t, k=kind: (walk.carried_at(k, t), 0.0),
                    walk.loop_frames(seconds), seconds)
# Hurt: one loop per wounded place and side, from the waist up over whatever the legs do; the arm it leaves free keeps
# the gait's swing.
for kind in walk.hurt_kinds():
    pose_worst = max(pose_worst, export_loop('hurt_' + kind, lambda t, k=kind: walk.hurt_at(t, k),
                                             walk.loop_frames(walk.HURT_SECONDS), walk.HURT_SECONDS))
    masks['hurt_' + kind] = upper_mask(walk.hurt_hands(kind), walk.HURT_UPPER)
# Cowering (FleeAndCower): hunched with both hands drawn up in front of the chest, from the waist up over the run (the legs keep their
# gait); standing still, the whole figure crouched on bent knees as well.
pose_worst = max(pose_worst, export_loop('cower', walk.cower_at, walk.loop_frames(walk.CRY_SECONDS), walk.CRY_SECONDS),
                 export_loop('hunker', walk.hunker_at, walk.loop_frames(walk.CRY_SECONDS), walk.CRY_SECONDS))
masks['cower'] = upper_mask('LR', walk.CRY_UPPER)
# Taking cover: crouched low on the drafted stance's feet, the whole figure, the drafted stance or the aim laid over it from
# the waist up in the game (WalkFigures.CoverOf), at a share by the cover beside the pawn.
if hasattr(walk, 'cover_at'):
    pose_worst = max(pose_worst, export_loop('cover', walk.cover_at, walk.loop_frames(walk.COVER_SECONDS), walk.COVER_SECONDS))
# Leading on a rope: the right arm only, reached a little back, over whatever the rest is doing.
pose_worst = max(pose_worst, export_loop('lead', walk.lead_at, walk.loop_frames(walk.LEAD_SECONDS), walk.LEAD_SECONDS))
masks['lead'] = upper_mask('R', walk.LEAD_UPPER)
# Rubbing the hands for warmth: both arms and the trunk from the waist up, over whatever the legs do.
if getattr(walk, 'RUB_CLIP', None) is not None:
    pose_worst = max(pose_worst, export_loop('rub', walk.rub_at, walk.RUB_CLIP['frames'], walk.RUB_SECONDS))
    masks['rub'] = upper_mask('LR', walk.RUB_UPPER)
# Talking: the same, over the idle, the sit or the walk.
if getattr(walk, 'TALK_CLIP', None) is not None:
    pose_worst = max(pose_worst, export_loop('talk', walk.talk_at, walk.TALK_CLIP['frames'], walk.TALK_SECONDS))
    masks['talk'] = upper_mask('LR', walk.TALK_UPPER)
# Reloading: the same, over the drafted stance (the game stretches it over the reload's time).
if getattr(walk, 'RELOAD_CLIP', None) is not None:
    pose_worst = max(pose_worst, export_loop('reload', walk.reload_at, walk.RELOAD_CLIP['frames'], walk.RELOAD_SECONDS))
    masks['reload'] = upper_mask('LR', walk.RELOAD_UPPER)
# Eating: both arms and the trunk from the waist up, over the sit (at a table) or the idle (the meal in hand).
for name, at in (('eat_sit', getattr(walk, 'eat_sit_at', None)), ('eat_stand', getattr(walk, 'eat_stand_at', None))):
    clip = getattr(walk, name.upper() + '_CLIP', None)
    if clip is not None:
        # As functions, so a variant with another mocap clip (walk.VARIANTS) has its own length.
        pose_worst = max(pose_worst, export_loop(name, at, lambda n=name: getattr(walk, n.upper() + '_CLIP')['frames'],
                                                 lambda n=name: getattr(walk, n.upper() + '_SECONDS')))
        masks[name] = upper_mask('LR', walk.EAT_UPPER)
# Researching sat at the bench: both arms and the trunk from the waist up, over the sit.
if getattr(walk, 'RESEARCH_CLIP', None) is not None:
    pose_worst = max(pose_worst, export_loop('work_research', walk.research_at, walk.RESEARCH_CLIP['frames'], walk.RESEARCH_SECONDS))
    masks['work_research'] = upper_mask('LR', walk.EAT_UPPER)
    # Standing at the bench, the same on the standing pose, the whole figure in place of the idle.
    if hasattr(walk, 'research_stand_at'):
        pose_worst = max(pose_worst, export_loop('work_research_stand', walk.research_stand_at, walk.RESEARCH_CLIP['frames'],
                                                 walk.RESEARCH_SECONDS))
    # With no seat, sat cross-legged on the floor, the whole figure in place of the idle.
    if hasattr(walk, 'research_floor_at') and getattr(walk, 'MEDITATE_CLIP', None) is not None:
        pose_worst = max(pose_worst, export_loop('work_research_floor', walk.research_floor_at, walk.RESEARCH_CLIP['frames'],
                                                 walk.RESEARCH_SECONDS))
# Generic work by where it is done: knelt at the ground (crouched hunched over for sowing) and stood at a bench, the
# whole figure in place of the idle; sat at a bench and sat with no bench, from the waist up over the sit, as
# researching is.
for name in ('crouch', 'sow', 'bench', 'bench_sit', 'chair'):
    clip = getattr(walk, name.upper() + '_CLIP', None)
    if clip is not None:
        # Read at each variant, which may play another mocap clip (sow's bent-over one).
        pose_worst = max(pose_worst, export_loop('work_' + name, getattr(walk, name + '_at'),
                                                 lambda n=name: getattr(walk, n.upper() + '_CLIP')['frames'],
                                                 lambda n=name: getattr(walk, n.upper() + '_SECONDS')))
        if name in ('bench_sit', 'chair'):
            masks['work_' + name] = upper_mask('LR', walk.EAT_UPPER)
# Taming: the right hand held out to the animal palm up (walk.tame_at), stood at a tall one, knelt on the ground work's
# kneel by a low one; the whole figure in place of the idle.
if hasattr(walk, 'tame_at'):
    pose_worst = max(pose_worst, export_loop('work_tame', walk.tame_at, walk.loop_frames(walk.TAME_SECONDS), walk.TAME_SECONDS))
    if getattr(walk, 'CROUCH_CLIP', None) is not None:
        pose_worst = max(pose_worst, export_loop('work_tame_kneel', walk.tame_kneel_at, walk.CROUCH_CLIP['frames'],
                                                 walk.CROUCH_SECONDS))
# Casting: the left hand raised to the target (or reached out to touch it), over whatever the rest is doing; the right arm
# keeps its own (a weapon's stance, or the gait's swing).
pose_worst = max(pose_worst, export_loop('cast', walk.cast_at, walk.loop_frames(walk.CAST_SECONDS), walk.CAST_SECONDS),
                 export_loop('cast_touch', walk.cast_touch_at, walk.loop_frames(walk.CAST_SECONDS), walk.CAST_SECONDS))
masks['cast'] = masks['cast_touch'] = upper_mask('L', walk.CAST_UPPER)
# Hip gear (a psychic lance, orbital targeter or tornado generator): taken from its slot on the right shoulder with the
# left hand (the game plays hip_draw backwards to put it back), held ready with that arm only while drafted, and pointed
# at the target by the pitch while it is used (hip_aim, and hip_fire as it goes off), the trunk and head coming too. It
# rides the left hand as one place in the hand's rest frame (its tool line, below).
if hasattr(walk, 'shield_at'):
    pose_worst = max(pose_worst, export_loop('shield', walk.shield_at, walk.loop_frames(walk.SHIELD_SECONDS), walk.SHIELD_SECONDS))
    masks['shield'] = upper_mask('L', walk.SHIELD_UPPER)
if hasattr(walk, 'hip_hold_at'):
    pose_worst = max(pose_worst, export_loop('hip_hold', walk.hip_hold_at, walk.loop_frames(walk.HIP_SECONDS), walk.HIP_SECONDS))
    masks['hip_hold'] = upper_mask('L', walk.HIP_UPPER)
    for stage, at in (('aim', walk.hip_aim_at), ('fire', walk.hip_fire_at)):
        aim_worst = max(aim_worst, export_loop('hip_' + stage, at, walk.AIM_PITCHES, 0.0))
        masks['hip_' + stage] = upper_mask('L', walk.HIP_AIM_UPPER)
    draw_worst['hip_draw'] = export_loop('hip_draw', walk.hip_draw_at, walk.HIP_DRAW_FRAMES, walk.HIP_DRAW_SECONDS)
    masks['hip_draw'] = upper_mask('L', walk.HIP_AIM_UPPER)
    print('HIP gear: the hand %.4f leg lengths short of the slot' % walk.hip_draw_keys()['gap'])
# Flinching: one jolt per way the figure is pushed, from the plain stance (the first frame) and back; the game adds each
# bone's turn from that first frame on top of any pose (its over line: the bones it nudges), by the ticks since the hit.
for way in walk.FLINCH_WAYS:
    pose_worst = max(pose_worst, export_loop('flinch_' + way, lambda t, w=way: walk.flinch_at(t, w), walk.FLINCH_FRAMES, 0.0))
    masks['flinch_' + way] = upper_mask('LR', walk.FLINCH_UPPER)
# Staggered: a stumble to one knee and back up, once through, its first and last frames standing (the game plays it by
# how far through the stagger the pawn is), the whole figure standing still and from the waist up over the slowed gait.
# Stunned: dazed, the whole figure in place of the idle, from the waist up on the move.
pose_worst = max(pose_worst, export_loop('reel', lambda t: walk.reel_at(t * walk.REEL_FRAMES / (walk.REEL_FRAMES - 1)),
                                         walk.REEL_FRAMES, walk.REEL_SECONDS),
                 export_loop('daze', walk.daze_at, walk.loop_frames(walk.DAZE_SECONDS), walk.DAZE_SECONDS))
masks['reel'] = upper_mask('LR', walk.REEL_UPPER)
masks['daze'] = upper_mask('LR', walk.DAZE_UPPER)
# Jumping: one pass from take-off to landing, its first frame the take-off and its last the landing (the game plays it by
# how far through the flight the pawn is, never round past the end); and the crouch held before it and after, one still
# frame the game eases in and out (its length, LAND_SECONDS, is how long coming up out of a landing takes).
pose_worst = max(pose_worst, export_loop('jump', lambda t: walk.jump_at(t * walk.JUMP_FRAMES / (walk.JUMP_FRAMES - 1)),
                                         walk.JUMP_FRAMES, 0.0),
                 export_loop('land', lambda t: walk.land_pose(), 1, walk.LAND_SECONDS))
# Bending to the ground: one still frame, which the game blends in and out over its length (BEND_SECONDS) when a pawn
# picks something up or sets it down; on the move, bend_walk instead, from the waist up over the stride.
pose_worst = max(pose_worst, export_loop('bend', lambda t: walk.bend_pose(), 1, walk.BEND_SECONDS),
                 export_loop('bend_walk', lambda t: walk.bend_walk_pose(), 1, walk.BEND_SECONDS))
masks['bend_walk'] = upper_mask('LR', walk.BEND_WALK_UPPER)
# Working: one stroke of each tool, the whole figure standing at the work (a seated kind from the waist up), looped on the clock; its tool rides the right
# hand, held the same way all through the stroke, so one place in the hand's rest frame (tool lines) puts it there.
tools = []
tool_drift = 0.0
for kind, w in walk.WORK.items():
    # A kind or variant with a mocap clip (walk.work_mocap) plays that clip, at its own length.
    pose_worst = max(pose_worst, export_loop('work_' + kind, lambda t, k=kind: walk.work_at(t, k),
                                             lambda k=kind: walk.work_mocap(k)['frames'] if walk.work_mocap(k) else WORK_FRAMES,
                                             lambda k=kind: walk.work_seconds(k)))
    if w.get('seated'):
        masks['work_' + kind] = upper_mask('LR', walk.EAT_UPPER)   # sat: laid over the sit from the waist up
    if w['tool'] is None:
        continue
    held = []
    for f in range(WORK_FRAMES):
        X, _ = walk.tool_stroke_at(f / WORK_FRAMES, kind)
        held.append(X['hand.R'].inverted() @ walk.tool_matrix(kind, walk.work_key(w, walk.work_stroke(f / WORK_FRAMES, w))))
    tool_drift = max(tool_drift, max((m.translation - held[0].translation).length for m in held) * rig.matrix_world.to_scale().x * scale)
    # From the tool's own model space (cells, as IsoRim loads it) to the rest pose's, in the game's axes.
    game = Matrix.Scale(scale, 4) @ GAME @ rig.matrix_world @ held[0] @ GAME.inverted()
    tools.append((kind, w['tool'], 'R', game))
    # A variant holds the tool in the hand as the stroke does (the same place in the hand's rest frame), unless it puts
    # it down (WORK_MOCAP_TOOL); its tool line is named as its clip is, less work_.
    for i, numbers in enumerate(walk.variants('work_' + kind)):
        if numbers.get('WORK_MOCAP_TOOL', True):
            tools.append(('%s~%d' % (kind, i + 2), w['tool'], 'R', game))
# The hip gear in the left hand, as walk.hip_rest holds it (the model is the gear worn, so the line names none).
if hasattr(walk, 'hip_rest'):
    tools.append(('hip', 'gear', 'L', Matrix.Scale(scale, 4) @ GAME @ rig.matrix_world @ walk.hip_rest() @ GAME.inverted()))
# Kneeling at work on the ground (walk.pluck_at), from the mocap clip, with bare hands; a kind of its own for the game.
if walk.PLUCK_CLIP is not None:
    pose_worst = max(pose_worst, export_loop('work_pluck', walk.pluck_at, walk.PLUCK_CLIP['frames'], walk.PLUCK_SECONDS))
# Kneeling by a patient (walk.tend_at), the mocap kneel as it is.
if walk.TEND_CLIP is not None:
    pose_worst = max(pose_worst, export_loop('work_tend', walk.tend_at, walk.TEND_CLIP['frames'], walk.TEND_SECONDS))
    # Playing with a baby on the floor by a toy box (walk.toys_at): the same kneel, a hand waved over it.
    if getattr(walk, 'MEDITATE_CLIP', None) is not None and 'play' in walk.CARRY:
        # Sat cross-legged rocking the baby (walk.play_sit_at), on the play carry's clock.
        pose_worst = max(pose_worst, export_loop('work_play', walk.play_sit_at, walk.loop_frames(walk.carry_seconds('play')),
                                                 walk.carry_seconds('play')))
    pose_worst = max(pose_worst, export_loop('work_toys', walk.toys_at, walk.TEND_CLIP['frames'], walk.TEND_SECONDS))
# Sat cross-legged meditating (walk.meditate_at), the mocap sit as it is; a work kind for the game, as it sits at a spot.
if getattr(walk, 'MEDITATE_CLIP', None) is not None:
    pose_worst = max(pose_worst, export_loop('work_meditate', walk.meditate_at, walk.MEDITATE_CLIP['frames'], walk.MEDITATE_SECONDS))
# Bent over vomiting (walk.vomit_at), the mocap pant with a heave; a work kind for the game, as it stands at a cell.
if getattr(walk, 'VOMIT_CLIP', None) is not None:
    pose_worst = max(pose_worst, export_loop('work_vomit', walk.vomit_at, walk.VOMIT_CLIP['frames'], walk.VOMIT_SECONDS))
# Getting up, once through from lying on the back to standing, its last frame the standing one (the game plays it by how
# far through it is, never round past the end).
if getattr(walk, 'GETUP_CLIP', None) is not None:
    def getup_frames():
        return walk.GETUP_CLIP['frames']
    pose_worst = max(pose_worst, export_loop('getup', lambda t: walk.getup_at(t * getup_frames() / (getup_frames() - 1)),
                                             getup_frames, lambda: walk.GETUP_SECONDS))
# Diving from a grenade: the dive to the ground, once through (the game plays it backwards to get up), and the loop lying on
# the front; and crouched low under fire (hunkering). Each by the hand that holds the weapon, posed where they are, not made upright.
if hasattr(walk, 'dive_at'):
    for side in ('R', 'L'):
        pose_worst = max(pose_worst,
                         export_loop('dive_' + side, lambda t, s=side: walk.dive_at(t * walk.DIVE_FRAMES / (walk.DIVE_FRAMES - 1), s),
                                     walk.DIVE_FRAMES, walk.DIVE_SECONDS),
                         export_loop('prone_' + side, lambda t, s=side: walk.prone_at(t, s), walk.loop_frames(walk.PRONE_SECONDS), walk.PRONE_SECONDS),
                         export_loop('duck_' + side, lambda t, s=side: walk.duck_at(t, s), walk.duck_frames(), walk.DUCK_SECONDS))
# Stop, drop and roll (ExtinguishSelf): down by the same dive, then rolled over each way, posed where it lies.
if hasattr(walk, 'roll_at'):
    pose_worst = max(pose_worst, export_loop('roll', lambda t: walk.roll_at(t), walk.roll_frames(), walk.ROLL_SECONDS))
# Lying from mocap clips, made upright as the lie is (the game lays them down the same way): asleep, downed in bed and
# downed on the ground; a corpse (one frame); and the two falls from standing, once through, each ending as the loop it
# falls into (walk.FALL_INTO) begins.
for name, clip, at in (('sleep', walk.SLEEP_CLIP, walk.sleep_at), ('downed', walk.DOWNED_CLIP, walk.downed_at),
                       ('downed_side', walk.DOWNED_SIDE_CLIP, walk.downed_side_at)):
    if clip is not None:
        pose_worst = max(pose_worst, export_loop(name, at, clip['frames'], clip['frames'] / clip['fps']))
# A baby awake on its back, waving and kicking (arms posed by hand and the crawl's legs on the lie).
if hasattr(walk, 'baby_at') and walk.CRAWL_CLIP is not None:
    pose_worst = max(pose_worst, export_loop('baby', walk.baby_at, walk.loop_frames(walk.BABY_SECONDS), walk.BABY_SECONDS))
if walk.DIE_CLIP is not None:
    pose_worst = max(pose_worst, export_loop('dead', walk.dead_at, 1, 1.0))
# The ragdoll's neutral pose (one frame): what a limp body's joints are pulled toward (Ragdoll.cs), by each bone's turn on
# its parent's only.
pose_worst = max(pose_worst, export_loop('ragdoll', lambda t: walk.ragdoll_pose(), 1, 1.0))
for name in ('knockdown', 'die'):
    if walk.fall_clip(name) is not None and (walk.FALL_INTO[name] != 'downed_side' or walk.DOWNED_SIDE_CLIP is not None):
        # By functions, so a variant with another mocap clip has its own length.
        def fall_frames(k=name):
            return walk.fall_clip(k)['frames']
        pose_worst = max(pose_worst, export_loop(name, lambda t, n=fall_frames, k=name: walk.fall_at(t * n() / (n() - 1), k), fall_frames,
                                                 lambda n=fall_frames, k=name: n() / walk.fall_clip(k)['fps']))
pose_worst = max(pose_worst, export_loop('sit', walk.sit_at, walk.loop_frames(walk.SIT_SECONDS), walk.SIT_SECONDS),
                 export_loop('lie', walk.lie_at, walk.loop_frames(walk.LIE_SECONDS), walk.LIE_SECONDS),
                 export_loop('crawl', walk.crawl_at, walk.CRAWL_FRAMES, 0.0))
# Walking with a bad leg and hopping with one missing (one clip per side) and through water, by the ground covered like the walk; swimming laid down
# by the game as the crawl is, and treading water on the clock.
pose_worst = max(pose_worst, export_loop('limp_L', lambda t: walk.limp_at(t, 'L'), walk.FRAMES, 0.0),
                 export_loop('limp_R', lambda t: walk.limp_at(t, 'R'), walk.FRAMES, 0.0),
                 export_loop('hop_L', lambda t: walk.hop_at(t, 'L'), walk.FRAMES, 0.0),
                 export_loop('hop_R', lambda t: walk.hop_at(t, 'R'), walk.FRAMES, 0.0),
                 export_loop('wade', walk.wade_at, walk.FRAMES, 0.0),
                 export_loop('swim', walk.swim_at, walk.SWIM_FRAMES, 0.0),
                 export_loop('tread', walk.tread_at, walk.loop_frames(walk.TREAD_SECONDS), walk.TREAD_SECONDS))
cells = rig.matrix_world.to_scale().y * scale   # armature units -> cells
crawl_ground = walk.crawl_ground_per_cycle() * cells
limp_ground, hop_ground, wade_ground, swim_ground = (walk.limp_ground_per_cycle() * cells, walk.hop_ground_per_cycle() * cells,
                                                     walk.wade_ground_per_cycle() * cells, walk.swim_ground_per_cycle() * cells)

with open(os.path.join(out, MTL), 'w') as f:
    f.write('newmtl %s\nKd 1.000 1.000 1.000\n' % MATERIAL)
with open(os.path.join(out, 'clip.txt'), 'w') as f:
    f.write('# Read by the game (WalkFigures.cs). Written by anim_scripts/export_frames.py.\n')
    f.write('frames %d\n' % walk.FRAMES)
    f.write('ground_per_cycle %.4f\n' % ground)   # cells an adult covers in one walk cycle (two steps)
    f.write('run_levels %d\n' % RUN_LEVELS)
    f.write('run_ground_per_cycle %s\n' % ' '.join('%.4f' % g for g in grounds[1:]))   # the same for run1 .. runN
    f.write('crawl_ground_per_cycle %.4f\n' % crawl_ground)   # and for anim.txt's crawl
    f.write('limp_ground_per_cycle %.4f\n' % limp_ground)   # limp_L and limp_R
    f.write('hop_ground_per_cycle %.4f\n' % hop_ground)   # hop_L and hop_R
    f.write('wade_ground_per_cycle %.4f\n' % wade_ground)
    f.write('swim_ground_per_cycle %.4f\n' % swim_ground)
write_anim(os.path.join(out, 'anim.txt'), clips)
print('SKIN %s: %d vertices, %d bones, keys %s; skinning matches Blender within %.5f cells'
      % (body.name, len(rest), len(deform), ', '.join(body_keys) or 'none', skin_worst))
if not CHECK_FITS:
    print('FIT not checked (export with --fit to see how far each garment sinks into the body)')
for fit in garment_fits if CHECK_FITS else []:
    print('FIT %s: in every frame, sinks at most %.4f cells into the body (stands %.4f to %.4f off it at rest)'
          % (fit['name'], fit['sink'], min(fit['gap']) * scale, max(fit['gap']) * scale))
print('STAND idle over %.1f s; drafted %s over %.1f s each, laid over the gait from %s (a free arm keeps the gait\'s)'
      % (walk.IDLE_SECONDS, ', '.join(drafted_clips), walk.DRAFT_SECONDS, ', '.join('%s %.1f' % kv for kv in walk.UPPER.items())))
print('AIM %s, %d pitches %.0f .. %.0f degrees; overreach %.4f' % (', '.join(walk.aimed_names()), walk.AIM_PITCHES,
                                                                 -walk.AIM_PITCH, walk.AIM_PITCH, aim_worst))
print('STRIKE %s, %d frames each, the hit at %.2f; overreach (with the aim) %.4f' % (', '.join(strikes), walk.STRIKE_FRAMES,
                                                                                  walk.STRIKE_AT[1], aim_worst))
for clip in draws:
    g = walk.draw_keys(clip)
    print('DRAW %s (%s): trunk twist %.0f lean %.0f side %.0f, shoulder %.0f; the handle %.3f leg lengths out of reach for the game to swing to the hand; overreach %.4f'
          % (clip, g['guard'], g['body']['twist'], g['body']['lean'], g['body']['side'], g['shoulder'], g['gap'], draw_worst[clip]))
print('CARRY %s, carried %s' % (', '.join('%s %.1f s' % (k, walk.carry_seconds(k)) for k in walk.CARRY), ', '.join(k for k in walk.CARRY if k != 'item')))
print('HURT %s over %.1f s each, laid over the gait from the waist up' % (', '.join(walk.hurt_kinds()), walk.HURT_SECONDS))
print('COWER cower and hunker over %.1f s, cower laid over the gait from the waist up' % walk.CRY_SECONDS)
if hasattr(walk, 'cover_at'):
    print('COVER over %.1f s, hips down %.2f leg lengths, the drafted stance laid over it from the waist up' % (walk.COVER_SECONDS, walk.COVER_DROP))
print('LEAD over %.1f s, laid over the gait on the right arm only' % walk.LEAD_SECONDS)
if getattr(walk, 'RUB_CLIP', None) is not None:
    print('RUB %d frames over %.2f s from %s (frames %d .. %d), laid over the gait from the waist up'
          % (walk.RUB_CLIP['frames'], walk.RUB_SECONDS, walk.RUB_CLIP['source'], walk.RUB_CLIP['first'], walk.RUB_CLIP['last']))
if getattr(walk, 'TALK_CLIP', None) is not None:
    print('TALK %d frames over %.2f s from %s (frames %d .. %d), laid over the gait or the sit from the waist up'
          % (walk.TALK_CLIP['frames'], walk.TALK_SECONDS, walk.TALK_CLIP['source'], walk.TALK_CLIP['first'], walk.TALK_CLIP['last']))
if getattr(walk, 'RELOAD_CLIP', None) is not None:
    print('RELOAD %d frames over %.2f s from %s (frames %d .. %d), laid over the drafted stance from the waist up'
          % (walk.RELOAD_CLIP['frames'], walk.RELOAD_SECONDS, walk.RELOAD_CLIP['source'], walk.RELOAD_CLIP['first'], walk.RELOAD_CLIP['last']))
for name in ('eat_sit', 'eat_stand'):
    clip = getattr(walk, name.upper() + '_CLIP', None)
    if clip is not None:
        print('%s %d frames over %.2f s from %s (frames %d .. %d), laid over the %s from the waist up'
              % (name.upper(), clip['frames'], clip['frames'] / clip['fps'], clip['source'], clip['first'], clip['last'],
                 'sit' if name == 'eat_sit' else 'idle'))
print('CAST cast and cast_touch over %.1f s, laid over the gait with the left arm; FLINCH %s, %d frames each, added on top'
      % (walk.CAST_SECONDS, ', '.join('flinch_' + w for w in walk.FLINCH_WAYS), walk.FLINCH_FRAMES))
print('REEL over %.1f s, a stumble to one knee, from the waist up on the move; DAZE over %.1f s' % (walk.REEL_SECONDS, walk.DAZE_SECONDS))
print('WORK %s, %d frames a stroke; tools held within %.4f cells of one place in the hand; %.4f model units a leg length (walk.MODEL_LEG %.4f)'
      % (', '.join('%s %.2f s' % (k, walk.work_seconds(k)) for k in walk.WORK), WORK_FRAMES, tool_drift,
         walk.LEG * rig.matrix_world.to_scale().x * scale, walk.MODEL_LEG))
if walk.PLUCK_CLIP is not None:
    print('PLUCK %d frames over %.2f s from %s (frames %d .. %d), leant %.0f degrees right, %d paws a loop'
          % (walk.PLUCK_CLIP['frames'], walk.PLUCK_SECONDS, walk.PLUCK_CLIP['source'], walk.PLUCK_CLIP['first'],
             walk.PLUCK_CLIP['last'], walk.PLUCK_LEAN, walk.PLUCK_PAWS))
if walk.TEND_CLIP is not None:
    print('TEND %d frames over %.2f s from %s (frames %d .. %d)'
          % (walk.TEND_CLIP['frames'], walk.TEND_SECONDS, walk.TEND_CLIP['source'], walk.TEND_CLIP['first'], walk.TEND_CLIP['last']))
if getattr(walk, 'MEDITATE_CLIP', None) is not None:
    print('MEDITATE %d frames over %.2f s from %s (frames %d .. %d)'
          % (walk.MEDITATE_CLIP['frames'], walk.MEDITATE_SECONDS, walk.MEDITATE_CLIP['source'], walk.MEDITATE_CLIP['first'],
             walk.MEDITATE_CLIP['last']))
if getattr(walk, 'VOMIT_CLIP', None) is not None:
    print('VOMIT %d frames over %.2f s from %s (frames %d .. %d), a heave of %.0f/%.0f degrees a loop'
          % (walk.VOMIT_CLIP['frames'], walk.VOMIT_SECONDS, walk.VOMIT_CLIP['source'], walk.VOMIT_CLIP['first'],
             walk.VOMIT_CLIP['last'], walk.VOMIT_HEAVE[0], walk.VOMIT_HEAVE[1]))
print('JUMP %d frames over the flight; land over %.1f s' % (walk.JUMP_FRAMES, walk.LAND_SECONDS))
for name in ('sleep', 'downed', 'downed_side', 'knockdown', 'die', 'electrocuted', 'shot_fall', 'falling_down', 'chop', 'gather', 'inspect', 'crouch', 'sow', 'bendsow',
             'bench', 'bench_sit', 'chair'):
    clip = walk.clip_file(name)
    if clip is not None:
        print('MOCAP %s: %d frames over %.2f s from %s (%s, frames %d .. %d)'
              % (name, clip['frames'], clip['frames'] / clip['fps'], clip['source'], clip['action'], clip['first'], clip['last']))
print('VARIANTS %s' % ', '.join(n for n, _, _ in clips if '~' in n))
print('POSES idle, drafted, bend, sit, lie and crawl (%d frames, %.3f cells per cycle); overreach %.4f' % (walk.CRAWL_FRAMES, crawl_ground, pose_worst))
print('MOVES limp %.3f, hop %.3f, wade %.3f, swim %.3f cells per cycle; tread over %.1f s' % (limp_ground, hop_ground, wade_ground, swim_ground, walk.TREAD_SECONDS))
print('EXPORT %d walk frames, %d run levels + idle, %d triangles each (face %d), scale %.4f, ground per cycle %s cells, overreach %.4f'
      % (walk.FRAMES, RUN_LEVELS, count, len(face_tris), scale, ' '.join('%.3f' % g for g in grounds), worst))
