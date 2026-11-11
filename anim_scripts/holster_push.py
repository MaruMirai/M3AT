# Where the game puts holstered weapons, backpacks and worn gear once they have pushed each other out of the way, for the
# preview tool: Source/Holsters.cs and Source/WornGears.cs (SkinnedFigure's Clearance, PackRigOf, HolsterAt, PackSide,
# HolsterClear, HolsterOver, PackLift, GearAt ...) in Python, on the same exported files the game reads. All of it is at
# rest, in the game's model space, in model units, as there: what comes out is a place before the bone's turn. When those
# change, change this. Numpy only, so it can be checked against the C# outside Blender.
import os
from collections import namedtuple
import numpy as np

HOLSTER_REACH = 0.05    # SkinnedFigure.HolsterReach: model units, how far to the side of a slot's line out a surface counts
HOLSTER_DEPTH = 0.15    # HolsterDepth: and how far along it, in and out from the slot
SAME_WAY_OUT = 0.9      # SameWayOut: cosine, two slots' ways out this close count as the same way
BIG_HALF_LENGTH = 0.25  # BigHalfLength: how far along its aim either way a big slot's weapon reaches
PACK_LOOK = 0.8         # PackLook: of a backpack's height and width, how far out across its face Clearance looks
PACK_FACE = 0.5         # PackFace: of a backpack's depth, the inner part its face is measured over
SIDE_FAR = 0.08         # SideFar: how far out from a slot beside a pack a surface counts
OVER_PACK_MIDDLE = 0.03 # OverPackMiddle: how far above a pack's middle a weapon lying on it goes
ALONG_BIG = (0.0, -0.5, 0.5, -1.0, 1.0)   # AlongBig: where Clearance looks along a big slot's weapon, shares of that
GEAR_BELT_WIDTH = 0.19  # GearBeltWidth: IsoRim's belt ring's half width
GEAR_GAP = 0.006        # GearGap: left between a pack or belt and what is under it
BELT_ARM_MOST = 0.5     # BeltArmMost: a vertex weighted more than this to the arms is an arm's, not the waist's
BELT_REACH = 0.4        # BeltReach: how far out the belt looks for a surface
ARM_BONES = ('shoulder.', 'upper_arm.', 'forearm.', 'hand.', 'thumb.')   # ArmBones
ISORIM_WAIST = 0.30     # IsoRimWaist: the height of the reference point in IsoRim's models
ISORIM_PACK_WIDE = 0.13    # IsoRimPackWide, and the rest: IsoRim's packs measured, as WornGears.cs has them
ISORIM_PACK_SIDE = 0.18
ISORIM_PACK_DEEP = 0.19
ISORIM_PACK_TALL = 0.40
ISORIM_PACK_MIDDLE = 0.226
SLAB_HALF_DEPTH = 0.025 # WalkFigures.SlabHalfDepth: cells, half of IsoRim's slab
STACK_GAP = 0.005       # WalkFigures.StackGap: model units left between two weapons lying on each other
NO_CLEARANCE_DEFS = ("Apparel_Bandolier", "CE_Apparel_TacVest")   # SkinnedFigure.NoClearanceDefs: webbing lain over, not pushed out by

# What a figure has on (Posed.Wearing): the body's keys as ((name, amount), ...), the garments worn by name, and whether an
# equipment pack is drawn on its back. Grown and of the standard size in the preview, so both pack scales are 1. gear_belt:
# whether a belt is drawn on its waist (WalkFigures.OnBeltSlots), which only says which slots are used.
Worn = namedtuple('Worn', 'mix mask gear_pack pack_scale backpack_scale gear_belt')
Worn.__new__.__defaults__ = (1.0, 1.0, False)

# A place something is carried from: Holster's bone (a number), at, aim and outward in the game's space, its size, pack
# (1: only with a pack; -1: only without; 0: either) and spots (where Clearance looks, from at, or None).
Slot = namedtuple('Slot', 'name size bone at aim outward pack spots')

def game_dir(v):
    """export_frames' game_dir: a place or way in the rig's axes (x left, y back, z up) in the game's."""
    return np.array((-v[0], v[2], -v[1]), dtype=float)

def game_turn(degrees):
    """walk.GEAR's 'turn' (degrees about the rig's axes, x first, then y, then z) as a 3 by 3 in the game's axes."""
    x, y, z = np.radians(degrees)
    cx, sx, cy, sy, cz, sz = np.cos(x), np.sin(x), np.cos(y), np.sin(y), np.cos(z), np.sin(z)
    turn = np.array(((cz, -sz, 0.0), (sz, cz, 0.0), (0.0, 0.0, 1.0))) @ np.array(((cy, 0.0, sy), (0.0, 1.0, 0.0), (-sy, 0.0, cy))) \
        @ np.array(((1.0, 0.0, 0.0), (0.0, cx, -sx), (0.0, sx, cx)))
    axes = np.array(((-1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, -1.0, 0.0)))
    return axes @ turn @ axes.T

def unit(v):
    v = np.asarray(v, dtype=float)
    n = np.linalg.norm(v)
    return v / n if n > 0.0 else v

def outer(verts, tris, point, way, far=HOLSTER_DEPTH, only=None):
    """SkinnedFigure.Outer: the furthest out along way that a surface stands on the line through point, measured from
    point: any vertex within HOLSTER_REACH of the line (of those only keeps, if given), or a triangle it passes through.
    Minus infinity for none."""
    best = -np.inf
    d = verts - point
    along = d @ way
    near = (along >= -HOLSTER_DEPTH) & (along <= far)
    if only is not None:
        near &= only
    if near.any():
        side = d[near] - np.outer(along[near], way)
        hit = (side * side).sum(axis=1) <= HOLSTER_REACH * HOLSTER_REACH
        if hit.any():
            best = along[near][hit].max()
    if len(tris):
        a = verts[tris[:, 0]]
        e1, e2 = verts[tris[:, 1]] - a, verts[tris[:, 2]] - a
        side = np.cross(way, e2)
        det = (e1 * side).sum(axis=1)
        ok = np.abs(det) >= 1e-9
        d = point - a[ok]
        det = det[ok]
        u = (d * side[ok]).sum(axis=1) / det
        up = np.cross(d, e1[ok])
        v = up @ way / det
        along = (e2[ok] * up).sum(axis=1) / det
        hit = (u >= 0.0) & (v >= 0.0) & (u + v <= 1.0) & (along <= far) & (along >= -HOLSTER_DEPTH)
        if hit.any():
            best = max(best, along[hit].max())
    return best

def quaternion(m):
    """The turn of a 3x3 matrix (no size) as (w, x, y, z)."""
    w = np.sqrt(max(0.0, 1.0 + m[0, 0] + m[1, 1] + m[2, 2])) / 2
    x = np.copysign(np.sqrt(max(0.0, 1.0 + m[0, 0] - m[1, 1] - m[2, 2])) / 2, m[2, 1] - m[1, 2])
    y = np.copysign(np.sqrt(max(0.0, 1.0 - m[0, 0] + m[1, 1] - m[2, 2])) / 2, m[0, 2] - m[2, 0])
    z = np.copysign(np.sqrt(max(0.0, 1.0 - m[0, 0] - m[1, 1] + m[2, 2])) / 2, m[1, 0] - m[0, 1])
    q = np.array((w, x, y, z))
    return q / np.linalg.norm(q)

def turn_of(q):
    """A (w, x, y, z) turn as a 3x3 matrix."""
    w, x, y, z = q
    return np.array(((1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
                     (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
                     (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y))))

def between(a, b, u):
    """WalkFigures.Between for 4x4 matrices of a turn and a place: u of the way from a to b, the turn slerped."""
    qa, qb = quaternion(a[:3, :3]), quaternion(b[:3, :3])
    if qa @ qb < 0:
        qb = -qb
    d = min(1.0, qa @ qb)
    angle = np.arccos(d)
    q = qa if angle < 1e-6 else (np.sin((1 - u) * angle) * qa + np.sin(u * angle) * qb) / np.sin(angle)
    m = np.identity(4)
    m[:3, :3] = turn_of(q / np.linalg.norm(q))
    m[:3, 3] = a[:3, 3] * (1 - u) + b[:3, 3] * u
    return m

class Piece:
    """The body or a garment as exported: rest places, triangles, weights, each key's moves."""
    def __init__(self, path):
        verts, tris, wv, wb, ww, defs = [], [], [], [], [], []
        self.keys, self.bone_keys = {}, {}
        moves = bones = None
        weighted = 0
        self.held = False     # a shield (its held line): in no clearance, as SkinnedFigure.HeldMask
        self.held_at = None   # and carried on the back: its held_at line, rest on the back to rest in the left fist
        self.held_grip = None   # and its held_grip line: rest on the back to rest on hand.L as shield_draw takes it by its edge
        self.held_rim = None    # and its held_rim line: that edge (model space)
        with open(path) as f:
            for line in f:
                w = line.split()
                if not w:
                    continue
                if w[0] == 'v':
                    verts.append((float(w[1]), float(w[2]), float(w[3])))
                elif w[0] == 'w':
                    for b, x in zip(w[1::2], w[2::2]):   # the nth w line is the nth vertex's
                        wv.append(weighted)
                        wb.append(int(b))
                        ww.append(float(x))
                    weighted += 1
                elif w[0] == 'f':
                    tris.append((int(w[1]), int(w[2]), int(w[3])))
                elif w[0] == 'defs':
                    defs += w[1:]
                elif w[0] == 'held':
                    self.held = w[1] == 'yes'
                elif w[0] == 'held_at' and len(w) > 12:
                    self.held_at = np.identity(4)
                    self.held_at[:3, :] = np.array([float(x) for x in w[1:13]]).reshape(3, 4)
                elif w[0] == 'held_grip' and len(w) > 12:
                    self.held_grip = np.identity(4)
                    self.held_grip[:3, :] = np.array([float(x) for x in w[1:13]]).reshape(3, 4)
                elif w[0] == 'held_rim' and len(w) > 3:
                    self.held_rim = np.array([float(x) for x in w[1:4]])
                elif w[0] == 'key':
                    moves = self.keys.setdefault(w[1], {})
                    bones = self.bone_keys.setdefault(w[1], {})
                elif w[0] == 'd':
                    moves[int(w[1])] = (float(w[2]), float(w[3]), float(w[4]))
                elif w[0] == 'db':
                    bones[int(w[1])] = np.array((float(w[2]), float(w[3]), float(w[4])))
        self.rest = np.array(verts, dtype=float).reshape(-1, 3)
        self.tris = np.array(tris, dtype=int).reshape(-1, 3)
        self.defs = defs
        self.weight_vert, self.weight_bone, self.weight = np.array(wv, dtype=int), np.array(wb, dtype=int), np.array(ww, dtype=float)
        for name, d in self.keys.items():
            full = np.zeros_like(self.rest)
            if d:
                full[list(d)] = list(d.values())
            self.keys[name] = full

    def shaped(self, mix):
        """SkinnedFigure.Mixed: the rest places with the keys in mix; a key this piece hasn't leaves it as it is."""
        out = self.rest.copy()
        for name, amount in mix:
            if name in self.keys:
                out += self.keys[name] * amount
        return out

class Figure:
    """The export in folder, and where it puts things. packs: walk.HOLSTER_PACKS. gear_slots: mount -> Slot, with each
    gear's scale and belt depth in gear_scale and gear_depth; set by set_gear."""
    def __init__(self, folder, packs=()):
        self.folder = folder
        self.packs = tuple(packs)
        self.body = Piece(os.path.join(folder, 'skin.txt'))
        self.garment_files = {}
        with open(os.path.join(folder, 'skin.txt')) as f:
            for line in f:
                if line.startswith('garment '):
                    _, name, rest = line.rstrip('\n').split(' ', 2)
                    self.garment_files[name] = rest
        self.names = list(self.garment_files)   # the game's order: WornPack takes the first
        self.pieces = {}
        self.bone_names, self.bone_index = [], {}
        heads = []
        with open(os.path.join(folder, 'anim.txt')) as f:
            for line in f:
                if line.startswith('bone '):
                    w = line.split()
                    self.bone_index[w[1]] = len(self.bone_names)
                    self.bone_names.append(w[1])
                    heads.append((float(w[2]), float(w[3]), float(w[4])))
                elif self.bone_names and not line.startswith('#'):
                    break
        self.hips = np.array(heads[0])
        self.gear_slots, self.gear_scale, self.gear_depth = {}, {}, {}
        self.cache, self.rigs = {}, {}

    def piece(self, name):
        p = self.pieces.get(name)
        if p is None:
            p = self.pieces[name] = Piece(os.path.join(self.folder, self.garment_files[name]))
        return p

    def unclear(self, name):
        """Whether a garment is in no clearance (SkinnedFigure.UnclearMask): a pack, a shield or NO_CLEARANCE_DEFS webbing."""
        piece = self.piece(name)
        return self.is_pack(name) or piece.held or any(d in NO_CLEARANCE_DEFS for d in piece.defs)

    def is_pack(self, name):
        """Whether a garment is drawn as a pack (SkinnedFigure.IsPack)."""
        return any(d in self.packs for d in self.piece(name).defs)

    def shaped(self, name, mix):
        key = ('shaped', name, mix)
        out = self.cache.get(key)
        if out is None:
            out = self.cache[key] = self.piece(name).shaped(mix)
        return out

    def body_shaped(self, mix):
        key = ('body', mix)
        out = self.cache.get(key)
        if out is None:
            out = self.cache[key] = self.body.shaped(mix)
        return out

    def bone_move(self, bone, mix):
        """Shape.boneMove: how far the shape moves a bone's head from the base skeleton."""
        move = np.zeros(3)
        for name, amount in mix:
            d = self.body.bone_keys.get(name, {}).get(bone)
            if d is not None:
                move = move + d * amount
        return move

    def slot(self, name, h, melee_at=None):
        """A walk.HOLSTERS entry as the export's holster line has it, in the game's space."""
        return Slot(name, h['size'], self.bone_index[h['bone']], self.hips + game_dir(h['at']), unit(game_dir(h['aim'])),
                    unit(game_dir(h.get('out', (0.0, 1.0, 0.0)))), {True: 1, False: -1}.get(h.get('pack'), 0), None)

    def set_gear(self, gear):
        """walk.GEAR as the export's gear lines have it (ReadGear)."""
        self.gear_slots, self.gear_scale, self.gear_depth, self.gear_turn = {}, {}, {}, {}
        for mount, g in gear.items():
            turn = game_turn(g.get('turn', (0.0, 0.0, 0.0)))
            self.gear_turn[mount] = turn
            spots = pack_spots(g['scale']) @ turn.T if mount == 'pack' else None   # the back plane turns with the pack
            self.gear_slots[mount] = Slot('gear_' + mount, 'gear', self.bone_index[g['bone']], self.hips + game_dir(g['at']), np.zeros(3),
                                          unit(game_dir(g['out'])), 0, spots)
            self.gear_scale[mount], self.gear_depth[mount] = g['scale'], g.get('depth', 1.0)
        self.rigs = {}   # a backpack's rig rides the pack slot

    # ---- Clearance

    def clearance(self, mix, mask, h):
        """SkinnedFigure.Clearance: how far a slot is pushed out: how much further out the outermost surface near it stands,
        the shape's body or any garment in mask but a pack or a shield, than the bare base body's does where the slot was set."""
        mask = tuple(g for g in mask if not self.unclear(g))
        key = ('push', mix, mask, h.size, h.bone, tuple(h.at), tuple(h.aim), tuple(h.outward), None if h.spots is None else h.spots.tobytes())
        push = self.cache.get(key)
        if push is not None:
            return push
        body = self.body
        moved = self.bone_move(h.bone, mix)
        before = outer(body.rest, body.tris, h.at, h.outward)
        stand_off = min(before, 0.0)
        push = 0.0
        spots = len(h.spots) if h.spots is not None else 5 if h.size == 'big' else 1
        for i in range(spots):
            at = h.at + (h.spots[i] if h.spots is not None else h.aim * (BIG_HALF_LENGTH * ALONG_BIG[i]))
            bare = before if i == 0 else max(outer(body.rest, body.tris, at, h.outward), stand_off)
            most = outer(self.body_shaped(mix), body.tris, at + moved, h.outward)
            for g in mask:
                most = max(most, outer(self.shaped(g, mix), self.piece(g).tris, at + moved, h.outward))
            if not np.isneginf(most) and not np.isneginf(bare):
                push = max(push, most - bare)
        self.cache[key] = push
        return push

    # ---- The belt (BeltPushes)

    def waist_of(self, name):
        """SkinnedFigure.WaistOf: (keep, tris): a piece's vertices not weighted more than BELT_ARM_MOST to the arms, and its
        triangles with all three corners among them. name None: the body."""
        key = ('waist', name)
        out = self.cache.get(key)
        if out is None:
            piece = self.body if name is None else self.piece(name)
            arm = np.array([n.startswith(ARM_BONES) for n in self.bone_names] + [False])
            on_arm = np.zeros(len(piece.rest))
            if len(piece.weight_vert):
                np.add.at(on_arm, piece.weight_vert, piece.weight * arm[piece.weight_bone])
            keep = on_arm <= BELT_ARM_MOST
            tris = piece.tris[keep[piece.tris].all(axis=1)] if len(piece.tris) else piece.tris
            out = self.cache[key] = (keep, tris)
        return out

    def belt_pushes(self, mix, mask, h):
        """SkinnedFigure.BeltPushes: how much further out than the bare base body's the waist stands at the belt's height:
        out of the slot's side, the other side, along +z and along -z."""
        mask = tuple(g for g in mask if not self.unclear(g))
        key = ('belt', mix, mask, h.bone, tuple(h.at), tuple(h.outward))
        out = self.cache.get(key)
        if out is not None:
            return out
        middle = np.array((0.0, h.at[1], h.at[2]))
        starts = (h.at, np.array((-h.at[0], h.at[1], h.at[2])), middle, middle)
        ways = (h.outward, np.array((-h.outward[0], h.outward[1], h.outward[2])), np.array((0.0, 0.0, 1.0)), np.array((0.0, 0.0, -1.0)))
        moved = self.bone_move(h.bone, mix)
        keep, tris = self.waist_of(None)
        pushes = []
        for at, way in zip(starts, ways):
            bare = outer(self.body.rest, tris, at, way, BELT_REACH, keep)
            most = outer(self.body_shaped(mix), tris, at + moved, way, BELT_REACH, keep)
            for g in mask:
                gk, gt = self.waist_of(g)
                most = max(most, outer(self.shaped(g, mix), gt, at + moved, way, BELT_REACH, gk))
            pushes.append(max(most - bare, 0.0) if not np.isneginf(most) and not np.isneginf(bare) else 0.0)
        out = self.cache[key] = tuple(pushes)
        return out

    # ---- A backpack (PackRig)

    def pack_rig(self, g):
        """SkinnedFigure.PackRigOf: (place, depth): the middle of the face a backpack lies on the back with, and how far
        out its far side is from there. None without the pack slot."""
        rig = self.rigs.get(g)
        if rig is not None:
            return rig
        slot = self.gear_slots.get('pack')
        if slot is None:
            return None
        way = slot.outward
        rest = self.piece(g).rest
        along = rest @ way
        inner, far = along.min(), along.max()
        face = rest[along <= inner + (far - inner) * PACK_FACE]
        low, high = face.min(axis=0), face.max(axis=0)
        middle = (low + high) * 0.5
        at = middle + way * (inner - middle @ way)
        half = (high - low) * PACK_LOOK * 0.5
        spots = np.array([(x * half[0], y * half[1], 0.0) for y in (0.0, 1.0, -1.0) for x in (0.0, 1.0, -1.0)])
        rig = self.rigs[g] = (Slot(g, 'gear', slot.bone, at, np.zeros(3), way, 0, spots), far - inner)
        return rig

    def worn_pack(self, worn):
        """SkinnedFigure.WornPack: the first backpack in the garments worn, None for none."""
        return next((g for g in self.names if g in worn.mask and self.is_pack(g)), None)

    def pack_push(self, worn, g, rig):
        """PackPush: how far a backpack's face goes out from its place at rest."""
        return self.clearance(worn.mix, worn.mask, rig[0]) + GEAR_GAP

    def packed(self, worn, g):
        """SkinnedFigure.Packed: a backpack's vertices at rest in the shape, before its bone's turn; None without the slot."""
        rig = self.pack_rig(g)
        if rig is None:
            return None
        h = rig[0]
        place = self.bone_move(h.bone, worn.mix) + h.at + h.outward * self.pack_push(worn, g, rig)
        return place + (self.piece(g).rest - h.at) * worn.backpack_scale

    # ---- A shield carried on the back (Shields.cs)

    def is_shield(self, name):
        """SkinnedFigure.CarriedOnBack: a shield with a held_at line."""
        return self.piece(name).held_at is not None

    def shield_over(self, worn, slots, extras, depths):
        """WalkFigures.ShieldOverWeapons: how far out the weapons holstered on the back stand (their slots' Slot, extras
        and half depths by name); minus infinity for none."""
        most = -np.inf
        for h in slots:
            depth = depths.get(h.name)
            if depth is not None and h.name in extras and self.lies_on_pack(h):
                most = max(most, h.at @ h.outward + self.clearance(worn.mix, worn.mask, h) + extras[h.name] + abs(depth))
        return most

    def shield_push(self, worn, rig, over):
        """ShieldPush: how far a shield's face goes out from its place on the back."""
        h = rig[0]
        face = h.at @ h.outward
        push = self.clearance(worn.mix, worn.mask, h) + GEAR_GAP
        packs = self.pack_outer(worn, h.outward)
        if not np.isneginf(packs):
            push = max(push, packs + GEAR_GAP - face)
        if not np.isneginf(over):
            push = max(push, over + GEAR_GAP - face)
        return push

    def shielded(self, worn, g, over, held):
        """SkinnedFigure.ShieldPlace, less the bone's turn: (the bone it rides, its vertices at rest in the shape): in the
        left fist (held), else on the pack slot's bone over what is under it (over: shield_over)."""
        piece = self.piece(g)
        rig = None if held else self.pack_rig(g)
        if rig is None:
            b = self.bone_index['hand.L']
            m = piece.held_at
            return b, piece.rest @ m[:3, :3].T + m[:3, 3] + self.bone_move(b, worn.mix)
        h = rig[0]
        return h.bone, piece.rest + self.bone_move(h.bone, worn.mix) + h.outward * self.shield_push(worn, rig, over)

    def shield_on_hand(self, worn, g, turned):
        """ShieldPlace part way through shield_draw, less the hand's turn: (hand.L, its vertices at rest in the shape) as
        held turned (0 .. 1) of the way from held_grip, as the hand takes it by its edge, to held_at, the guard's grip."""
        piece = self.piece(g)
        b = self.bone_index['hand.L']
        c = np.identity(4)
        c[:3, 3] = piece.rest.mean(0)   # turned about its own middle, as ShieldPlace
        m = between(piece.held_grip @ c, piece.held_at @ c, turned) @ np.linalg.inv(c)
        return b, piece.rest @ m[:3, :3].T + m[:3, 3] + self.bone_move(b, worn.mix)

    def pack_outer(self, worn, way):
        """PackOuter: the outermost thing worn on the back along way, any pack there; minus infinity for none."""
        most = -np.inf
        g = self.worn_pack(worn)
        rig = self.pack_rig(g) if g is not None else None
        if rig is not None:
            most = rig[0].at @ way + (self.pack_push(worn, g, rig) + rig[1] * worn.backpack_scale) * (rig[0].outward @ way)
        return max(most, self.gear_pack_outer(worn, way)) if worn.gear_pack else most

    def lies_on_pack(self, h):
        """LiesOnPack: a big slot on the pack's bone, facing the way it does."""
        gear = self.gear_slots.get('pack')
        return h.pack <= 0 and h.size == 'big' and gear is not None and h.bone == gear.bone and h.outward @ gear.outward > SAME_WAY_OUT

    def pack_lift(self, worn, h, at):
        """PackLift: how far up a slot at this height goes to lie about the middle of the pack worn."""
        if not self.lies_on_pack(h):
            return 0.0
        g = self.worn_pack(worn)
        rig = self.pack_rig(g) if g is not None else None
        if rig is not None:
            middle = rig[0].at[1]
        elif worn.gear_pack:
            middle = self.gear_pack_middle(worn)
        else:
            return 0.0
        return max(0.0, middle + OVER_PACK_MIDDLE - at[1])

    def side_surface(self, mix, mask, h):
        """SideSurface: the furthest out along a slot's way that the shape's body or the clothes worn stand beside it."""
        mask = tuple(g for g in mask if not self.unclear(g))
        key = ('side', mix, mask, h.bone, tuple(h.at), tuple(h.aim), tuple(h.outward))
        best = self.cache.get(key)
        if best is not None:
            return best
        best = -np.inf
        moved = self.bone_move(h.bone, mix)
        for along in ALONG_BIG:
            at = h.at + moved + h.aim * (BIG_HALF_LENGTH * along)
            if abs(along) < 1.0:
                best = max(best, outer(self.body_shaped(mix), self.body.tris, at, h.outward, SIDE_FAR))
            for g in mask:
                best = max(best, outer(self.shaped(g, mix), self.piece(g).tris, at, h.outward, SIDE_FAR))
        self.cache[key] = best
        return best

    def pack_side(self, worn, h, at, apart):
        """PackSide: a slot for a pack's side, at rest before the bone's move for the shape."""
        g = self.worn_pack(worn)
        rig = self.pack_rig(g) if g is not None else None
        reach = self.gear_pack_side(worn) if worn.gear_pack else 0.0
        if rig is not None:
            pivot, way = rig[0].at, rig[0].outward
            push = self.pack_push(worn, g, rig)
            side = ((self.piece(g).rest - pivot) @ h.outward).max()
            reach = max(reach, side * worn.backpack_scale)
        else:
            place = self.gear_pack_place(worn)
            if place is None:
                return at + h.outward * apart
            pivot, way, push = place
        offset = at - pivot
        along = offset @ h.outward
        past = self.side_surface(worn.mix, worn.mask, h)
        if not np.isneginf(past):
            reach = max(reach, along + past)
        scale = worn.backpack_scale if rig is not None else worn.pack_scale
        return pivot + way * push + (offset - h.outward * along) * scale + h.outward * (reach + apart)

    # ---- Weapons

    def holster_at(self, worn, h, at, extra=0.0):
        """HolsterAt's rest: where a slot holds its weapon at rest, before the bone's turn. at: the place the weapon takes,
        h.at or its melee place."""
        move = self.bone_move(h.bone, worn.mix)
        if h.pack > 0:
            return self.pack_side(worn, h, at, extra) + move
        return at + np.array((0.0, 1.0, 0.0)) * self.pack_lift(worn, h, at) + move + h.outward * (self.clearance(worn.mix, worn.mask, h) + extra)

    @staticmethod
    def holster_stacks(h, b):
        """HolsterStacks: whether a weapon in h lies on one in b."""
        return h.size == 'big' and b.size == 'big' and h.bone == b.bone and h.outward @ b.outward > SAME_WAY_OUT

    def holster_over(self, worn, h, b, under_extra, apart):
        """HolsterOver: how much further out than Clearance has it h goes to stand apart out from the weapon in b."""
        own = h.at @ h.outward + self.clearance(worn.mix, worn.mask, h)
        below = b.at @ h.outward + self.clearance(worn.mix, worn.mask, b) + under_extra
        return max(0.0, below + apart - own)

    def holster_clear(self, worn, h, apart):
        """HolsterClear: what a weapon needs of HolsterAt's extra beside what it lies on."""
        if h.pack > 0:
            return apart
        if not self.lies_on_pack(h):
            return 0.0
        most = self.pack_outer(worn, h.outward)
        return 0.0 if np.isneginf(most) else max(0.0, most + apart - h.at @ h.outward - self.clearance(worn.mix, worn.mask, h))

    def extras(self, worn, slots, depths):
        """WalkFigures.OverOthers for the slots in the order they fill: how much further out than its Clearance each goes.
        depths: each slot's weapon's half depth in model units (HalfDepth / size), None for one kept empty, and a negative
        one for a slot kept for a weapon now in the hands (counted, as the game keeps it, with nothing further of its own)."""
        out = {}
        for i, h in enumerate(slots):
            depth = depths.get(h.name)
            if depth is None:
                continue
            if depth < 0.0:
                out[h.name] = 0.0
                continue
            most = self.holster_clear(worn, h, depth + STACK_GAP)
            for b in slots[:i]:
                under = depths.get(b.name)
                if under is not None and self.holster_stacks(h, b):
                    most = max(most, self.holster_over(worn, h, b, out[b.name], depth + abs(under) + STACK_GAP))
            out[h.name] = most
        return out

    # ---- Worn gear (WornGears.cs)

    def gear_pack_place(self, worn):
        """GearPackPlace: an equipment pack's reference point, the way out, and how far out it goes; None without the slot."""
        h = self.gear_slots.get('pack')
        if h is None:
            return None
        push = self.clearance(worn.mix, worn.mask, h) + GEAR_GAP
        g = self.worn_pack(worn)
        rig = self.pack_rig(g) if g is not None else None
        if rig is not None:
            back = rig[0].at @ h.outward + self.pack_push(worn, g, rig) + rig[1] * worn.backpack_scale
            push = max(push, back + GEAR_GAP - h.at @ h.outward)
        return h.at, h.outward, push

    def gear_pack_outer(self, worn, way):
        place = self.gear_pack_place(worn)
        if place is None:
            return -np.inf
        return place[0] @ way + (place[2] + ISORIM_PACK_DEEP * self.gear_scale['pack'] * worn.pack_scale) * (place[1] @ way)

    def gear_pack_middle(self, worn):
        return self.gear_slots['pack'].at[1] + ISORIM_PACK_MIDDLE * self.gear_scale['pack'] * worn.pack_scale if 'pack' in self.gear_slots else -np.inf

    def gear_pack_side(self, worn):
        return ISORIM_PACK_SIDE * self.gear_scale['pack'] * worn.pack_scale if 'pack' in self.gear_slots else 0.0

    def gear_at(self, worn, mount):
        """GearAt, less the bone's turn: the 4 by 4 taking the gear's own model (IsoRim's figure space) to rest in the
        game's space, and the place its slot has gone to. None without the slot."""
        h = self.gear_slots.get(mount)
        if h is None:
            return None
        k = self.gear_scale[mount]
        if mount == 'belt':
            side, other, ahead, behind = self.belt_pushes(worn.mix, worn.mask, h)
            half = max(h.at[0] + GEAR_GAP, 0.01)
            sx = (half + max(side, other)) / GEAR_BELT_WIDTH
            sz = (half * self.gear_depth[mount] + (ahead + behind) / 2) / GEAR_BELT_WIDTH
            at = h.at + h.outward * (max(side, other) + GEAR_GAP)
            scale, pivot, reference = (sx, k, sz), np.array((0.0, h.at[1], (ahead - behind) / 2)), np.array((0.0, ISORIM_WAIST, 0.0))
        else:
            push = self.clearance(worn.mix, worn.mask, h) + GEAR_GAP
            if mount == 'pack':
                k *= worn.pack_scale
                push = self.gear_pack_place(worn)[2]
            at = h.at + h.outward * push
            scale, pivot, reference = (k, k, k), at, np.array((0.0 if mount == 'pack' else GEAR_BELT_WIDTH, ISORIM_WAIST, 0.0))
        move = self.bone_move(h.bone, worn.mix)
        m = np.identity(4)
        m[:3, :3] = self.gear_turn[mount] @ np.diag(scale)
        m[:3, 3] = pivot - m[:3, :3] @ reference + move
        return m, at + move

def pack_spots(scale):
    """WornGears.PackSpots: where Clearance looks for an equipment pack of this size, over its back plane. The first is
    the slot's own place."""
    return np.array([((-1.0 if i == 2 else float(i)) * ISORIM_PACK_WIDE * scale, ISORIM_PACK_TALL * up / 3.0 * scale, 0.0)
                     for up in range(4) for i in range(3)])
