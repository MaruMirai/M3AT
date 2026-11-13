using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The chibi skinned at runtime: one rest-pose mesh with its bone weights (skin.txt) and the clips as one move per bone
    // per frame (anim.txt), both written by anim_scripts/export_frames.py. Each pawn gets its own meshes, posed every frame
    // it is drawn, so a pose can be any blend of clips and frames (no snapping to the nearest baked frame).
    //
    // Garments (garment_*.txt, listed in skin.txt) are more meshes on the same rig, each with the apparel layer and body
    // part groups it is drawn for; they are posed with the same bones, so they need no frames of their own.
    //
    // Body types: the body's shape keys (the game's body type names, and Shrinkhead for a smaller head) come as moves of
    // the vertices they change, with the moves of the joints (bone heads) that go with them; each garment has the same
    // keys, fitted to the body by the exporter. A pawn's shape (its body type's key, and the head shrink) is put together
    // once per mix and shared (Shape). The clips were made for the base skeleton; with the joints moved, each bone's
    // place is corrected down the bone chain (End), so the limbs stay joined and the feet stay under the hips.
    //
    // It is drawn as IsoRim draws a loaded model (DynamicModels.Build and Submit): a lit solid copy in the tint, and an
    // inside-out copy pushed out along the outline hull (ModelLibrary.BuildHull, worked out once per shape in the rest pose
    // and turned with the bones), in the tint times ModelBaker.OutlineShade. DynamicModels.DrawParts places, culls, batches
    // and floors them like Api.DrawModel.
    //
    // With SmoothShading the solid copy is drawn with this mod's MarusSmooth material instead of IsoRim's LitModel: the
    // same sun curve, but from the mesh's own normals (skinned with the pose) blended across each triangle, rather than
    // one snapped direction per triangle, which looks blotchy on a curved body.
    //
    // Hair (<style>.txt and .png, built from the hairstyle's sprites by HairBuilder, or by archive/hair.py) is one
    // more piece, read the first time a pawn with that style is drawn and cut down to the options' share of its triangles, its shell
    // too (Decimate). It rides the head bone, has its own smooth
    // normals and texture places and the body's keys fitted to it, and is drawn textured (MarusSmoothTex: the same
    // shading, the sprites' art cut out below alpha 0.5) in the hair colour. Its night light pass is its own too
    // (MarusSmoothTexMark), not IsoRim's: see HairMaterial. Its outline is drawn from its shell (<style>.shell.txt):
    // the whole closed hull it was cut from, so the outline runs unbroken round the hair's holes and the face, in the
    // hair's colour where there is hair and the skin's where there is not. While it is worn the head's own outline is
    // left out under it (HideUnder), so the two don't overlap.
    //
    // A hairstyle is found by its HairDef's texPath, less Things/Pawn/Humanlike/Hairs/ when it has that (so a
    // vanilla style is plain Bob), as the first of: a M3AThair/<style> in any active mod (made by hand or by hair.py, for
    // this body), and the cache (HairBuilder), where every other style is built from its sprites in the background the
    // first time a pawn with it is drawn. Until it is built the pawn is drawn bald. This mod ships no hair of its own:
    // what is built is made from whichever hair textures the player has, and stays in their config folder.
    //
    // Outlines are drawn with this mod's MarusOutline, a copy of IsoRim's moving outline (pushed away from the camera, by
    // OutlinePush rather than View3D.OutlinePush; drawn only outside the chibi's own solid faces, by stencil bit 64, so
    // it is the silhouette alone, unless the mod's options turn that off, Silhouette), with its own light pass (MarusOutlineMark). IsoRim's outline has
    // none, so at night it takes the game's overlay pulled toward the camera: the light of the ground behind it on
    // screen, which changed along a line that moved as the camera tilted.
    [StaticConstructorOnStartup]
    public sealed partial class SkinnedFigure
    {
        private const string Tag = "[M3AT] ";
        private const float OutlineWidth = 0.03f;   // cells, as IsoRim's models' outline (Api.OutlineWidth)
        // Cells the outline shell is pushed away from the camera (IsoRim pushes its moving models' by View3D.OutlinePush,
        // 0.2, to hide the shell where one part tucks into another). The chibi's needn't hide anything over its own body:
        // MarusOutline is drawn only where the body, garments and hair haven't set stencil bit 64, so it is the silhouette
        // alone. A small push only keeps the line from being lost where a wall or a piece of furniture is close behind.
        public const float OutlinePush = 0.05f;
        public const bool SmoothShading = true;   // false: IsoRim's own flat, snapped shading
        public const float ScalpShade = 0.7f;   // the scalp under the hair, as a share of the hair colour (it is in shadow)
        // The scalp's hairline: the head above a plane that rises ScalpSlope cells per cell towards the face, through the
        // middle of the head's extent: the top of the forehead in front, down past the temples to the nape behind.
        private const float ScalpSlope = 0.56f;
        // The forehead's few vertices just clear that plane, and the colour fades from them to the next vertex down, which
        // drew a V to the brow. The front of the head within this many cells of its middle has the plane this much higher.
        private const float ScalpForeheadHalf = 0.1f, ScalpForeheadRaise = 0.05f;
        // A long triangle can reach from above that plane far down the face, and the colour runs down it to the far corner:
        // a vertex with an edge to the skin more than this many cells below it stays skin. The temples' run to the jaw (0.18).
        private const float ScalpEdgeDrop = 0.15f;
        public const float ShadeBands = 0f;   // 2 or more: the smooth shade rounded to that many levels (toon); 0: smooth
        private const string SmoothDef = "MarusSmooth";
        public const string ShrinkKey = "Shrinkhead";   // the body key that makes the head smaller
        public const string PregnantKey = "Pregnant";   // the body key for a full-term belly
        public const string BabyLimbsKey = "Babylimbs";   // the body key for a newborn's short, stubby limbs
        private const string HairTextures = "Things/Pawn/Humanlike/Hairs/";   // vanilla hair texPaths; the rest names the style
        private const string BeardTextures = "Things/Pawn/Humanlike/Beards/";   // vanilla beard texPaths; the rest names the style
        private const string BeardFolder = "beards/";   // where in the hair's folders a beard's files are
        private const int BeardTriangles = 500;   // the least a built beard is cut down to, walls and all (LoadHair)
        private const string RidgeFile = "ridges_{0}.png";   // a design of the FacialRidges gene (anim_scripts/make_ridges.py)
        private const int FlatBeardSize = 512;   // pixels: a beard laid flat on the face is a texture this square (FlatBeardOf)
        private const string ModHairFolder = "M3AThair";   // another mod's hair for this body, at its root
        private const string HairDef = "MarusSmoothTex";
        private const string HairMarkDef = "MarusSmoothTexMark";
        private const string OutlineDef = "MarusOutline";
        private const string OutlineLinesDef = "MarusOutlineLines";
        private const string OutlineMarkDef = "MarusOutlineMark";
        private const string GlassDef = "MarusGlass";

        // A mesh on the rig: the body (pieces[0]) or a garment.
        internal sealed class Piece
        {
            public string name;
            public string[] defs;     // a garment's: the apparel (ThingDef names) it is drawn for
            public string layer;      // and the apparel layer (ApparelLayerDef) it stands in on for apparel with no garment of its own, "" none (used only without a def the game has: StandIns.cs)
            public string[] groups;   // and the body part groups (BodyPartGroupDef) it stands in for, any of them
            public string textureFile;   // a garment's image, next to its file; null: none, drawn in the apparel's colour
            public int tint = -1;        // a textured garment's: 1 tinted by the apparel's colour, 0 not, -1 as the apparel is (Tinted)
            public Color mean = Color.white;   // a textured garment's mean colour, which its outline is drawn in (times the tint)
            public int hideHair = -1, hideFace = -1;   // a garment's: 1 the hair (the face layers) left off while it is worn, 0 not, -1 if it hides most of the scalp (face); the hair's also 2, cut at its rim (HairUnderHat)
            public bool overHair;       // a garment's: worn over the hair, pushed out past it rather than tucking it (HatOverHair.cs)
            public bool hidesClothes;   // a garment's: the garments worn under it are left off (body armour, HidesClothes)
            public bool withLegwear;    // a garment's: also worn once the torso and legs are covered (the shoes, WalkFigures.WornGarments)
            public bool forBaby;        // a garment's: worn by a baby a few hours old (the baby romper, WalkFigures.BabyRomperOf)
            public bool held;           // a garment's: held in the hand (a shield, its held line), drawn only while the game shows
                                        // the weapon (WalkFigures.WornGarments); it hides nothing and is in no clearance (HeldMask)
            public bool heldOnBack;     // and with a held_at line: carried on the back as exported, and in the hand while shown (Shields.cs)
            public Matrix4x4 heldAt;    // and that line: from its rest place on the back to its rest place in the left fist
            public Matrix4x4? heldGrip; // and its held_grip line: from there to its rest place on hand.L as shield_draw takes it by its edge
            public Color? colour;       // a garment's own colour, in place of the apparel's (the shoes); null: the apparel's
            public int[] hides;       // a garment's: the body triangles (numbered as skin.txt's) under it, left out while it is worn
            public int wallsFrom = -1;   // built hair's: its first wall triangle (its walls line), which Decimated keeps whole
            public Vector2? wallUv;      // built hair's: the texture place every hairline wall takes (its wall_uv line, HairlineWalled)
            public WallAtlas wallAtlas;  // hair: its image's art, which its hairline walls keep their texture places on; null: not read
            public bool[] glass;      // a garment's: which of its triangles are glass (a helmet's visor), drawn see-through (DrawVisor); null: none
            public int[] shellTris;   // and its triangles that aren't glass, which its solid mesh draws
            public Piece visor;       // and the glass as a piece of its own (VisorOf), posed as the hair is
            public Vector3[] rest;
            public int[] tris;
            public int[] firstWeight;   // vertex i's weights are weightBone/weight[firstWeight[i] .. firstWeight[i + 1])
            public int[] weightBone;
            public float[] weight;
            public readonly Dictionary<string, Vector3[]> keys = new Dictionary<string, Vector3[]>(StringComparer.OrdinalIgnoreCase);   // each vertex's move, cells
            public Vector2[] uvs;       // texture places (hair); null: none
            public Vector3[] normal;    // normals from the file (hair, whose vertices are split where its sprites meet); null: worked out
            public Texture2D texture;   // hair: its sprites; a garment: its image
            public Material material;   // hair or a textured garment: its own textured material, made when first drawn
            public Material mark;       // and its light pass; null: the game's overlay lights it
            public Piece shell;         // hair: the shell its outline is drawn from; null: its own mesh
            public bool[] skinVert;     // a shell's: vertices on no hair, outlined in the skin's colour
            public Vector4[][] packed;  // each vertex's bones for the graphics card (GpuSkinning.cs, PackedOf); null until first skinned there
        }

        // A piece's rest pose in one body shape.
        internal sealed class PieceShape
        {
            public Vector3[] rest;
            public Vector3[] hull;
            public Vector3[] normal;   // smooth: the area-weighted mean of the face normals round each vertex
            public PieceShape shell;   // hair: its shell's
        }

        // One mix of the body's keys, shared by every pawn of that body type.
        public sealed class Shape
        {
            internal PieceShape[] pieces;
            internal Vector3[] boneHead;   // each bone's rest head in this shape, cells
            internal Vector3[] boneMove;   // and how far that is from the base skeleton the clips were made for
            internal Vector3[] faceRest;
            internal Vector3 faceCentre;
            internal List<KeyValuePair<string, float>> mix;   // the keys and amounts it is made of
            internal readonly Dictionary<Piece, PieceShape> hairs = new Dictionary<Piece, PieceShape>();   // made when first drawn
            internal readonly Dictionary<(int, Piece, PieceShape), PieceShape> overHair = new Dictionary<(int, Piece, PieceShape), PieceShape>();   // a hat over the hair, by garment, hair and its shape under (HatOverHair.cs)
            internal readonly Dictionary<(GarmentMask, int), float> holsterPush = new Dictionary<(GarmentMask, int), float>();   // Clearance's, by slot and garments
            internal bool headMeasured;       // the box round the head (MeasureHead)
            internal List<string> geneKeys;   // the gene keys it has whole (null: none), which MeasureHead leaves out too
            internal Vector3 headLow, headHigh;
            internal readonly Dictionary<(int, GarmentMask), PieceShape> letOut = new Dictionary<(int, GarmentMask), PieceShape>();   // webbing let out, by garment and garments under it (LetOut.cs)
        }

        private Piece[] pieces;
        private Vector3[] boneHead;   // each bone's rest head, cells
        private int[] boneParent;     // -1 for none
        private int[] boneOrder;      // parents before their children
        private bool[] armBone;       // shoulder to hand, either side (Over with arms)
        private bool[] trunkBone;     // the trunk below the neck (ReclineOf)
        private bool[] legBone;       // the thighs down (LaidMiddleOf)
        private float[] twistShare;   // each bone's share of a twist of the trunk (Twist): the spine above the hips
        private float[] lookShare;    // each bone's share of a turn of the head (Look): the neck and the head
        private float[] overMask;     // Over with arms: the clip's shares, each times its weight
        private readonly Dictionary<string, int> boneIndex = new Dictionary<string, int>();   // by name (anim.txt's bone lines)
        private readonly Dictionary<string, Vector3[]> boneKeys = new Dictionary<string, Vector3[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vector3> centreKeys = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        private int headBone;
        // The face patch (FaceLayers): its vertices, each made from a body vertex (it takes that vertex's weights).
        private int[] faceFrom;
        private Vector3[] faceRest;
        private Vector2[] faceUvs;
        private int[] faceTris;
        private Vector3 faceCentre;
        private readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>();
        // How many ways each clip with variants (walk.VARIANTS, exported as <clip>~2, <clip>~3 ...) can be played, itself
        // included; a clip without any is not listed.
        private readonly Dictionary<string, int> variants = new Dictionary<string, int>();
        private readonly Dictionary<string, Shape> shapes = new Dictionary<string, Shape>();
        private readonly Dictionary<(GarmentMask, int), int[]> bodyTris = new Dictionary<(GarmentMask, int), int[]>();   // by mask of garments worn and bare feet (HideUnder)
        private readonly Dictionary<long, bool[]> garmentUnder = new Dictionary<long, bool[]>();   // by garment under and over (UnderOf)
        private readonly Dictionary<(int, GarmentMask), int[]> garmentTris = new Dictionary<(int, GarmentMask), int[]>();   // by garment and mask of garments over it (HideGarments)
        private readonly Dictionary<(int, GarmentMask), int[]> garmentSolidTris = new Dictionary<(int, GarmentMask), int[]>();   // and less its glass (GarmentSolidTris)
        private readonly Dictionary<(GarmentMask, Piece, int), int[]> outlineTris = new Dictionary<(GarmentMask, Piece, int), int[]>();   // and hair
        private string folder;
        private bool turnRound;   // the export's triangles are wound the wrong way out (Load), hair's too
        private readonly Dictionary<string, Piece> hairs = new Dictionary<string, Piece>();   // by style; null: none to be had
        private readonly Dictionary<string, Piece> hairImages = new Dictionary<string, Piece>();   // by style: the hair last read, whose texture and materials the next read keeps
        private float hairsDetail = -1f;   // the SettingsMod.HairDetail that hairs were cut down to
        private string stamp;   // skin.txt's hash: the cache folder of hair built for this body
        private HairBuilder builder;
        private HairBuilder beardBuilder;
        private readonly Dictionary<string, Material> ridges = new Dictionary<string, Material>();   // by design: its material (RidgesMaterial)
        private readonly Dictionary<string, Material> flatBeards = new Dictionary<string, Material>();   // by beard style: its flat texture's material (FlatBeardOf)
        private bool[] onHead;   // the body's vertices the head bone moves most
        private bool[] onArm;    // the body's vertices an arm bone below the shoulder moves most (OnArm)
        private bool[] scalp;   // the body's vertices above the hairline (Scalp)
        private readonly List<int> scalpGiven = new List<int>();
        private readonly List<Vector3> hairlineGiven = new List<Vector3>();   // skin.txt's hairline line (the rig's Hairline curve), in order round it   // the body's scalp vertices from skin.txt's scalp line (the rig's Scalp group)
        private readonly Dictionary<bool[], int[]> faceTrisOff = new Dictionary<bool[], int[]>();   // by scalp (FaceTrisOff)
        private List<string> hairRoots;   // where finished hair files are looked for, in order (the cache not among them)
        private int finishedAt = -1;   // the frame the builder's finished styles were last taken in

        private class Clip
        {
            public Quaternion[][] turn;   // [frame][bone]: the bone's turn from rest, on top of its parent's (ReadAnim)
            public Vector3[][] head;      // [frame][bone]: where its rest head goes, cells (only the root's is used)
            public float seconds;         // one loop in game seconds, for a clip timed by the clock; 0: by the ground covered
            public float[] over;          // [bone]: how much of each bone it takes when laid over another (Over); null: all
        }

        // How a drafted stance holds its weapon (walk.WEAPONS, anim.txt's weapon lines; WalkFigures.DrawWeapon).
        public class Hold
        {
            public string hand;    // "L" or "R": the fist it is held from
            public string line;    // what it runs along: "hands" (through the other fist), "forearm", "aim" or "hand" (rides it)
            public float behind;   // how far back from the sprite's middle the fist is, a share of its length
            public float drop;     // straight down added to the line
            public float roll;     // degrees turned about the line
            public Vector3 aim;    // the fixed direction for "aim", model space; for "hand", its direction with the hand at rest
            public Vector3 at;     // for "hand": the grip moved from the fist, model units, with the hand at rest
            public float scale = -1f;   // a share of its drawn size (as it lies on the floor); -1: the export's WeaponScale
        }

        // The tool a work clip holds (anim.txt's tool lines, walk.WORK; WalkFigures.DrawTool): IsoRim's tool model riding a
        // hand bone.
        public class Tool
        {
            public string model;   // IsoRim's Models/<model>.obj
            public string bone;    // the hand bone it rides: hand.L or hand.R
            public Matrix4x4 place;   // from the tool model's own space to the rest pose's (model units), moved by that bone
        }

        private readonly Dictionary<string, Hold> holds = new Dictionary<string, Hold>();
        private readonly Dictionary<string, Tool> tools = new Dictionary<string, Tool>();

        // The tool the work_<kind> clip holds; null for one with bare hands, or an older export without tool lines.
        public Tool ToolOf(string kind)
        {
            Tool t;
            return tools.TryGetValue(kind, out t) ? t : null;
        }
        public float HandGrip = -1f;      // model units from the wrist to the fist (anim.txt's weapon_hold); -1: not given
        public float WeaponScale = -1f;   // a share of a weapon's drawn size (as it lies on the floor); -1: not given
        public Vector3 WeaponShift;       // model units the weapon is moved: x out to its hand's side, y up, z forward
        public float AimPitch;            // degrees the aim clips' frames reach up and down (anim.txt's aim_pitch); 0: none
        private readonly Dictionary<string, Vector3> grips = new Dictionary<string, Vector3>();

        // The way a hand's fist closes from the wrist at rest (anim.txt's weapon_grip: along the hand bone), model space;
        // false for an older export without it.
        public bool GripOf(string side, out Vector3 along)
        {
            return grips.TryGetValue(side, out along);
        }

        // How the export says the stance kind holds its weapon; null for an older export without weapon lines.
        public Hold HoldOf(string kind)
        {
            Hold h;
            return holds.TryGetValue(kind, out h) ? h : null;
        }

        // One mesh drawn with one material (Api.SubmitFigureMesh, DrawMeshes): an outline shell (left out with the outlines
        // off, never flashing), and whether IsoRim's light pass for the material is drawn with it (lit: a material set up by
        // Api.ConfigureFigureMaterial with one, as the solid's and IsoRim's own are; a textured piece's and the outline's light
        // passes are parts of their own).
        internal sealed class MeshPart
        {
            public Mesh mesh;
            public Material material;
            public bool outline;
            public bool lit;
        }

        // One piece's posed meshes for one pawn.
        internal class PieceMeshes
        {
            public Mesh solid;
            public Mesh outline;
            public readonly List<MeshPart> parts = new List<MeshPart>();
            public Vector3[] verts;
            public Vector3[] hulled;
            public Vector3[] normals;
            public Color32 tint;
            public Color32 lineTint;   // the outline's, before it is shaded
            public Color32 skin;   // a hair shell's: the skin colour its skin vertices are outlined in
            public bool[] scalp;   // the body's: the vertices coloured as the scalp (Scalp), null: none
            public Color32 scalpTint;
            public Color32[] paint;   // the body's: its prosthetics' colours (PaintOf), alpha 0 where there is none; null: none
            public int hiddenLimbs;   // the fur's: the prosthetics its tufts are left off (HideFur)
            public bool tinted;
            public int outlineMarkAt = -1;   // where the outline's light pass is in parts; -1: not added yet
            public bool textureMarked;   // a textured garment's light pass is in parts
            public int clearAt = -1;   // where its two stencil-clearing parts are in parts (SetSlot); -1: not added yet
            public int afterAt = -1;   // where its redraw after IsoRim's ambient occlusion is in parts (SetSlot); -1: not added yet
            public bool isSkin;   // the body or the fur on it, which never take the occlusion (ClothesOcclusion)
            public bool dressed;  // a garment (or the fur): skinned by the pawn's dress bones while it has a pylon (Prosthetics.cs)
            public int bare = -1;   // the bare feet its triangles were left out for (Unshod)
            public int posedAt = -1;   // the Posed.posedAt it was last posed at
            public PieceShape gpuShape;   // posed on the graphics card (GpuSkinning.cs): the shape whose rest pose its meshes hold; null: posed here
            public int cpuAt = -1;   // and the Posed.posedAt its verts and normals were last worked out here at for what reads them (PosedOnCpu)
            public GarmentMask hidden;   // the garments whose triangles it has left out: the body's (HideUnder), a garment's (HideGarments)
            public Piece hiddenHair;   // and the hair whose shell its outline is left out under
            public bool tucked;   // and whether what is under the blanket is left out too (BedTuck.cs)
        }

        // One pawn's posed meshes.
        public class Posed
        {
            public Shape shape;
            internal PieceMeshes[] pieces;   // made when first drawn
            internal Piece hairPiece;   // the hair its hair meshes are for
            internal Piece hairOver;    // the hair a hat worn over it is pushed out past (HairUnderHats)
            internal PieceMeshes hair;
            internal readonly Dictionary<Piece, PieceMeshes> visors = new Dictionary<Piece, PieceMeshes>();   // the glass of each worn garment with a visor (DrawVisor)
            internal Piece beardPiece;   // and the beard its beard meshes are for
            internal PieceMeshes beard;
            internal PieceMeshes ears;   // its gene's ears (CatEars.cs), made when first drawn
            internal string earsMadeOf;  // and the file those meshes are of
            internal Piece hornsPiece;   // the horns its horn meshes are for (Horns.cs)
            internal PieceMeshes horns;
            public EarState earState;   // and how they are moving
            internal PieceMeshes tail;   // its cat tail (CatTail.cs), made when first drawn
            public bool furTail, tailMadeFur;   // whether it has the furry tail, and whether those meshes are of it
            public TailState tailState;   // and how it is moving
            public FaceLayers.Patch face;   // null without a face patch
            public Vector3[] faceVerts;
            public Vector3[] faceNormals;
            internal bool[] faceScalp;   // the scalp its face patch's triangles leave out (DrawBody), null: none
            internal WoundMeshes wounds;   // its wounds (SetWounds), null: none laid on yet
            public int frame;   // when it was last drawn
            public float flash;   // how red the game's damage flash draws it now, 0 .. 1 in sixteenths (HitFlash.Of, set by its draw)
            // The pose being blended (Begin, Add, End).
            public Quaternion[] turn;
            public Vector3[] head;
            public float total;
            public Quaternion[] overTurn;   // the clips laid over it (Over), per bone
            public float[] overWeight;
            public Quaternion[] topTurn;   // the clips laid over those (Top), per bone
            public float[] topWeight;
            public Quaternion[] nudge;   // the clips added on top (Nudge), per bone: a turn from each clip's first frame
            public float twist;   // degrees the trunk is turned about the vertical over the hips (Twist)
            public float look;    // degrees the head is turned about the vertical on the chest (Look)
            public float lean;    // degrees the trunk is leant sideways over the hips, + to the figure's left (Lean)
            public GarmentMask wornMask;       // the garments it has on this draw (Wearing)
            public bool gearPack;      // and whether an equipment pack is drawn on its back
            public float packScale = 1f;   // the size an equipment pack is drawn at, of this figure's: as big on every pawn
            public float backpackScale = 1f;   // and a backpack, likewise, a child's being a smaller one
            public float shieldOut;   // how far a shield carried on the back is through being taken into the hand: 0 on the back, 1 in the guard (Shielding)
            public float shieldOver = float.NegativeInfinity;   // and how far out the weapons holstered under it stand (ShieldPush)
            public int standOn = -1;   // the thigh of the one leg it stands on (Balance); -1: none
            public float balance;   // and how far the hips are moved over that foot (0 .. 1)
            public float crouch;   // how far the hips are let down over the planted feet (Crouch), 0 .. 1 of CrouchDrop
            public float recline;   // degrees the trunk is bent forward over the hips, the thighs kept as they were (Recline)
            public Vector3[] pos;   // each bone's head as posed, cells
            public bool[] cut;   // the bones cut off at their head, with all below them (Cut): a missing limb
            public int[] thin;   // the bone each bone is thinned about (Fit, Prosthetics.cs: a peg's or a pylon's); -1: not
            public float[] thinBy;   // and to what share of its width
            public bool[] thinAll;   // and whether the bones below it are thinned with it (a peg's foot), not left whole (a pylon's)
            public bool pylons;   // some bone is thinned (a peg's or pylon's): the clothes are skinned by dress, not bones (Prosthetics.cs)
            public int bare;   // the feet the shoes are off (1 left, 2 right): a prosthetic leg or foot (Fit, Unshod)
            public Matrix4x4[] dress;   // bones as the clothes take them: a pylon left its full width, so a sleeve or trouser leg hangs as it would
            public float[] dressRows;   // and as 12 numbers each, as boneRows
            internal MaterialPropertyBlock dressBlock;   // and for the graphics card, as bonesBlock
            internal Vector4[] dressVectors;
            internal int dressAt = -1;
            internal bool pylonsPosed;   // pylons as the meshes were last posed: a change poses them again
            public int limbs;    // its prosthetic limbs (Fit), which the body is coloured by
            public bool tucked;   // under IsoRim's blanket in bed: only the head is drawn (BedTuck.cs); set before HideUnder
            public Matrix4x4[] bones;   // what the meshes are skinned by: a cut bone squashed to the joint it was cut at
            public Matrix4x4[] whole;   // the same uncut, for what rides a bone (BoneOf, JointOf)
            public float[] boneRows;   // bones as 12 numbers each, the top three rows (ReadBones), what the meshes are skinned from
            public Matrix4x4[] shownBones, shownWhole;   // bones, whole and pos as the meshes were last posed (Finish)
            public Vector3[] shownPos;
            public int drawsHeld;   // draws since then, set going from its id so pawns don't all pose in the same frame
            public float posedTime;   // and when that was, real seconds
            public bool poseEveryDraw;   // carried: it stays in the carrier's arms by being placed from them every draw
            public int posedAt;   // counts each change to what the meshes are posed from (Finish, Wearing): the same, and nothing need be posed again
            public Shape posedShape;   // the shape it was last posed in
            public int extentAt = -1;   // the posedAt lowest and bodyTop were found at (Extent)
            public int garmentsHiddenFor = -1;   // the worn answer (WalkFigures.WornGarments' version) HideGarments last hid for
            public bool garmentsHiddenTucked;   // and whether it was tucked under a blanket then
            public int garmentsHiddenBare;   // and its bare feet then
            public float lowest, bodyTop;
            public MaterialPropertyBlock bonesBlock;   // its bones for the meshes skinned on the graphics card (BlockOf), made when first drawn
            public Vector4[] boneVectors;
            public int bonesAt = -1;   // the posedAt they were set at
        }

        private readonly Dictionary<int, Posed> posed = new Dictionary<int, Posed>();
        private readonly List<int> stale = new List<int>();

        public bool Has(string clip)
        {
            return clips.ContainsKey(clip);
        }

        // The clip or one of its variants (<clip>~2, <clip>~3 ...), picked by roll (any number, such as a hash of the pawn
        // and the tick the event began on): the same roll picks the same one, so an event keeps its variant all through,
        // and clips with the same number of variants (the four flinches, bend and bend_walk) pick alike. The clip itself
        // when it has none, or for a null clip.
        public string Variant(string clip, int roll)
        {
            int count;
            if (clip == null || !variants.TryGetValue(clip, out count))
            {
                return clip;
            }
            int pick = (int)((uint)roll % (uint)count);
            string name = pick == 0 ? clip : clip + "~" + (pick + 1);
            return clips.ContainsKey(name) ? name : clip;
        }

        public int FramesOf(string clip)
        {
            Clip c;
            return clips.TryGetValue(clip, out c) ? c.turn.Length : 0;
        }

        // One loop of a clock-timed clip (the idle's breathing and weight shifts), game seconds; 0 for the gaits, which
        // go by the ground covered, and for an older export's one-frame idle.
        public float SecondsOf(string clip)
        {
            Clip c;
            return clips.TryGetValue(clip, out c) ? c.seconds : 0f;
        }

        // True if the body has this shape key.
        public bool HasKey(string key)
        {
            return key != null && pieces[0].keys.ContainsKey(key);
        }

        // The garments: pieces 1 .. Pieces - 1.
        public int Pieces
        {
            get { return pieces.Length; }
        }

        public string PieceName(int piece)
        {
            return pieces[piece].name;
        }

        private GarmentMask hairHiders, faceHiders;   // the garments (bit g: garment g) that leave the hair, and the face layers, off
        private GarmentMask faceIfFull;   // and those that leave the face layers off only when the game hides the eyes too (HidesFace)

        // Sorts out which garments leave the hair and the face layers off while worn: as their files say, else the ones
        // that hide (hides) more than half the scalp's vertices, or of the face patch's triangles (those only for apparel
        // the game itself draws without eyes, HidesFace).
        private void FindHeadHiders()
        {
            bool[] scalp = Scalp();
            for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
            {
                Piece piece = pieces[g];
                bool[] gone = new bool[pieces[0].rest.Length];
                foreach (int k in piece.hides)
                {
                    gone[pieces[0].tris[3 * k]] = gone[pieces[0].tris[3 * k + 1]] = gone[pieces[0].tris[3 * k + 2]] = true;
                }
                int scalpAll = 0, scalpGone = 0;
                for (int i = 0; i < scalp.Length; i++)
                {
                    if (scalp[i])
                    {
                        scalpAll++;
                        scalpGone += gone[i] ? 1 : 0;
                    }
                }
                int faceAll = 0, faceGone = 0;
                for (int k = 0; faceTris != null && k + 2 < faceTris.Length; k += 3)
                {
                    faceAll++;
                    faceGone += gone[faceFrom[faceTris[k]]] && gone[faceFrom[faceTris[k + 1]]] && gone[faceFrom[faceTris[k + 2]]] ? 1 : 0;
                }
                if (piece.hideHair == 1 || (piece.hideHair == -1 && scalpGone * 2 > scalpAll && scalpAll > 0))
                {
                    hairHiders |= GarmentBit(g);
                }
                if (piece.hideHair == 2)
                {
                    hairTuckers |= GarmentBit(g);
                    piece.hides = WithScalpUnder(piece.hides, g, scalp);
                }
                if (piece.hideFace == 1)
                {
                    faceHiders |= GarmentBit(g);
                }
                else if (piece.hideFace == -1 && faceGone * 2 > faceAll && faceAll > 0)
                {
                    faceIfFull |= GarmentBit(g);
                }
            }
        }

        // True if a garment in mask (bit g: garment g is worn) leaves the hair off.
        public bool HidesHair(GarmentMask mask)
        {
            return (mask & hairHiders) != 0;
        }

        // True if a garment in mask (worn[g]: the apparel garment g is worn as) leaves the face layers off. One that only
        // hides most of the face (a big bandana or a hood can) does so only if the game also leaves the eyes off for its
        // apparel, so it takes a full helmet.
        public bool HidesFace(GarmentMask mask, Apparel[] worn)
        {
            if ((mask & faceHiders) != 0)
            {
                return true;
            }
            for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((mask & faceIfFull & (GarmentBit(g))) != 0 && worn[g] != null && HidesEyes(worn[g].def.apparel))
                {
                    return true;
                }
            }
            return false;
        }

        // The game's own rule (PawnRenderTree.AdjustParms): the apparel's renderSkipFlags say Eyes, or it has none and
        // covers the full head.
        private static bool HidesEyes(ApparelProperties ap)
        {
            if (ap == null)
            {
                return false;
            }
            if (ap.renderSkipFlags != null && ap.renderSkipFlags.Count > 0)
            {
                return ap.renderSkipFlags.Contains(RenderSkipFlagDefOf.Eyes);
            }
            return ap.bodyPartGroups != null && ap.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead);
        }

        private static readonly Dictionary<ThingDef, bool> beardHiders = new Dictionary<ThingDef, bool>();

        // True if this apparel leaves the beard off: as the game's flags say (PawnRenderTree.AdjustParms), or if it covers
        // the mouth or the full head. The game has its masks drawn over the beard (their flags are None), but a mask here
        // lies close on the face, and the beard stands out through it.
        public static bool HidesBeard(ThingDef apparel)
        {
            bool hides;
            if (!beardHiders.TryGetValue(apparel, out hides))
            {
                ApparelProperties ap = apparel.apparel;
                hides = ap != null && ((ap.renderSkipFlags != null && ap.renderSkipFlags.Contains(RenderSkipFlagDefOf.Beard))
                    || (ap.bodyPartGroups != null && ap.bodyPartGroups.Any(g => g == BodyPartGroupDefOf.FullHead || g.defName == "Mouth")));
                beardHiders[apparel] = hides;
            }
            return hides;
        }

        // The garment drawn for this apparel by name (its defs), or -1 if it has none of its own. Biotech's children's
        // clothes with a grown-up version take its garment when they have none of their own (AdultOf): the body keys
        // already shrink it to the child's body.
        public int GarmentOf(ThingDef def)
        {
            int g;
            if (def == null)
            {
                return -1;
            }
            if (garmentOf.TryGetValue(def.defName, out g))
            {
                return g;
            }
            string adult;
            return AdultOf.TryGetValue(def.defName, out adult) && garmentOf.TryGetValue(adult, out g) ? g : -1;
        }

        private readonly Dictionary<string, int> garmentOf = new Dictionary<string, int>();

        // Children's apparel and the grown-up apparel whose garment it borrows. The romper has no grown-up version.
        private static readonly Dictionary<string, string> AdultOf = new Dictionary<string, string>
        {
            { "Apparel_KidShirt", "Apparel_BasicShirt" },
            { "Apparel_KidPants", "Apparel_Pants" },
            { "Apparel_KidTribal", "Apparel_TribalA" },
            { "Apparel_KidParka", "Apparel_Parka" },
            { "Apparel_VacsuitChildren", "Apparel_Vacsuit" },
            { "Apparel_KidHelmet", "Apparel_SimpleHelmet" },
            { "CE_Apparel_KidBackpack", "CE_Apparel_Backpack" },   // a backpack isn't shrunk by the body keys: see BorrowsPack
        };

        // True if something worn in mask (worn[g]: the apparel garment g is worn as) is body armour, which the gene tail
        // would come through: the hide_clothes line (power, recon, cataphract and plate armour, and whatever stands in for
        // them), but not a vacsuit (the game's Vacsuit apparel tag), which is skin tight and could let a tail out.
        public bool HidesTail(GarmentMask mask, Apparel[] worn)
        {
            for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((mask & (GarmentBit(g))) != 0 && pieces[g].hidesClothes && worn[g] != null
                    && (worn[g].def.apparel.tags == null || !worn[g].def.apparel.tags.Contains("Vacsuit")))
                {
                    return true;
                }
            }
            return false;
        }

        // True if garment over, worn as over, leaves off garment under, worn as under: over has the hide_clothes line, and
        // under is worn on a lower apparel layer and covers a body part group over covers (a shirt under armour, not a hat).
        public bool HidesClothes(int over, ApparelProperties overAp, ApparelProperties underAp)
        {
            if (over <= 0 || !pieces[over].hidesClothes || overAp == null || underAp == null || overAp.bodyPartGroups == null
                || underAp.bodyPartGroups == null || OuterLayer(underAp) >= OuterLayer(overAp))
            {
                return false;
            }
            for (int i = 0; i < underAp.bodyPartGroups.Count; i++)
            {
                if (overAp.bodyPartGroups.Contains(underAp.bodyPartGroups[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // The draw order of the outermost layer the apparel is worn on.
        private static float OuterLayer(ApparelProperties ap)
        {
            float outer = float.MinValue;
            for (int i = 0; ap.layers != null && i < ap.layers.Count; i++)
            {
                outer = Mathf.Max(outer, ap.layers[i].drawOrder);
            }
            return outer;
        }

        // True if this garment is worn with legwear (its with_legwear line), and its own colour if it has one.
        public bool WithLegwear(int piece)
        {
            return piece > 0 && pieces[piece].withLegwear;
        }

        // True if this garment is worn by a baby a few hours old (its baby_romper line: the baby romper).
        public bool ForBaby(int piece)
        {
            return piece > 0 && piece < pieces.Length && pieces[piece].forBaby;
        }

        // True if this garment is held in the hand (its held line: a shield).
        public bool IsHeld(int piece)
        {
            return piece > 0 && piece < pieces.Length && pieces[piece].held;
        }

        // The worn-garment bits of the held garments (shields): left out of what hides the clothes, the fur and the body
        // under them and of the clearances holsters, packs, gear and collars are pushed out by, as a shield goes wherever
        // the hand does and was placed at rest by the hand hanging beside the hip.
        private GarmentMask heldMask = -1;
        public GarmentMask HeldMask
        {
            get
            {
                if (heldMask < 0)
                {
                    heldMask = 0;
                    for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
                    {
                        if (pieces[g].held)
                        {
                            heldMask |= GarmentBit(g);
                        }
                    }
                }
                return heldMask;
            }
        }

        public Color? ColourOf(int piece)
        {
            return pieces[piece].colour;
        }

        // Reads skin.txt, the garments it lists and anim.txt from folder. Null (and a message) if one is missing or unreadable.
        public static SkinnedFigure Load(string folder)
        {
            string skinPath = Path.Combine(folder, "skin.txt"), animPath = Path.Combine(folder, "anim.txt");
            if (!File.Exists(skinPath) || !File.Exists(animPath))
            {
                return null;
            }
            try
            {
                SkinnedFigure s = new SkinnedFigure { folder = folder, stamp = Stamp(skinPath) };
                List<Piece> all = new List<Piece>();
                List<string> garments = new List<string>();
                Piece body = s.ReadMesh(skinPath, true, garments);
                all.Add(body);
                // Turned round as the body is, if it is: the exporter writes every piece the same way.
                bool turn = InsideOut(body.rest, body.tris);
                s.turnRound = turn;
                foreach (string file in garments)
                {
                    Piece g = s.ReadMesh(Path.Combine(folder, file), false, null);
                    foreach (int k in g.hides)
                    {
                        if (k < 0 || k >= body.tris.Length / 3)
                        {
                            throw new InvalidDataException(file + ": hides body triangle " + k + ", but the body has " + body.tris.Length / 3);
                        }
                    }
                    if (g.textureFile != null)
                    {
                        s.ReadTexture(g, Path.Combine(folder, g.textureFile));
                    }
                    foreach (string def in g.defs)
                    {
                        s.garmentOf[def] = all.Count;
                    }
                    all.Add(g);
                }
                foreach (Piece p in all)
                {
                    if (turn)
                    {
                        TurnRound(p.tris);
                    }
                    if (p.glass != null)
                    {
                        p.shellTris = Kept(p.tris, p.glass);
                        p.visor = VisorOf(p);
                    }
                }
                if (turn && s.faceTris != null)
                {
                    TurnRound(s.faceTris);
                }
                s.pieces = all.ToArray();
                if (all.Count - 1 > MaxHiding)
                {
                    Log.Warning(Tag + "the export has " + (all.Count - 1) + " garments, but worn-garment masks hold " + MaxHiding
                        + ": those past it (by name, from " + all[MaxHiding + 1].name + ") hide nothing and are hidden by nothing.");
                }
                s.FindHeadHiders();
                s.ReadAnim(animPath);
                s.GrainFur();
                return body.rest.Length > 0 && s.clips.Count > 0 ? s : null;
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not read the skinned chibi in " + folder + ": " + e);
                return null;
            }
        }

        private readonly Dictionary<string, Texture2D> garmentTextures = new Dictionary<string, Texture2D>();   // by file, shared

        // Loads a textured garment's image (shared with any other garment that has the same file) and its mean colour over
        // its vertices' places, which its outline is drawn in. Without the image it is drawn untextured, with a message.
        private void ReadTexture(Piece g, string path)
        {
            if (g.uvs == null)
            {
                throw new InvalidDataException(g.name + ": a texture but no place on it for each vertex (t lines)");
            }
            Texture2D t;
            if (!garmentTextures.TryGetValue(path, out t))
            {
                if (!File.Exists(path))
                {
                    Log.Warning(Tag + "the garment " + g.name + " has no " + Path.GetFileName(path) + ", so it is drawn in the apparel's colour.");
                    g.textureFile = null;
                    return;
                }
                t = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                t.LoadImage(File.ReadAllBytes(path));
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Trilinear;
                t.name = "Marus_Garment_" + Path.GetFileNameWithoutExtension(path);
                garmentTextures[path] = t;
            }
            g.texture = t;
            Color sum = Color.clear;
            foreach (Vector2 uv in g.uvs)
            {
                sum += t.GetPixelBilinear(uv.x, uv.y);
            }
            g.mean = g.uvs.Length > 0 ? sum / g.uvs.Length : Color.white;
            g.mean.a = 1f;
        }

        // A file's FNV-1a hash, as hex.
        private static string Stamp(string path)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in File.ReadAllBytes(path))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        private static float Num(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        private static Vector3 Vec(string[] p, int at)
        {
            return new Vector3(Num(p[at]), Num(p[at + 1]), Num(p[at + 2]));
        }

        // Reads skin.txt (isBody: also the face patch, the joints' moves and the garment list) or a garment's file.
        private Piece ReadMesh(string path, bool isBody, List<string> garments)
        {
            Piece piece = new Piece { name = Path.GetFileNameWithoutExtension(path), layer = "", groups = new string[0], defs = null };
            List<Vector3> v = new List<Vector3>();
            List<int> first = new List<int>(), bone = new List<int>(), t = new List<int>(), fFrom = new List<int>(), fTris = new List<int>();
            List<int> hides = new List<int>(), skinVerts = new List<int>(), glass = new List<int>();
            List<float> w = new List<float>();
            List<Vector3> fRest = new List<Vector3>();
            List<Vector2> fUvs = new List<Vector2>(), uvs = new List<Vector2>();
            List<Vector3> normals = new List<Vector3>();
            Dictionary<int, int> faceIndex = new Dictionary<int, int>();
            Vector3[] keyMoves = null;
            Dictionary<int, Vector3> keyBones = null;
            List<KeyValuePair<string, Dictionary<int, Vector3>>> bodyBoneKeys = new List<KeyValuePair<string, Dictionary<int, Vector3>>>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0 || p[0].StartsWith("#"))
                {
                    continue;
                }
                switch (p[0])
                {
                    case "v":
                        v.Add(Vec(p, 1));
                        break;
                    case "w":
                        first.Add(bone.Count);
                        for (int i = 1; i + 1 < p.Length; i += 2)
                        {
                            bone.Add(int.Parse(p[i]));
                            w.Add(Num(p[i + 1]));
                        }
                        break;
                    case "n":
                        normals.Add(Vec(p, 1));
                        break;
                    case "t":
                        uvs.Add(new Vector2(Num(p[1]), Num(p[2])));
                        break;
                    case "f":
                        t.Add(int.Parse(p[1]));
                        t.Add(int.Parse(p[2]));
                        t.Add(int.Parse(p[3]));
                        break;
                    case "defs":
                        piece.defs = new string[p.Length - 1];
                        Array.Copy(p, 1, piece.defs, 0, p.Length - 1);
                        break;
                    case "texture":
                        piece.textureFile = raw.Trim().Substring("texture".Length).Trim();
                        break;
                    case "tint":
                        piece.tint = p[1] == "yes" ? 1 : 0;
                        break;
                    case "hide_hair":
                        piece.hideHair = p[1] == "tuck" ? 2 : p[1] == "yes" ? 1 : 0;
                        break;
                    case "hide_face":
                        piece.hideFace = p[1] == "yes" ? 1 : 0;
                        break;
                    case "over_hair":
                        piece.overHair = p[1] == "yes" && !Legacy;
                        if (p[1] == "yes" && Legacy)
                        {
                            piece.hideHair = 2;   // (the autotest's comparison with the rules before: tucked)
                        }
                        break;
                    case "hide_clothes":
                        piece.hidesClothes = p[1] == "yes";
                        break;
                    case "with_legwear":
                        piece.withLegwear = p[1] == "yes";
                        break;
                    case "baby_romper":
                        piece.forBaby = p[1] == "yes";
                        break;
                    case "held":
                        piece.held = p[1] == "yes";
                        break;
                    case "held_at":
                        if (p.Length > 12)
                        {
                            Matrix4x4 h = Matrix4x4.identity;
                            for (int r = 0; r < 3; r++)
                            {
                                h.SetRow(r, new Vector4(Num(p[1 + 4 * r]), Num(p[2 + 4 * r]), Num(p[3 + 4 * r]), Num(p[4 + 4 * r])));
                            }
                            piece.heldAt = h;
                            piece.heldOnBack = true;
                        }
                        break;
                    case "held_grip":
                        if (p.Length > 12)
                        {
                            Matrix4x4 h = Matrix4x4.identity;
                            for (int r = 0; r < 3; r++)
                            {
                                h.SetRow(r, new Vector4(Num(p[1 + 4 * r]), Num(p[2 + 4 * r]), Num(p[3 + 4 * r]), Num(p[4 + 4 * r])));
                            }
                            piece.heldGrip = h;
                        }
                        break;
                    case "colour":
                        piece.colour = new Color(Num(p[1]), Num(p[2]), Num(p[3]));
                        break;
                    case "layer":
                        piece.layer = p[1];
                        break;
                    case "groups":
                        piece.groups = new string[p.Length - 1];
                        Array.Copy(p, 1, piece.groups, 0, p.Length - 1);
                        break;
                    case "walls":
                        piece.wallsFrom = int.Parse(p[1]);
                        break;
                    case "wall_uv":
                        piece.wallUv = new Vector2(float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture));
                        break;
                    case "hide":
                        for (int i = 1; i < p.Length; i++)
                        {
                            hides.Add(int.Parse(p[i]));
                        }
                        break;
                    case "skin":
                        for (int i = 1; i < p.Length; i++)
                        {
                            skinVerts.Add(int.Parse(p[i]));
                        }
                        break;
                    case "visor":
                        for (int i = 1; i < p.Length; i++)
                        {
                            glass.Add(int.Parse(p[i]));
                        }
                        break;
                    case "key":
                        // The vertices come before the keys, so each key's moves can be laid out in full.
                        keyMoves = new Vector3[v.Count];
                        piece.keys[p[1]] = keyMoves;
                        keyBones = new Dictionary<int, Vector3>();
                        if (isBody)
                        {
                            bodyBoneKeys.Add(new KeyValuePair<string, Dictionary<int, Vector3>>(p[1], keyBones));
                            centreKeys[p[1]] = Vector3.zero;
                        }
                        break;
                    case "d":
                        keyMoves[int.Parse(p[1])] = Vec(p, 2);
                        break;
                    case "db":
                        keyBones[int.Parse(p[1])] = Vec(p, 2);
                        break;
                    case "dc":
                        centreKeys[bodyBoneKeys[bodyBoneKeys.Count - 1].Key] = Vec(p, 1);
                        break;
                    case "fv":
                        faceIndex[int.Parse(p[1])] = fFrom.Count;
                        fFrom.Add(int.Parse(p[1]));
                        fUvs.Add(new Vector2(Num(p[2]), Num(p[3])));
                        fRest.Add(Vec(p, 4));
                        break;
                    case "ff":
                        fTris.Add(faceIndex[int.Parse(p[1])]);
                        fTris.Add(faceIndex[int.Parse(p[2])]);
                        fTris.Add(faceIndex[int.Parse(p[3])]);
                        break;
                    case "centre":
                        faceCentre = Vec(p, 1);
                        break;
                    case "head":
                        headBone = int.Parse(p[1]);
                        break;
                    case "scalp":
                        for (int k = 1; k < p.Length; k++)
                        {
                            scalpGiven.Add(int.Parse(p[k]));
                        }
                        break;
                    case "hairline":
                        for (int k = 1; k + 2 < p.Length; k += 3)
                        {
                            hairlineGiven.Add(Vec(p, k));
                        }
                        break;
                    case "garment":
                        if (garments != null)
                        {
                            garments.Add(p[2]);
                        }
                        break;
                }
            }
            if (first.Count == 0 && !isBody)
            {
                // No weights (hair): the whole piece rides the head bone.
                for (int i = 0; i < v.Count; i++)
                {
                    first.Add(bone.Count);
                    bone.Add(headBone);
                    w.Add(1f);
                }
            }
            if (first.Count != v.Count)
            {
                throw new InvalidDataException(path + ": " + v.Count + " vertices but " + first.Count + " weight lines");
            }
            first.Add(bone.Count);
            piece.rest = v.ToArray();
            piece.firstWeight = first.ToArray();
            piece.weightBone = bone.ToArray();
            piece.weight = w.ToArray();
            piece.tris = t.ToArray();
            piece.hides = hides.ToArray();
            if (glass.Count > 0)
            {
                piece.glass = new bool[piece.tris.Length / 3];
                foreach (int k in glass)
                {
                    if (k < 0 || k >= piece.glass.Length)
                    {
                        throw new InvalidDataException(path + ": glass triangle " + k + ", but it has " + piece.glass.Length);
                    }
                    piece.glass[k] = true;
                }
            }
            if (skinVerts.Count > 0)
            {
                piece.skinVert = new bool[v.Count];
                foreach (int i in skinVerts)
                {
                    piece.skinVert[i] = true;
                }
            }
            piece.uvs = uvs.Count == v.Count ? uvs.ToArray() : null;
            piece.normal = normals.Count == v.Count ? normals.ToArray() : null;
            if (isBody)
            {
                piece.name = "body";
                if (fFrom.Count > 0 && fTris.Count > 0)
                {
                    faceFrom = fFrom.ToArray();
                    faceRest = fRest.ToArray();
                    faceUvs = fUvs.ToArray();
                    faceTris = fTris.ToArray();
                }
                pendingBoneKeys = bodyBoneKeys;   // laid out once the bones are known (ReadAnim)
            }
            else if (piece.name.StartsWith("garment_"))
            {
                piece.name = piece.name.Substring("garment_".Length);
                if (piece.defs == null)
                {
                    piece.defs = new[] { "Apparel_" + piece.name };   // an older export, before defs lines
                }
            }
            if (piece.defs == null)
            {
                piece.defs = new string[0];
            }
            return piece;
        }

        private List<KeyValuePair<string, Dictionary<int, Vector3>>> pendingBoneKeys;

        private void ReadAnim(string path)
        {
            List<Vector3> heads = new List<Vector3>();
            List<int> parents = new List<int>();
            Clip clip = null;
            int frame = 0, bone = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0 || p[0].StartsWith("#"))
                {
                    continue;
                }
                if (p[0] == "bone")
                {
                    boneIndex[p[1]] = heads.Count;
                    heads.Add(Vec(p, 2));
                    parents.Add(p.Length > 5 ? int.Parse(p[5]) : -1);   // an older export without parents: no corrections down the chain
                }
                else if (p[0] == "clip")
                {
                    int frames = int.Parse(p[2]);
                    clip = new Clip { turn = new Quaternion[frames][], head = new Vector3[frames][], seconds = p.Length > 3 ? Num(p[3]) : 0f };
                    for (int f = 0; f < frames; f++)
                    {
                        clip.turn[f] = new Quaternion[heads.Count];
                        clip.head[f] = new Vector3[heads.Count];
                    }
                    clips[p[1]] = clip;
                    frame = bone = 0;
                }
                else if (p[0] == "k" && clip != null)
                {
                    if (bone == heads.Count)
                    {
                        frame++;
                        bone = 0;
                    }
                    clip.turn[frame][bone] = new Quaternion(Num(p[2]), Num(p[3]), Num(p[4]), Num(p[1]));
                    clip.head[frame][bone] = Vec(p, 5);
                    bone++;
                }
                else if (p[0] == "weapon_hold" && p.Length > 2)
                {
                    HandGrip = Num(p[1]);
                    WeaponScale = Num(p[2]);
                    WeaponShift = p.Length > 5 ? new Vector3(Num(p[4]), Num(p[3]), Num(p[5])) : Vector3.zero;   // older: no shift
                }
                else if (p[0] == "aim_pitch" && p.Length > 1)
                {
                    AimPitch = Num(p[1]);
                }
                else if (p[0] == "weapon_grip" && p.Length > 4)
                {
                    grips[p[1]] = Vec(p, 2).normalized;
                }
                else if (p[0] == "weapon" && p.Length > 9)
                {
                    holds[p[1]] = new Hold { hand = p[2], line = p[3], behind = Num(p[4]), drop = Num(p[5]), roll = Num(p[6]), aim = Vec(p, 7),
                                             at = p.Length > 12 ? Vec(p, 10) : Vector3.zero,   // older: no at
                                             scale = p.Length > 13 ? Num(p[13]) : -1f };   // older: no scale
                }
                else if (p[0] == "draw_timing" && p.Length > 1)
                {
                    DrawGrab = Num(p[1]);
                }
                else if (p[0] == "draw_slide" && p.Length > 2)
                {
                    drawSlides[p[1]] = Num(p[2]);
                }
                else if (p[0] == "holster" || p[0] == "holster_melee" || p[0] == "holster_pack" || p[0] == "holster_belt" || p[0] == "holster_packs")
                {
                    ReadHolster(p);
                }
                else if (p[0] == "gear")
                {
                    ReadGear(p);
                }
                else if (p[0] == "dress_loop" && p.Length > 3)
                {
                    ReadDressLoop(p);
                }
                else if (p[0] == "hip_timing" && p.Length > 2)
                {
                    HipGrab = Num(p[1]);
                    HipSlide = Num(p[2]);
                }
                else if (p[0] == "cuddle_pet" && p.Length > 3)
                {
                    PetAlong = Num(p[1]);
                    PetLift = Num(p[2]);
                    PetBack = Num(p[3]);
                }
                else if (p[0] == "shield_timing" && p.Length > 2)
                {
                    ShieldGrab = Num(p[1]);
                    ShieldSlide = Num(p[2]);
                }
                else if (p[0] == "weapon_type" || p[0] == "type_scale" || p[0] == "holster_weapon")
                {
                    ReadType(p);
                }
                else if (p[0] == "tool" && p.Length > 15)
                {
                    Matrix4x4 m = Matrix4x4.identity;
                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 4; c++)
                        {
                            m[r, c] = Num(p[4 + r * 4 + c]);
                        }
                    }
                    tools[p[1]] = new Tool { model = p[2], bone = "hand." + p[3], place = m };
                }
                else if (p[0] == "over")
                {
                    Clip laid;
                    if (clips.TryGetValue(p[1], out laid))
                    {
                        laid.over = new float[heads.Count];
                        for (int b = 0; b < heads.Count && b + 2 < p.Length; b++)
                        {
                            laid.over[b] = Num(p[b + 2]);
                        }
                    }
                }
            }
            foreach (string name in clips.Keys)
            {
                int mark = name.IndexOf('~');
                if (mark > 0 && clips.ContainsKey(name.Substring(0, mark)))
                {
                    string own = name.Substring(0, mark);
                    int count;
                    variants.TryGetValue(own, out count);
                    variants[own] = Mathf.Max(count, 1) + 1;
                }
            }
            boneHead = heads.ToArray();
            boneParent = parents.ToArray();
            armBone = new bool[boneHead.Length];
            trunkBone = new bool[boneHead.Length];
            legBone = new bool[boneHead.Length];
            overMask = new float[boneHead.Length];
            foreach (KeyValuePair<string, int> pair in boneIndex)
            {
                string limb = pair.Key.Split('.')[0];
                armBone[pair.Value] = limb == "shoulder" || limb == "upper_arm" || limb == "forearm" || limb == "hand";
                // The trunk below the neck (ReclineOf): spine to spine.004, and the pelvis bones on the hips.
                trunkBone[pair.Value] = pair.Key == "spine" || limb == "pelvis"
                    || (limb == "spine" && string.CompareOrdinal(pair.Key, "spine.004") <= 0);
                legBone[pair.Value] = limb == "thigh" || limb == "shin" || limb == "foot" || limb == "toe" || limb == "heel";
            }
            twistShare = new float[boneHead.Length];
            for (int i = 0; i < TwistBones.Length; i++)
            {
                int b;
                if (boneIndex.TryGetValue(TwistBones[i], out b))
                {
                    twistShare[b] = TwistShares[i];
                }
            }
            lookShare = new float[boneHead.Length];
            for (int i = 0; i < LookBones.Length; i++)
            {
                int b;
                if (boneIndex.TryGetValue(LookBones[i], out b))
                {
                    lookShare[b] = LookShares[i];
                }
            }
            // Parents before children, so each bone's correction can build on its parent's.
            List<int> order = new List<int>();
            bool[] placed = new bool[boneHead.Length];
            while (order.Count < boneHead.Length)
            {
                int before = order.Count;
                for (int b = 0; b < boneHead.Length; b++)
                {
                    if (!placed[b] && (boneParent[b] < 0 || placed[boneParent[b]]))
                    {
                        placed[b] = true;
                        order.Add(b);
                    }
                }
                if (order.Count == before)
                {
                    throw new InvalidDataException(path + ": the bones' parents go round in a loop");
                }
            }
            boneOrder = order.ToArray();
            // The file has each bone's turn in model space. Kept as the turn on top of its parent's instead, so clips can
            // be blended bone by bone (an arm from one clip follows the chest of another) and End rebuilds the chain.
            foreach (Clip c in clips.Values)
            {
                foreach (Quaternion[] turn in c.turn)
                {
                    Quaternion[] model = (Quaternion[])turn.Clone();
                    for (int b = 0; b < turn.Length; b++)
                    {
                        if (boneParent[b] >= 0)
                        {
                            turn[b] = Quaternion.Inverse(model[boneParent[b]]) * model[b];
                        }
                    }
                }
            }
            if (pendingBoneKeys != null)
            {
                foreach (KeyValuePair<string, Dictionary<int, Vector3>> k in pendingBoneKeys)
                {
                    Vector3[] moves = new Vector3[boneHead.Length];
                    foreach (KeyValuePair<int, Vector3> m in k.Value)
                    {
                        moves[m.Key] = m.Value;
                    }
                    boneKeys[k.Key] = moves;
                }
                pendingBoneKeys = null;
            }
        }

        // As IsoRim's loader: a closed mesh whose triangles enclose a negative volume is the wrong way out.
        private static bool InsideOut(Vector3[] pos, int[] tris)
        {
            float volume = 0f;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                volume += Vector3.Dot(pos[tris[i]], Vector3.Cross(pos[tris[i + 1]], pos[tris[i + 2]])) / 6f;
            }
            return volume < 0f;
        }

        private static void TurnRound(int[] tris)
        {
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int k = tris[i + 1];
                tris[i + 1] = tris[i + 2];
                tris[i + 2] = k;
            }
        }

        // The outline hull of a rest pose, per vertex: the mean of the face normals round each vertex, OutlineWidth long.
        // (This looked for IsoRim's mitred hull by reflection on the wrong class, so the mean is what was always drawn;
        // Api.BuildOutlineHull is the mitred one, should the look ever be wanted.)
        private static Vector3[] Hull(Vector3[] pos, int[] tris)
        {
            Vector3[] mean = new Vector3[pos.Length];
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = pos[tris[i]], b = pos[tris[i + 1]], c = pos[tris[i + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                for (int k = 0; k < 3; k++)
                {
                    mean[tris[i + k]] += n;
                }
            }
            for (int i = 0; i < mean.Length; i++)
            {
                mean[i] = mean[i].normalized * OutlineWidth;
            }
            return mean;
        }

        // A garment's outline hull: each vertex pushed OutlineWidth along its smooth normal. IsoRim's mitred hull suits its
        // hard-edged props, but a garment from a generator (Meshy) is a decimated mesh folded sharply all over: a third of
        // the power armour's corners hit the mitre limit or have no mitre at all, each pushed its own way, and the shell
        // tears into spikes. The smooth normals change slowly from vertex to vertex, so the shell stays whole; it is only
        // a little thin at sharp outward corners. Vertices split along UV seams share a normal, so the shell doesn't part there.
        private static Vector3[] SmoothHull(Vector3[] normal)
        {
            Vector3[] hull = new Vector3[normal.Length];
            for (int i = 0; i < normal.Length; i++)
            {
                hull[i] = normal[i] * OutlineWidth;
            }
            return hull;
        }

        // Smooth vertex normals: each face's normal weighted by its area (the cross product's length), summed round each
        // vertex. The mesh shares its vertices (it has no seams), so the body is smooth all over.
        // Vertices in the same place share one normal: a textured garment's are split along its UV seams (one copy per
        // place on the image), and the seam would otherwise show in the shading.
        private static Vector3[] Normals(Vector3[] pos, int[] tris)
        {
            int[] weld = new int[pos.Length];
            Dictionary<Vector3, int> first = new Dictionary<Vector3, int>(pos.Length);
            for (int i = 0; i < pos.Length; i++)
            {
                int j;
                if (!first.TryGetValue(pos[i], out j))
                {
                    j = i;
                    first[pos[i]] = i;
                }
                weld[i] = j;
            }
            Vector3[] n = new Vector3[pos.Length];
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = pos[tris[i]], b = pos[tris[i + 1]], c = pos[tris[i + 2]];
                Vector3 f = Vector3.Cross(b - a, c - a);
                n[weld[tris[i]]] += f;
                n[weld[tris[i + 1]]] += f;
                n[weld[tris[i + 2]]] += f;
            }
            for (int i = 0; i < n.Length; i++)
            {
                n[i] = n[weld[i]].normalized;
            }
            return n;
        }

        // ---- shapes

        // The shape for a body type's key (null or one the body lacks: the base body) with the head shrunk by shrink
        // (0 .. 1 of the Shrinkhead key). genderKey (Male or Female, or null) is added on top of a body type that is not
        // itself a gender's (Thin, Fat, Hulk) at genderAmount; a key named for the pair ("Hulk_Female") is added as well
        // if the body has one, to correct what the two summed get wrong. bodyAmount (0 .. 1) is how much of the body
        // type's key, pregnant (0 .. 1) how much of the Pregnant key is added on top, and babyLimbs (0 .. 1) how much of
        // the Babylimbs key. builds are more keys added on top at their amounts (WalkFigures.VariedBuild: Thin, Fat, Hulk
        // mixed per pawn), each that the body lacks or at 0 left out. geneKeys are keys
        // added whole for the pawn's genes (WalkFigures.GeneKeysOf), any the body lacks left out. Made once per mix;
        // its garments' shapes only when first worn in it (ShapedPiece).
        public Shape ShapeOf(string bodyKey, string genderKey, float genderAmount, float shrink, float bodyAmount = 1f, float pregnant = 0f,
            float babyLimbs = 0f, List<KeyValuePair<string, float>> builds = null, List<string> geneKeys = null)
        {
            if (!HasKey(bodyKey))
            {
                bodyKey = null;
            }
            if (bodyKey == null || bodyKey == "Male" || bodyKey == "Female")
            {
                genderKey = null;   // the base body (children, babies) has no gender shape; Male and Female already are one
            }
            string pairKey = genderKey != null && HasKey(bodyKey + "_" + genderKey) ? bodyKey + "_" + genderKey : null;
            genderAmount = HasKey(genderKey) ? Mathf.Round(Mathf.Clamp01(genderAmount) * 100f) / 100f : 0f;
            shrink = HasKey(ShrinkKey) ? Mathf.Round(Mathf.Clamp01(shrink) * 100f) / 100f : 0f;
            bodyAmount = Mathf.Round(Mathf.Clamp01(bodyAmount) * 100f) / 100f;
            pregnant = HasKey(PregnantKey) ? Mathf.Round(Mathf.Clamp01(pregnant) * 100f) / 100f : 0f;
            babyLimbs = HasKey(BabyLimbsKey) ? Mathf.Round(Mathf.Clamp01(babyLimbs) * 100f) / 100f : 0f;
            if (bodyAmount <= 0f)
            {
                bodyKey = null;
            }
            string id = (bodyKey ?? "") + "|" + bodyAmount.ToString(CultureInfo.InvariantCulture) + "|" + (genderAmount > 0f || pairKey != null ? genderKey : "") + "|"
                + genderAmount.ToString(CultureInfo.InvariantCulture) + "|" + shrink.ToString(CultureInfo.InvariantCulture) + "|"
                + pregnant.ToString(CultureInfo.InvariantCulture) + "|" + babyLimbs.ToString(CultureInfo.InvariantCulture);
            List<KeyValuePair<string, float>> extra = new List<KeyValuePair<string, float>>();
            List<string> shownGeneKeys = null;
            if (geneKeys != null)
            {
                foreach (string k in geneKeys)
                {
                    if (HasKey(k))
                    {
                        extra.Add(new KeyValuePair<string, float>(k, 1f));
                        (shownGeneKeys = shownGeneKeys ?? new List<string>()).Add(k);
                        id += "|" + k;
                    }
                }
            }
            if (builds != null)
            {
                foreach (KeyValuePair<string, float> k in builds)
                {
                    float amount = Mathf.Round(Mathf.Clamp01(k.Value) * 100f) / 100f;
                    if (amount > 0f && HasKey(k.Key))
                    {
                        extra.Add(new KeyValuePair<string, float>(k.Key, amount));
                        id += "|" + k.Key + amount.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            Shape s;
            if (shapes.TryGetValue(id, out s))
            {
                return s;
            }
            List<KeyValuePair<string, float>> mix = new List<KeyValuePair<string, float>>();
            if (bodyKey != null)
            {
                mix.Add(new KeyValuePair<string, float>(bodyKey, bodyAmount));
            }
            if (genderAmount > 0f)
            {
                mix.Add(new KeyValuePair<string, float>(genderKey, genderAmount));
            }
            if (pairKey != null)
            {
                mix.Add(new KeyValuePair<string, float>(pairKey, 1f));
            }
            if (shrink > 0f)
            {
                mix.Add(new KeyValuePair<string, float>(ShrinkKey, shrink));
            }
            if (pregnant > 0f)
            {
                mix.Add(new KeyValuePair<string, float>(PregnantKey, pregnant));
            }
            if (babyLimbs > 0f)
            {
                mix.Add(new KeyValuePair<string, float>(BabyLimbsKey, babyLimbs));
            }
            mix.AddRange(extra);
            s = new Shape { pieces = new PieceShape[pieces.Length], mix = mix, geneKeys = shownGeneKeys };
            s.pieces[0] = ShapePiece(pieces[0], mix);
            s.boneHead = Mixed(boneHead, boneKeys, mix);
            s.boneMove = new Vector3[boneHead.Length];
            for (int b = 0; b < boneHead.Length; b++)
            {
                s.boneMove[b] = s.boneHead[b] - boneHead[b];
            }
            s.faceCentre = faceCentre;
            foreach (KeyValuePair<string, float> k in mix)
            {
                Vector3 c;
                if (centreKeys.TryGetValue(k.Key, out c))
                {
                    s.faceCentre += c * k.Value;
                }
            }
            if (faceFrom != null)
            {
                // Each face vertex moves as the body vertex it was made from.
                Vector3[] bodyRest = pieces[0].rest, bodyShaped = s.pieces[0].rest;
                s.faceRest = new Vector3[faceRest.Length];
                for (int i = 0; i < faceRest.Length; i++)
                {
                    s.faceRest[i] = faceRest[i] + (bodyShaped[faceFrom[i]] - bodyRest[faceFrom[i]]);
                }
            }
            shapes[id] = s;
            System.Threading.Interlocked.Increment(ref LoadTrail.shapesMade);
            return s;
        }

        // A piece's rest pose in the shape, the body's made with it and each garment's when it is first worn in it, so a shape
        // costs little more than the body (varied bodies make one for nearly every pawn).
        internal PieceShape ShapedPiece(Shape s, int index)
        {
            PieceShape shaped = s.pieces[index];
            if (shaped == null)
            {
                shaped = ShapePiece(pieces[index], s.mix);
                s.pieces[index] = shaped;
            }
            return shaped;
        }

        private static PieceShape ShapePiece(Piece piece, List<KeyValuePair<string, float>> mix)
        {
            Vector3[] rest = Mixed(piece.rest, piece.keys, mix);
            Vector3[] normal = piece.normal ?? Normals(rest, piece.tris);
            Vector3[] hull = null;   // hair: outlined by its shell instead
            if (piece.shell == null)
            {
                hull = piece.defs.Length > 0 ? SmoothHull(normal) : Hull(rest, piece.tris);
            }
            return new PieceShape
            {
                rest = rest,
                hull = hull,
                normal = normal,
                shell = piece.shell != null ? ShapePiece(piece.shell, mix) : null
            };
        }

        private static Vector3[] Mixed(Vector3[] rest, Dictionary<string, Vector3[]> keys, List<KeyValuePair<string, float>> mix)
        {
            Vector3[] r = (Vector3[])rest.Clone();
            foreach (KeyValuePair<string, float> k in mix)
            {
                Vector3[] moves;
                if (!keys.TryGetValue(k.Key, out moves))
                {
                    continue;   // a garment without this key: unchanged by it
                }
                for (int i = 0; i < r.Length; i++)
                {
                    r[i] += moves[i] * k.Value;
                }
            }
            return r;
        }

        // ---- posing

        // The pawn's posed meshes in this shape, made on first use. Pawns not drawn for a while are let go.
        public Posed For(int id, Shape shape)
        {
            Posed p;
            int frame = Time.frameCount;
            if (!posed.TryGetValue(id, out p))
            {
                Prune(frame);
                p = NewPosed();
                p.drawsHeld = (int)((uint)id % PoseEveryMost);
                posed[id] = p;
            }
            p.frame = frame;
            p.shape = shape;
            p.poseEveryDraw = false;
            return p;
        }

        private void Prune(int frame)
        {
            if (posed.Count < 64)
            {
                return;
            }
            stale.Clear();
            foreach (KeyValuePair<int, Posed> pair in posed)
            {
                if (frame - pair.Value.frame > 600)
                {
                    stale.Add(pair.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                Posed p = posed[stale[i]];
                foreach (PieceMeshes m in p.pieces)
                {
                    if (m != null)
                    {
                        Api.RetireMesh(m.solid);
                        Api.RetireMesh(m.outline);
                    }
                }
                if (p.hair != null)
                {
                    Api.RetireMesh(p.hair.solid);
                    Api.RetireMesh(p.hair.outline);
                }
                if (p.beard != null)
                {
                    Api.RetireMesh(p.beard.solid);
                    Api.RetireMesh(p.beard.outline);
                }
                RetireEars(p);
                RetireHorns(p);
                RetireTail(p);
                if (p.face != null)
                {
                    Api.RetireMesh(p.face.face);
                    Api.RetireMesh(p.face.mouth);
                    Api.RetireMesh(p.face.mouthLow);
                }
                if (p.wounds != null)
                {
                    Api.RetireMesh(p.wounds.mesh);
                }
                posed.Remove(stale[i]);
            }
        }

        private Posed NewPosed()
        {
            Posed p = new Posed();
            int bones = boneHead.Length;
            p.turn = new Quaternion[bones];
            p.head = new Vector3[bones];
            p.overTurn = new Quaternion[bones];
            p.overWeight = new float[bones];
            p.topTurn = new Quaternion[bones];
            p.topWeight = new float[bones];
            p.nudge = new Quaternion[bones];
            p.pos = new Vector3[bones];
            p.cut = new bool[bones];
            p.thin = new int[bones];
            p.thinBy = new float[bones];
            p.thinAll = new bool[bones];
            p.dress = new Matrix4x4[bones];
            p.dressRows = new float[12 * bones];
            p.bones = new Matrix4x4[bones];
            p.boneRows = new float[12 * bones];
            p.shownBones = new Matrix4x4[bones];
            p.shownWhole = new Matrix4x4[bones];
            p.shownPos = new Vector3[bones];
            p.whole = new Matrix4x4[bones];
            p.pieces = new PieceMeshes[pieces.Length];
            if (faceFrom != null)
            {
                p.faceVerts = new Vector3[faceRest.Length];
                p.faceNormals = new Vector3[faceRest.Length];
                p.face = FaceLayers.NewPatch(faceRest, faceUvs, faceTris, faceCentre);
                p.face.smooth = SmoothShading;
            }
            return p;
        }

        private PieceMeshes NewPieceMeshes(Piece piece)
        {
            int n = piece.rest.Length;
            Piece outer = piece.shell ?? piece;
            PieceMeshes m = new PieceMeshes();
            m.verts = new Vector3[n];
            m.hulled = new Vector3[outer.rest.Length];
            m.normals = new Vector3[n];
            m.solid = NewMesh(piece.rest, piece.tris, piece.uvs ?? new Vector2[n]);
            m.outline = NewMesh(outer.rest, outer.tris, new Vector2[outer.rest.Length]);
            if (piece.shellTris != null)
            {
                // The solid mesh without the glass, which DrawVisor draws as a piece of its own (not as a second sub-mesh:
                // the engine's mesh keeps one, rebuilding its index buffer from sub-mesh 0's alone whenever the vertices
                // or colours are set). The outline keeps every triangle, so the glass is inside the silhouette.
                m.solid.SetTriangles(piece.shellTris, 0, false);
            }
            m.parts.Add(new MeshPart { mesh = m.solid, material = SolidMaterial(), outline = false });
            m.parts.Add(new MeshPart { mesh = m.outline, material = OutlineMaterial(), outline = true });
            return m;
        }

        private static Mesh NewMesh(Vector3[] verts, int[] tris, Vector2[] uvs)
        {
            List<Vector3> tags = new List<Vector3>(verts.Length);
            for (int i = 0; i < verts.Length; i++)
            {
                tags.Add(Api.MovingMeshTag);   // clipped under the roof over where it is drawn, as IsoRim's moving models are
            }
            Mesh m = new Mesh();
            m.MarkDynamic();
            m.vertices = verts;
            m.uv = uvs;
            m.SetUVs(1, tags);
            m.triangles = tris;
            // Set once, roomy enough for any pose (cells, the model's own space): it is only for culling.
            m.bounds = new Bounds(new Vector3(0f, 0.7f, 0f), new Vector3(2.5f, 2f, 2.5f));
            return m;
        }

        // A pose is a weighted blend of clip samples: Begin, then Add each, then Over each laid on top, then Top each laid over
        // those, then Nudge each added on top of that, then End.
        public void Begin(Posed p)
        {
            for (int b = 0; b < p.turn.Length; b++)
            {
                p.turn[b] = new Quaternion(0f, 0f, 0f, 0f);
                p.head[b] = Vector3.zero;
                p.overTurn[b] = new Quaternion(0f, 0f, 0f, 0f);
                p.overWeight[b] = 0f;
                p.topTurn[b] = new Quaternion(0f, 0f, 0f, 0f);
                p.topWeight[b] = 0f;
                p.nudge[b] = Quaternion.identity;
                p.cut[b] = false;
                p.thin[b] = -1;
                p.thinAll[b] = false;
            }
            p.pylons = false;
            p.total = 0f;
            p.twist = 0f;
            p.look = 0f;
            p.lean = 0f;
            p.standOn = -1;
            p.balance = 0f;
            p.crouch = 0f;
            p.recline = 0f;
        }

        // Cuts a bone off at its head, with every bone below it, for this pose (after Begin): a missing limb. Every vertex
        // is drawn in to the joint by as much as it rides the bones cut, so the limb closes to a rounded stump there and
        // what is worn on it goes with it. False if the export has no bone of that name.
        public bool Cut(Posed p, string bone)
        {
            int b;
            if (!boneIndex.TryGetValue(bone, out b))
            {
                return false;
            }
            p.cut[b] = true;
            return true;
        }

        // Adds clip at phase (0 .. 1 round the cycle, between frames blended) with this weight, every bone alike.
        public void Add(Posed p, string clip, float phase, float w)
        {
            Clip c;
            if (w <= 0f || !clips.TryGetValue(clip, out c))
            {
                return;
            }
            Sample(c, phase, w, p.turn, p.head, null, null);
            p.total += w;
        }

        // Lays clip over what Add blended, each bone by w times the clip's own share of it (anim.txt's over line): at 1, the
        // bones it takes are its own, turned on top of the parents the blend gave them (the drafted arms and chest ride
        // the walking hips); the rest keep the blend.
        public void Over(Posed p, string clip, float phase, float w)
        {
            Clip c;
            if (w <= 0f || !clips.TryGetValue(clip, out c))
            {
                return;
            }
            Sample(c, phase, w, p.overTurn, null, c.over, p.overWeight);
        }

        // As Over, but the arms (shoulder to hand) by arms in place of w: a hand held to a wound all the way or not at
        // all, the hunch under it by how hurt.
        public void Over(Posed p, string clip, float phase, float w, float arms)
        {
            Clip c;
            if ((w <= 0f && arms <= 0f) || !clips.TryGetValue(clip, out c))
            {
                return;
            }
            for (int b = 0; b < overMask.Length; b++)
            {
                overMask[b] = (c.over != null ? c.over[b] : 1f) * (armBone[b] ? arms : w);
            }
            Sample(c, phase, 1f, p.overTurn, null, overMask, p.overWeight);
        }

        // As Over, but laid over what the Over clips gave (they are blended with each other, by their weights): a hand raised
        // to cast takes that arm from a drafted stance all the way, not half of it.
        public void Top(Posed p, string clip, float phase, float w)
        {
            Clip c;
            if (w <= 0f || !clips.TryGetValue(clip, out c))
            {
                return;
            }
            Sample(c, phase, w, p.topTurn, null, c.over, p.topWeight);
        }

        // Adds clip on top of everything else with this weight, the bones in its over line only: each bone's turn at phase
        // from its turn in the clip's first frame (the plain stance it was made from), put before the bone's own turn, so a
        // jolt (a flinch) moves a standing, walking, aiming or lying figure alike. Played once: phase is not wrapped.
        public void Nudge(Posed p, string clip, float phase, float w)
        {
            Clip c;
            if (w <= 0f || phase < 0f || phase >= 1f || !clips.TryGetValue(clip, out c))
            {
                return;
            }
            int n = c.turn.Length;
            float at = phase * n;
            int f0 = Mathf.FloorToInt(at) % n, f1 = (f0 + 1) % n;
            float t = at - Mathf.Floor(at);
            Quaternion[] first = c.turn[0];
            for (int b = 0; b < first.Length; b++)
            {
                float wb = c.over != null ? w * c.over[b] : w;
                if (wb <= 0f)
                {
                    continue;
                }
                Quaternion q = Quaternion.Slerp(c.turn[f0][b], c.turn[f1][b], t) * Quaternion.Inverse(first[b]);
                p.nudge[b] = Quaternion.Slerp(Quaternion.identity, q, Mathf.Clamp01(wb)) * p.nudge[b];
            }
        }

        // Turns the trunk about the vertical (degrees, as the figure's yaw) over the hips, spread up the spine (TwistBones),
        // with the arms, head and what they hold going with it; the hips and legs stay as posed. A figure turning to track
        // its aim keeps its feet planted this way until it steps round (WalkFigures.StepOf).
        public void Twist(Posed p, float degrees)
        {
            p.twist = degrees;
        }

        // Leans the trunk sideways (degrees, + to the figure's left) over the hips, spread up the spine as the twist is
        // (TwistBones), the arms, head and what they hold going with it: two sat side by side leaning together over a book.
        public void Lean(Posed p, float degrees)
        {
            p.lean = degrees;
        }

        // Turns the head about the vertical (degrees, as the figure's yaw) on top of whatever the clips and the twist do,
        // spread down the neck (LookBones): a look aside at someone without turning the body, as a teacher looks round
        // at the pupil beside it (WalkFigures.LessonLookOf).
        public void Look(Posed p, float degrees)
        {
            p.look = degrees;
        }

        // Puts the weight on one leg ("L" or "R") whatever the clips do, for a figure with the other gone: amount (0 .. 1)
        // of the way to BalanceShare (OverFoot).
        public void Balance(Posed p, string side, float amount)
        {
            int thigh;
            if (amount > 0f && boneIndex.TryGetValue("thigh." + side, out thigh))
            {
                p.standOn = thigh;
                p.balance = amount;
            }
        }

        // Lets the hips down over the feet whatever the clips do (amount 0 .. 1 of CrouchDrop), the feet staying just where
        // and how the clips put them and the knees bending to reach: a crouch behind cover (WalkFigures.CoverOf) that keeps
        // the stance's own legs, turned and staggered as they are, and the hips' own turn, so an aim or drafted stance
        // laid on top points just as it does standing.
        public void Crouch(Posed p, float amount)
        {
            p.crouch = Mathf.Clamp01(amount);
        }

        // How far the hips go down at a full crouch, cells for a figure of size 1 in the base shape.
        public float CrouchDepth()
        {
            int thigh, shin, foot;
            if (!boneIndex.TryGetValue("thigh.L", out thigh) || !boneIndex.TryGetValue("shin.L", out shin) || !boneIndex.TryGetValue("foot.L", out foot))
            {
                return 0f;
            }
            return CrouchDrop * ((boneHead[shin] - boneHead[thigh]).magnitude + (boneHead[foot] - boneHead[shin]).magnitude);
        }

        private const float CrouchDrop = 0.4f;   // leg lengths the hips go down at a full crouch (walk.COVER_DROP)
        private const float CrouchBack = 0.12f;   // and back over the heels (walk.COVER_BACK)
        private const float CrouchSplay = 0.2f;   // the knees go forward and this much out to their own side, so they fold apart (walk.COVER_SPLAY)

        private const float BalanceShare = 0.8f;   // share of the way the hips move sideways to be over the foot (walk.HOP_OVER, so the hop is left alone)

        // ---- a carrier's hands on the person it carries

        // Where a carry loop's wrist holds the person carried, in the frame of the carried figure's bone it is nearest
        // (the middle of the thighs, the back), as the export made them: both in the base shape, the carried one scaled
        // about its hips by the size the export placed the hands for. bone -1: that hand holds nothing (a free hand).
        private struct Grip
        {
            public int bone;
            public Vector3 local;
        }

        private readonly Dictionary<string, Grip[][]> carryGrips = new Dictionary<string, Grip[][]>();   // by carry and carried clip: per frame, right then left
        private Posed gripCarrier, gripCarried;   // scratch figures in the base shape that grips are found on
        private const float GripMost = 0.12f;   // model units a wrist may be off the carried figure's bones and still count as holding it

        // Where the hand on side ("R" or "L") holds a carried figure posed in carried (Chain done, its own shape and place),
        // in carried's model units with its whole move in: the same place on it the export's hand holds, so a person of
        // another size or build is still held there. carryClip and carriedClip as the carrier and carried play them, at
        // phase; carriedScale the size against the carrier the export placed the hands for (a baby's CRADLE_SCALE);
        // chestBone the bone the carried figure rides. False for a hand that holds nothing.
        public bool GripOf(string carryClip, string carriedClip, float carriedScale, string chestBone, float phase, string side, Posed carried, out Vector3 at)
        {
            at = Vector3.zero;
            Clip c;
            int chest;
            if (!clips.TryGetValue(carryClip, out c) || !clips.ContainsKey(carriedClip) || !boneIndex.TryGetValue(chestBone, out chest))
            {
                return false;
            }
            Grip[][] frames;
            string key = carryClip + "|" + carriedClip;
            if (!carryGrips.TryGetValue(key, out frames))
            {
                frames = new Grip[c.turn.Length][];
                carryGrips[key] = frames;
            }
            int n = frames.Length, s = side == "R" ? 0 : 1;
            float f = Mathf.Repeat(phase, 1f) * n;
            int f0 = Mathf.FloorToInt(f) % n, f1 = (f0 + 1) % n;
            float t = f - Mathf.Floor(f);
            Grip g0 = GripAt(frames, f0, carryClip, carriedClip, carriedScale, chest)[s];
            Grip g1 = GripAt(frames, f1, carryClip, carriedClip, carriedScale, chest)[s];
            if (g0.bone < 0 || g1.bone < 0)
            {
                return false;
            }
            at = Vector3.Lerp(carried.whole[g0.bone].MultiplyPoint3x4(g0.local), carried.whole[g1.bone].MultiplyPoint3x4(g1.local), t);
            return true;
        }

        private Grip[] GripAt(Grip[][] frames, int frame, string carryClip, string carriedClip, float carriedScale, int chest)
        {
            if (frames[frame] != null)
            {
                return frames[frame];
            }
            Shape shape = ShapeOf(null, null, 0f, 0f);
            if (gripCarrier == null)
            {
                gripCarrier = NewPosed();
                gripCarried = NewPosed();
            }
            gripCarrier.shape = gripCarried.shape = shape;
            float phase = (float)frame / frames.Length;
            Begin(gripCarrier);
            Add(gripCarrier, carryClip, phase, 1f);
            Chain(gripCarrier, Matrix4x4.identity);
            Vector3 hips = RootOf(carriedClip);
            Matrix4x4 on = gripCarrier.whole[chest] * Matrix4x4.Translate(hips) * Matrix4x4.Scale(Vector3.one * carriedScale) * Matrix4x4.Translate(-hips);
            Begin(gripCarried);
            Add(gripCarried, carriedClip, phase, 1f);
            Chain(gripCarried, on);
            Vector3[] rest = shape.boneHead;
            Grip[] found = new Grip[2];
            string[] sides = { "R", "L" };
            for (int s = 0; s < 2; s++)
            {
                found[s].bone = -1;
                int hand;
                if (!boneIndex.TryGetValue("hand." + sides[s], out hand))
                {
                    continue;
                }
                Vector3 wrist = gripCarrier.whole[hand].MultiplyPoint3x4(rest[hand]);
                // The carried figure's bone whose length (its head to a child's) passes nearest the wrist; not its arms,
                // which may lie on its own chest under the hand.
                float best = GripMost * GripMost;
                for (int b = 0; b < boneParent.Length; b++)
                {
                    int parent = boneParent[b];
                    if (parent < 0 || armBone[b] || armBone[parent])
                    {
                        continue;
                    }
                    Vector3 a = gripCarried.whole[parent].MultiplyPoint3x4(rest[parent]), e = gripCarried.whole[b].MultiplyPoint3x4(rest[b]);
                    Vector3 ae = e - a;
                    float u = ae.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector3.Dot(wrist - a, ae) / ae.sqrMagnitude) : 0f;
                    float d = (a + ae * u - wrist).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        found[s].bone = parent;
                    }
                }
                if (found[s].bone >= 0)
                {
                    found[s].local = gripCarried.whole[found[s].bone].inverse.MultiplyPoint3x4(wrist);
                }
            }
            frames[frame] = found;
            return found;
        }

        // After Chain: bends the arm on side ("R" or "L") so its wrist goes w (0 .. 1) of the way to target (model units,
        // the whole move in, as JointOf gives), as far as it reaches; the elbow kept in the plane it was bent in and the hand
        // keeping its turn. Call Reached once the arms are done.
        public void Reach(Posed p, Matrix4x4 whole, string side, Vector3 target, float w)
        {
            int upper, fore, hand;
            if (w <= 0f || !boneIndex.TryGetValue("upper_arm." + side, out upper) || !boneIndex.TryGetValue("forearm." + side, out fore)
                || !boneIndex.TryGetValue("hand." + side, out hand))
            {
                return;
            }
            Vector3[] rest = p.shape.boneHead;
            Vector3 shoulder = p.pos[upper], elbow = p.pos[fore], wrist = p.pos[hand];
            Vector3 to = Vector3.Lerp(wrist, whole.inverse.MultiplyPoint3x4(target), Mathf.Clamp01(w));
            float a = (rest[fore] - rest[upper]).magnitude, b = (rest[hand] - rest[fore]).magnitude;
            Vector3 line = to - shoulder;
            float d = Mathf.Clamp(line.magnitude, Mathf.Abs(a - b) * 1.001f + 1e-4f, (a + b) * 0.999f);
            if (line.sqrMagnitude < 1e-8f)
            {
                return;
            }
            Vector3 along = line.normalized;
            to = shoulder + along * d;
            Vector3 bend = Vector3.ProjectOnPlane(elbow - shoulder, along);
            if (bend.sqrMagnitude < 1e-8f)
            {
                bend = Vector3.ProjectOnPlane(Vector3.down, along);   // a straight arm: its elbow down
            }
            float x = (a * a - b * b + d * d) / (2f * d);
            Vector3 bent = shoulder + along * x + bend.normalized * Mathf.Sqrt(Mathf.Max(0f, a * a - x * x));
            Quaternion handTurn = p.turn[hand];
            p.turn[upper] = Quaternion.FromToRotation(elbow - shoulder, bent - shoulder) * p.turn[upper];
            p.turn[fore] = Quaternion.FromToRotation(wrist - elbow, to - bent) * p.turn[fore];
            p.turn[hand] = handTurn;
            p.pos[fore] = bent;
            p.pos[hand] = to;
        }

        // Reach with the wrist moved by (model units, before the whole move) from where Chain left it.
        public void ReachBy(Posed p, Matrix4x4 whole, string side, Vector3 by, float w)
        {
            int hand;
            if (boneIndex.TryGetValue("hand." + side, out hand))
            {
                Reach(p, whole, side, whole.MultiplyPoint3x4(p.pos[hand] + by), w);
            }
        }

        // A cuddled pet (carry_cuddle, anim.txt's cuddle_pet line; walk.PET_ALONG, PET_LIFT, PET_BACK): its trunk's middle
        // PetAlong of the way from the left elbow to the wrist and its feet PetLift (model units) over that line; the clips
        // lay the right hand on a back PetBack (model units) over its feet.
        public float PetAlong = 0.65f;
        public float PetLift = 0.0199f;
        public float PetBack = 0.0894f;

        // After Reach: the bones placed again from the turns it left (each in model space), as Chain left them otherwise.
        public void Reached(Posed p, Matrix4x4 whole)
        {
            int root = boneOrder[0];
            for (int i = boneOrder.Length - 1; i > 0; i--)
            {
                int b = boneOrder[i];
                p.turn[b] = Quaternion.Inverse(p.turn[boneParent[b]]) * p.turn[b];
            }
            Place(p, whole, p.pos[root], 0f);   // (the twist is in the turns already)
        }

        private static readonly string[] TwistBones = { "spine.001", "spine.002", "spine.003" };   // the belly up to the chest
        private static readonly float[] TwistShares = { 0.3f, 0.35f, 0.35f };   // their shares of it, all together 1
        private static readonly string[] LookBones = { "spine.004", "spine.005", "spine.006" };   // the neck up to the head
        private static readonly float[] LookShares = { 0.25f, 0.35f, 0.4f };   // their shares of a look, all together 1

        private static void Sample(Clip c, float phase, float w, Quaternion[] turn, Vector3[] head, float[] mask, float[] weight)
        {
            int n = c.turn.Length;
            float at = Mathf.Repeat(phase, 1f) * n;
            int f0 = Mathf.FloorToInt(at) % n, f1 = (f0 + 1) % n;
            float t = at - Mathf.Floor(at);
            AddFrame(c, f0, w * (1f - t), turn, head, mask);
            AddFrame(c, f1, w * t, turn, head, mask);
            for (int b = 0; weight != null && b < weight.Length; b++)
            {
                weight[b] += w * (mask != null ? mask[b] : 1f);
            }
        }

        private static void AddFrame(Clip c, int frame, float w, Quaternion[] sums, Vector3[] heads, float[] mask)
        {
            if (w <= 0f)
            {
                return;
            }
            Quaternion[] turn = c.turn[frame];
            Vector3[] head = c.head[frame];
            for (int b = 0; b < turn.Length; b++)
            {
                float wb = mask != null ? w * mask[b] : w;
                if (wb <= 0f)
                {
                    continue;
                }
                Quaternion q = turn[b];
                Quaternion sum = sums[b];
                // The same turn has two quaternions; take the one on the side of what is summed so far.
                float side = sum.x * q.x + sum.y * q.y + sum.z * q.z + sum.w * q.w < 0f ? -wb : wb;
                sums[b] = new Quaternion(sum.x + q.x * side, sum.y + q.y * side, sum.z + q.z * side, sum.w + q.w * side);
                if (heads != null)
                {
                    heads[b] += head[b] * wb;
                }
            }
        }

        private static Quaternion Normalised(Quaternion q)
        {
            float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return len > 1e-6f ? new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len) : Quaternion.identity;
        }

        // Ends the blend and works out the bones. The turns are each bone's on top of its parent's, so they are put
        // together down the chain, parents first; each bone turns about its own head, and that head is placed from its
        // parent's by the parent's turn of the gap between them in this shape. Only the root keeps the clip's place
        // (plus its own move for the shape), so a shape with moved joints keeps the limbs joined and the feet under the
        // hips. The body and face are posed here; the garments when drawn.
        public void End(Posed p)
        {
            End(p, Matrix4x4.identity);
        }

        // Where a bone's head (its joint: hand.R's is the right wrist) is as End left it, model units with the whole move
        // in; false if the export has no bone of that name.
        public bool JointOf(Posed p, string bone, out Vector3 at)
        {
            int b;
            if (!boneIndex.TryGetValue(bone, out b))
            {
                at = Vector3.zero;
                return false;
            }
            at = p.whole[b].MultiplyPoint3x4(p.shape.boneHead[b]);
            return true;
        }

        // A bone's move from rest as End left it (model units, the whole move in): what a thing riding that bone is moved
        // by when it was placed in the rest pose (a carried person on the chest); false if the export has no such bone.
        public bool BoneOf(Posed p, string bone, out Matrix4x4 move)
        {
            int b;
            if (!boneIndex.TryGetValue(bone, out b))
            {
                move = Matrix4x4.identity;
                return false;
            }
            move = p.whole[b];
            return true;
        }

        // Where a clip puts the root's head (the hips) in its first frame, cells; zero if there is no such clip.
        public Vector3 RootOf(string clip)
        {
            Clip c;
            return clips.TryGetValue(clip, out c) ? c.head[0][boneOrder[0]] : Vector3.zero;
        }

        // Where a clip puts a bone's head in its first frame, cells for a figure of size 1 (the export's, in the base shape);
        // zero if there is no such clip or bone.
        public Vector3 HeadOf(string clip, string bone)
        {
            Clip c;
            int b;
            return clips.TryGetValue(clip, out c) && boneIndex.TryGetValue(bone, out b) ? c.head[0][b] : Vector3.zero;
        }

        // How far the hips of a clip's first frame, turned by laid, face round from those of another's first frame
        // (degrees about the vertical, as a figure's yaw turns it): by the way the hips' rest forward points.
        public float FacingFrom(string clip, Quaternion laid, string from)
        {
            Clip c, f;
            if (!clips.TryGetValue(clip, out c) || !clips.TryGetValue(from, out f))
            {
                return 0f;
            }
            int root = boneOrder[0];
            Vector3 a = f.turn[0][root] * Vector3.forward;
            Vector3 b = laid * c.turn[0][root] * Vector3.forward;
            a.y = b.y = 0f;
            return a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f ? 0f : Vector3.SignedAngle(a, b, Vector3.up);
        }

        // The same with the whole figure moved by whole (a turn and a shift, model units, no scale) after it is posed: laid
        // on its back or tipped face down. The face, hair and outline go with the bones.
        public void End(Posed p, Matrix4x4 whole)
        {
            Chain(p, whole);
            Finish(p);
        }

        // The first half of End: the blend ended and the bones placed (p.turn in model space, p.pos, p.whole and
        // p.bones), but nothing posed yet, so a ragdoll can read the pose and put its own in its place (Override) first.
        public void Chain(Posed p, Matrix4x4 whole)
        {
            if (p.total <= 0f)
            {
                Add(p, "idle", 0f, 1f);
            }
            for (int b = 0; b < p.turn.Length; b++)
            {
                Quaternion q = Normalised(p.turn[b]);
                if (p.overWeight[b] > 0f)
                {
                    q = Quaternion.Slerp(q, Normalised(p.overTurn[b]), Mathf.Clamp01(p.overWeight[b]));
                }
                if (p.topWeight[b] > 0f)
                {
                    q = Quaternion.Slerp(q, Normalised(p.topTurn[b]), Mathf.Clamp01(p.topWeight[b]));
                }
                p.turn[b] = p.nudge[b] * q;
            }
            int root = boneOrder[0];
            if (p.recline != 0f)
            {
                Bend(p, p.recline);
            }
            Place(p, whole, p.head[root] / p.total + p.shape.boneMove[root], p.twist, p.look, p.lean);
            if (p.standOn >= 0)
            {
                OverFoot(p, whole);
            }
            if (p.crouch > 0f)
            {
                LetDown(p, whole);
            }
        }

        // Bends the trunk forward over the hips by degrees (about the figure's x, its side to side) whatever the clips do,
        // the thighs turned back by as much, so the legs lie as they did: a figure on its back with its head propped up
        // (ReclineOf). Before Place, on the turns as Chain blended them.
        public void Recline(Posed p, float degrees)
        {
            p.recline = degrees;
        }

        private void Bend(Posed p, float degrees)
        {
            int root = boneOrder[0];
            Quaternion was = p.turn[root];
            Quaternion bent = Quaternion.AngleAxis(degrees, Vector3.right) * was;
            Quaternion back = Quaternion.Inverse(bent) * was;
            foreach (string side in new[] { "L", "R" })
            {
                int thigh;
                if (boneIndex.TryGetValue("thigh." + side, out thigh) && boneParent[thigh] == root)
                {
                    p.turn[thigh] = back * p.turn[thigh];
                }
            }
            p.turn[root] = bent;
        }

        // How far (degrees, Recline) a figure laid on its back in clip's first frame wants its trunk bent forward so the
        // back comes down to the ground with the head: the chibi's head reaches further behind it than the back does, so
        // rested on its lowest point it lay on the back of its head with the body floating over the ground. The least bend
        // that brings the trunk as low as the head, at most ReclineMost; none if the trunk is down already (a figure on its
        // side, a small head). Worked out once per shape (the head shrink, the body keys) and clip, on the body at rest
        // skinned here, the trunk and head turned rigidly about the hips as Bend turns them and the legs left.
        public float ReclineOf(Shape shape, string clip)
        {
            float found;
            if (shape == null || !clips.ContainsKey(clip))
            {
                return 0f;
            }
            if (reclines.TryGetValue((shape, clip), out found))
            {
                return found;
            }
            Measure(shape, clip);
            return reclines[(shape, clip)];
        }

        // Where the middle of the body of a figure laid on its back in clip's first frame comes along it (cells for a figure
        // of size 1, laid down as WalkFigures lays a lying clip, before it is moved along), with the trunk bent by
        // ReclineOf: so lying clips can be lined up with each other in a bed (their hips are in one place, but a clip with
        // straight legs reaches further toward the feet). Zero if there is no such clip.
        public float LaidMiddleOf(Shape shape, string clip)
        {
            float found;
            if (shape == null || !clips.ContainsKey(clip))
            {
                return 0f;
            }
            if (!laidMiddles.TryGetValue((shape, clip), out found))
            {
                Measure(shape, clip);
                found = laidMiddles[(shape, clip)];
            }
            return found;
        }

        // Works out ReclineOf and LaidMiddleOf for a shape and clip.
        private void Measure(Shape shape, string clip)
        {
            float found;
            if (reclinePose == null)
            {
                reclinePose = NewPosed();
            }
            Posed p = reclinePose;
            p.shape = shape;
            Begin(p);
            Add(p, clip, 0f, 1f);
            Matrix4x4 laid = Matrix4x4.Rotate(Quaternion.AngleAxis(-90f, Vector3.right));   // as WalkFigures lays a lying clip down
            Chain(p, laid);
            Piece body = pieces[0];
            Vector3[] rest = ShapedPiece(shape, 0).rest;
            List<Vector2> head = new List<Vector2>(), trunk = new List<Vector2>(), bent = new List<Vector2>();
            float legsAlong = 0f;
            Vector3 hips = laid.MultiplyPoint3x4(p.pos[boneOrder[0]]);
            for (int i = 0; i < rest.Length; i++)
            {
                Vector3 v = Vector3.zero;
                float onHead = 0f, onTrunk = 0f, onLegs = 0f;
                for (int k = body.firstWeight[i]; k < body.firstWeight[i + 1]; k++)
                {
                    int b = body.weightBone[k];
                    float w = body.weight[k];
                    v += p.bones[b].MultiplyPoint3x4(rest[i]) * w;
                    if (legBone[b])
                    {
                        onLegs += w;
                    }
                    if (b == headBone)
                    {
                        onHead += w;
                    }
                    else if (trunkBone[b])
                    {
                        onTrunk += w;
                    }
                }
                // Laid down, the bend turns about x through the hips: only y and z matter.
                Vector2 yz = new Vector2(v.y - hips.y, v.z - hips.z);
                // For the middle: the legs stay as they lie under the bend (Bend turns the thighs back), the rest turns with it.
                if (onLegs > 0.5f)
                {
                    legsAlong += yz.y;
                }
                else
                {
                    bent.Add(yz);
                }
                if (onHead > 0.5f)
                {
                    head.Add(yz);
                }
                else if (onTrunk > 0.5f)
                {
                    trunk.Add(yz);
                }
            }
            found = 0f;
            if (head.Count > 0 && trunk.Count > 0)
            {
                for (float a = 0f; a <= ReclineMost; a += ReclineStep)
                {
                    float c = Mathf.Cos(a * Mathf.Deg2Rad), s = Mathf.Sin(a * Mathf.Deg2Rad);
                    if (LowestTurned(trunk, c, s) <= LowestTurned(head, c, s))
                    {
                        break;
                    }
                    found = a + ReclineStep;
                }
                found = Mathf.Min(found, ReclineMost);
            }
            reclines[(shape, clip)] = found;
            float c2 = Mathf.Cos(found * Mathf.Deg2Rad), s2 = Mathf.Sin(found * Mathf.Deg2Rad);
            float along = legsAlong;
            for (int i = 0; i < bent.Count; i++)
            {
                along += bent[i].x * s2 + bent[i].y * c2;   // z turned about x as LowestTurned turns y
            }
            laidMiddles[(shape, clip)] = rest.Length > 0 ? hips.z + along / rest.Length : 0f;
        }

        // The lowest of these (y, z about the hips) turned about x by the angle whose cosine and sine are c and s, as
        // Quaternion.AngleAxis(a, Vector3.right) turns them.
        private static float LowestTurned(List<Vector2> points, float c, float s)
        {
            float lowest = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                lowest = Mathf.Min(lowest, points[i].x * c - points[i].y * s);
            }
            return lowest;
        }

        private readonly Dictionary<(Shape, string), float> reclines = new Dictionary<(Shape, string), float>();
        private readonly Dictionary<(Shape, string), float> laidMiddles = new Dictionary<(Shape, string), float>();
        private Posed reclinePose;   // a scratch figure ReclineOf poses
        private const float ReclineMost = 30f;   // degrees at most
        private const float ReclineStep = 0.5f;

        // Lets the hips down and back over the planted feet by p.crouch (Crouch), each leg bent to keep its foot where it
        // was, its knee forward and out in the plane it was already bent in; the trunk, arms and head ride the hips
        // unturned. A leg that can't reach (a foot already far under) lets the hips down no further than it reaches.
        private void LetDown(Posed p, Matrix4x4 whole)
        {
            int root = boneOrder[0];
            Vector3[] rest = p.shape.boneHead;
            float shift = 0f;
            int[] legs = new int[6];
            int n = 0;
            foreach (string side in new[] { "L", "R" })
            {
                int thigh, shin, foot;
                if (!boneIndex.TryGetValue("thigh." + side, out thigh) || !boneIndex.TryGetValue("shin." + side, out shin) || !boneIndex.TryGetValue("foot." + side, out foot))
                {
                    return;
                }
                legs[n++] = thigh;
                legs[n++] = shin;
                legs[n++] = foot;
                shift += (rest[shin] - rest[thigh]).magnitude + (rest[foot] - rest[shin]).magnitude;
            }
            shift *= 0.5f * p.crouch;   // leg lengths to cells, by how deep
            // Back along the way the hips face (flat), not the figure's: a stance turned side on goes back over its heels.
            Vector3 facing = p.turn[root] * Vector3.forward;
            facing.y = 0f;
            facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward;
            Vector3 move = Vector3.down * (shift * CrouchDrop) - facing * (shift * CrouchBack);
            for (int i = 0; i < 6; i += 3)
            {
                int thigh = legs[i], shin = legs[i + 1], foot = legs[i + 2];
                Vector3 hip = p.pos[thigh], knee = p.pos[shin], ankle = p.pos[foot];
                float upper = (rest[shin] - rest[thigh]).magnitude, lower = (rest[foot] - rest[shin]).magnitude, reach = (upper + lower) * 0.999f;
                Vector3 to = hip + move;
                Vector3 line = ankle - to;
                float d = line.magnitude;
                if (d > reach || d < 1e-4f)
                {
                    continue;   // left as the clip had it: the other leg's bend still lets the hips down
                }
                Vector3 along = line / d;
                Vector3 outward = p.turn[root] * new Vector3(rest[thigh].x - rest[root].x, 0f, 0f);
                Vector3 bend = Vector3.ProjectOnPlane(knee - hip, along);
                Vector3 front = Vector3.ProjectOnPlane(facing, along).normalized;
                bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : front;
                bend = Vector3.ProjectOnPlane(bend + outward.normalized * CrouchSplay, along);
                if (bend.sqrMagnitude < 1e-8f)
                {
                    bend = front;
                }
                float a = (upper * upper - lower * lower + d * d) / (2f * d);
                Vector3 bent = to + along * a + bend.normalized * Mathf.Sqrt(Mathf.Max(0f, upper * upper - a * a));
                p.turn[thigh] = Quaternion.FromToRotation(knee - hip, bent - to) * p.turn[thigh];
                p.turn[shin] = Quaternion.FromToRotation(ankle - knee, ankle - bent) * p.turn[shin];
                // (the foot keeps its turn in model space, so it stays flat as it was)
            }
            // Back to each bone's turn on its parent's, children first, for Place to put together again (the twist is in them).
            for (int i = boneOrder.Length - 1; i > 0; i--)
            {
                int b = boneOrder[i];
                p.turn[b] = Quaternion.Inverse(p.turn[boneParent[b]]) * p.turn[b];
            }
            Place(p, whole, p.pos[root] + move, 0f);
        }

        // Moves the figure sideways so its hips are over the foot it stands on (Balance), the foot staying just where and
        // how the clips put it and that leg bent to reach it, its knee in the same plane; let down if it can't reach. A clip
        // with the hips there already or past it (the hop), or a foot crossed over the middle, is left as it is.
        private void OverFoot(Posed p, Matrix4x4 whole)
        {
            int thigh = p.standOn, shin = -1, foot = -1;
            for (int b = 0; b < boneParent.Length && (shin < 0 || foot < 0); b++)
            {
                if (boneParent[b] == thigh)
                {
                    shin = b;
                }
                else if (shin >= 0 && boneParent[b] == shin)
                {
                    foot = b;
                }
            }
            if (shin < 0 || foot < 0)
            {
                return;
            }
            int root = boneOrder[0];
            Vector3[] rest = p.shape.boneHead;
            Vector3 hip = p.pos[thigh], knee = p.pos[shin], ankle = p.pos[foot];
            float move = (rest[root].x + (ankle.x - rest[root].x) * BalanceShare - p.pos[root].x) * p.balance;
            if (move * (rest[thigh].x - rest[root].x) <= 0f)
            {
                return;
            }
            float upper = (rest[shin] - rest[thigh]).magnitude, lower = (rest[foot] - rest[shin]).magnitude, reach = (upper + lower) * 0.999f;
            Vector3 shift = new Vector3(move, 0f, 0f);
            Vector3 to = hip + shift, line = ankle - to;
            if (line.sqrMagnitude > reach * reach)
            {
                float flat = line.x * line.x + line.z * line.z;
                if (flat >= reach * reach)
                {
                    return;
                }
                shift.y = ankle.y + Mathf.Sqrt(reach * reach - flat) - hip.y;
                to = hip + shift;
                line = ankle - to;
            }
            float d = line.magnitude;
            if (d < 1e-4f)
            {
                return;
            }
            Vector3 along = line / d;
            Vector3 bend = Vector3.ProjectOnPlane(knee - hip, along);
            if (bend.sqrMagnitude < 1e-8f)
            {
                bend = Vector3.ProjectOnPlane(Vector3.forward, along);   // a straight leg: its knee to the front
            }
            float a = (upper * upper - lower * lower + d * d) / (2f * d);
            Vector3 bent = to + along * a + bend.normalized * Mathf.Sqrt(Mathf.Max(0f, upper * upper - a * a));
            p.turn[thigh] = Quaternion.FromToRotation(knee - hip, bent - to) * p.turn[thigh];
            p.turn[shin] = Quaternion.FromToRotation(ankle - knee, ankle - bent) * p.turn[shin];
            // Back to each bone's turn on its parent's, children first, for Place to put together again (the twist is in them).
            for (int i = boneOrder.Length - 1; i > 0; i--)
            {
                int b = boneOrder[i];
                p.turn[b] = Quaternion.Inverse(p.turn[boneParent[b]]) * p.turn[b];
            }
            Place(p, whole, p.pos[root] + shift, 0f);
        }

        // Puts the turns (each on top of its parent's) together down the chain from the root's head at root (model units,
        // before whole), and works out each bone's matrix.
        private void Place(Posed p, Matrix4x4 whole, Vector3 root, float twist, float look = 0f, float lean = 0f)
        {
            Vector3[] shapedHead = p.shape.boneHead;
            for (int i = 0; i < boneOrder.Length; i++)
            {
                int b = boneOrder[i], parent = boneParent[b];
                if (parent < 0)
                {
                    p.pos[b] = root;
                }
                else
                {
                    p.pos[b] = p.pos[parent] + p.turn[parent] * (shapedHead[b] - shapedHead[parent]);
                    p.turn[b] = p.turn[parent] * p.turn[b];   // now in model space: the parent's was put together before it
                }
                if (twist != 0f && twistShare[b] > 0f)
                {
                    // Its share of the twist, about the vertical through its own head; the bones above take it on from here.
                    p.turn[b] = Quaternion.AngleAxis(twist * twistShare[b], Vector3.up) * p.turn[b];
                }
                if (lean != 0f && twistShare[b] > 0f)
                {
                    // Its share of the lean, about the way the figure faces (model space's +z): a turn about it by +
                    // degrees takes up toward -x, the figure's left.
                    p.turn[b] = Quaternion.AngleAxis(lean * twistShare[b], Vector3.forward) * p.turn[b];
                }
                if (look != 0f && lookShare[b] > 0f)
                {
                    p.turn[b] = Quaternion.AngleAxis(look * lookShare[b], Vector3.up) * p.turn[b];
                }
                Quaternion q = p.turn[b];
                p.whole[b] = whole * Matrix4x4.TRS(p.pos[b] - q * shapedHead[b], q, Vector3.one);
                p.bones[b] = p.whole[b];
                if (p.thin[b] < 0 && parent >= 0 && p.thin[parent] >= 0 && p.thinAll[parent])
                {
                    p.thin[b] = p.thin[parent];   // a peg's foot goes with its shin
                    p.thinBy[b] = p.thinBy[parent];
                    p.thinAll[b] = true;
                }
                if (p.thin[b] >= 0)
                {
                    // A peg or pylon (Prosthetics.cs): drawn in at rest about the line of the bone it was thinned at, then posed.
                    p.bones[b] = p.whole[b] * Thinned(shapedHead, p.thin[b], p.thinBy[b]);
                }
                if (p.cut[b] || (parent >= 0 && p.cut[parent]))
                {
                    // Everything on it put at the joint it was cut at: its own head, or the one its parent was cut at.
                    Vector3 joint = p.cut[b] && (parent < 0 || !p.cut[parent])
                        ? p.whole[b].MultiplyPoint3x4(shapedHead[b]) : p.bones[parent].GetColumn(3);
                    Matrix4x4 squashed = Matrix4x4.zero;
                    squashed.SetColumn(3, new Vector4(joint.x, joint.y, joint.z, 1f));
                    p.bones[b] = squashed;
                    p.cut[b] = true;   // so its own children are cut too
                }
                p.dress[b] = p.thin[b] >= 0 && !p.cut[b] ? p.whole[b] : p.bones[b];
            }
        }

        // Turns the pose Chain left toward another by w (0 .. 1): turn, each bone's in model space, and root, the root's
        // head, both in a space that frame takes to the one Chain was given (whole in): a ragdoll's, drawn where it lies
        // and the way it lies. Each bone's own nudge (a flinch, a breath) is put on top again, as Chain puts it.
        public void Override(Posed p, Quaternion[] turn, Vector3 root, Matrix4x4 frame, float w)
        {
            if (w <= 0f)
            {
                return;
            }
            Quaternion spin = frame.rotation;
            int r = boneOrder[0];
            Vector3 at = Vector3.Lerp(p.whole[r].MultiplyPoint3x4(p.shape.boneHead[r]), frame.MultiplyPoint3x4(root), w);
            // Each bone's turn on its parent's, in both poses, blended there (the root in model space): blended in model
            // space bone by bone, a joint between two poses far apart (lying face down, getting up off its back) took
            // turns on its parent neither pose has, and the skin about it stretched. Place rebuilds the chain from them
            // with the shape's own bone lengths.
            for (int b = 0; b < turn.Length; b++)
            {
                int parent = boneParent[b];
                Quaternion canned = parent < 0 ? p.whole[b].rotation : Quaternion.Inverse(p.whole[parent].rotation) * p.whole[b].rotation;
                Quaternion doll = parent < 0 ? spin * turn[b] : Quaternion.Inverse(turn[parent]) * turn[b];
                p.turn[b] = Quaternion.Slerp(canned, p.nudge[b] * doll, w);   // the canned pose has its nudge already
            }
            Place(p, Matrix4x4.identity, at, 0f);   // (the canned pose's twist is in p.whole already; a cut limb is cut again just as Chain cut it)
        }

        // The second half of End: the body and face posed by the bones Chain (or Override) placed.
        public void Finish(Posed p)
        {
            // Its turn not come round: it stays as last posed, and what rides its bones (a weapon, a load) with it, so the hand
            // stays on the handle. It still walks and turns smoothly, as where it is drawn and its yaw are set at each draw.
            if (p.shape == p.posedShape && !p.poseEveryDraw && WaitsItsTurn(p))
            {
                Array.Copy(p.shownBones, p.bones, p.bones.Length);
                Array.Copy(p.shownWhole, p.whole, p.whole.Length);
                Array.Copy(p.shownPos, p.pos, p.pos.Length);
                return;
            }
            Array.Copy(p.bones, p.shownBones, p.bones.Length);
            Array.Copy(p.whole, p.shownWhole, p.whole.Length);
            Array.Copy(p.pos, p.shownPos, p.pos.Length);
            p.posedTime = Time.realtimeSinceStartup;
            // Nothing moved (the game paused, a corpse): the meshes are right as they are. Setting a mesh's vertices costs far
            // more here than posing them, as the engine rebuilds the whole mesh and makes new buffers for it on the GPU.
            bool moved = ReadBones(p.bones, p.boneRows) | (p.pylons && ReadBones(p.dress, p.dressRows)) || p.shape != p.posedShape;
            if (p.pylons != p.pylonsPosed)
            {
                moved = true;   // the clothes go over to the other bones
                p.pylonsPosed = p.pylons;
            }
            if (moved)
            {
                p.posedAt++;
                p.posedShape = p.shape;
            }
            Pose(p, 0);
            if (p.face != null && GpuSkinning)
            {
                FaceOnGpu(p);
            }
            else if (p.face != null && (moved || p.face.bones != null))
            {
                FaceLeaveGpu(p.face);
                Piece piece = pieces[0];
                Vector3[] faceShaped = p.shape.faceRest;
                Vector3[] bodyNormal = ShapedPiece(p.shape, 0).normal;
                float[] f = p.boneRows;
                for (int i = 0; i < faceShaped.Length; i++)
                {
                    int from = faceFrom[i];
                    Vector3 r = faceShaped[i], d = bodyNormal[from];
                    float x = 0f, y = 0f, z = 0f, nx = 0f, ny = 0f, nz = 0f;
                    for (int k = piece.firstWeight[from]; k < piece.firstWeight[from + 1]; k++)
                    {
                        int o = 12 * piece.weightBone[k];
                        float w = piece.weight[k];
                        x += (f[o] * r.x + f[o + 1] * r.y + f[o + 2] * r.z + f[o + 3]) * w;
                        y += (f[o + 4] * r.x + f[o + 5] * r.y + f[o + 6] * r.z + f[o + 7]) * w;
                        z += (f[o + 8] * r.x + f[o + 9] * r.y + f[o + 10] * r.z + f[o + 11]) * w;
                        nx += (f[o] * d.x + f[o + 1] * d.y + f[o + 2] * d.z) * w;
                        ny += (f[o + 4] * d.x + f[o + 5] * d.y + f[o + 6] * d.z) * w;
                        nz += (f[o + 8] * d.x + f[o + 9] * d.y + f[o + 10] * d.z) * w;
                    }
                    p.faceVerts[i] = new Vector3(x, y, z);
                    // The body vertex it was made from, posed as Pose poses it (worked out here, as on the graphics card the
                    // body's normals aren't), so it shades as the head under it.
                    p.faceNormals[i] = new Vector3(nx, ny, nz);
                }
                p.face.face.vertices = p.faceVerts;
                p.face.mouth.vertices = p.faceVerts;
                p.face.face.normals = p.faceNormals;
                p.face.mouth.normals = p.faceNormals;
                if (p.face.lowUsed)
                {
                    p.face.mouthLow.vertices = p.faceVerts;
                    p.face.mouthLow.normals = p.faceNormals;
                }
                p.face.centre = p.bones[headBone].MultiplyPoint3x4(p.shape.faceCentre);
            }
        }

        private const int PoseEveryFigures = 4;   // figures on screen up to which each is posed every draw
        private const int PoseEverySecondFigures = 12;   // and every second draw; past this, every PoseEveryMost
        private const int PoseEveryMost = 3;
        private const float PoseLongest = 1f / 15f;   // real seconds it waits at most, so a low frame rate doesn't leave limbs jerking

        public static bool FewerPoses   // false: every figure is posed every draw, however many are on screen (the mod's options set it)
        {
            get { return SettingsMod.Settings != null && SettingsMod.Settings.fewerPoses; }
        }

        private static int PoseEvery
        {
            get { return !FewerPoses || figuresLast <= PoseEveryFigures ? 1 : figuresLast <= PoseEverySecondFigures ? 2 : PoseEveryMost; }
        }

        // True if this draw of the figure keeps its last pose: posing is most of what a figure costs here, in the engine's
        // mesh rebuild and new GPU buffers as well as the skinning, and with many figures in view no one limb is looked at.
        private static bool WaitsItsTurn(Posed p)
        {
            int every = PoseEvery;
            if (every <= 1)
            {
                return false;
            }
            if (++p.drawsHeld < every && Time.realtimeSinceStartup - p.posedTime < PoseLongest)
            {
                return true;
            }
            p.drawsHeld = 0;
            return false;
        }

        // Copies each bone's top three rows into rows, as Matrix4x4.MultiplyPoint3x4 transposes the whole matrix on every call
        // in this engine, which is most of the time skinning takes. True if any of them changed.
        private static bool ReadBones(Matrix4x4[] bones, float[] rows)
        {
            bool changed = false;
            for (int b = 0; b < bones.Length; b++)
            {
                Matrix4x4 m = bones[b];
                int o = 12 * b;
                changed |= Row(rows, o, m.m00, m.m01, m.m02, m.m03);
                changed |= Row(rows, o + 4, m.m10, m.m11, m.m12, m.m13);
                changed |= Row(rows, o + 8, m.m20, m.m21, m.m22, m.m23);
            }
            return changed;
        }

        private static bool Row(float[] rows, int o, float a, float b, float c, float d)
        {
            bool changed = rows[o] != a || rows[o + 1] != b || rows[o + 2] != c || rows[o + 3] != d;
            rows[o] = a;
            rows[o + 1] = b;
            rows[o + 2] = c;
            rows[o + 3] = d;
            return changed;
        }

        // The lowest point of the body as End left it, and the highest that is not the head (the front of the chest of a
        // figure on its back), model units.
        public void Extent(Posed p, out float lowest, out float bodyTop)
        {
            if (p.extentAt == p.posedAt)
            {
                lowest = p.lowest;
                bodyTop = p.bodyTop;
                return;
            }
            Vector3[] verts = PosedOnCpu(p, 0).verts;
            Piece body = pieces[0];
            lowest = float.MaxValue;
            bodyTop = float.MinValue;
            for (int i = 0; i < verts.Length; i++)
            {
                float y = verts[i].y;
                lowest = Mathf.Min(lowest, y);
                if (y > bodyTop && HeadWeight(body, i) < 0.5f)
                {
                    bodyTop = y;
                }
            }
            p.extentAt = p.posedAt;
            p.lowest = lowest;
            p.bodyTop = bodyTop;
        }

        // The body's vertices as End left them (model units, before the figure's place, yaw and size), worked out on the CPU.
        // The figure's own: don't keep or change them.
        public Vector3[] BodyVerts(Posed p)
        {
            return PosedOnCpu(p, 0).verts;
        }

        // How high the seat is under the figure as End left it (upright), model units: the lowest point of the body no
        // further forward than the hips, so the thighs, shins and feet held out in front are not sat on.
        public float SeatOf(Posed p)
        {
            Vector3[] verts = PosedOnCpu(p, 0).verts;
            float hips = p.pos[boneOrder[0]].z + 0.02f;
            float seat = float.MaxValue;
            for (int i = 0; i < verts.Length; i++)
            {
                if (verts[i].z <= hips)
                {
                    seat = Mathf.Min(seat, verts[i].y);
                }
            }
            return seat < float.MaxValue ? seat : 0f;
        }

        // The height of the middle of the head in this shape standing at rest, model units.
        public float HeadHeight(Shape shape)
        {
            return shape.faceCentre.y;
        }

        private float HeadWeight(Piece piece, int vertex)
        {
            for (int k = piece.firstWeight[vertex]; k < piece.firstWeight[vertex + 1]; k++)
            {
                if (piece.weightBone[k] == headBone)
                {
                    return piece.weight[k];
                }
            }
            return 0f;
        }

        // What the figure has on this draw, which places what it carries on its back. Before any of it is drawn.
        public void Wearing(Posed p, GarmentMask mask, bool gearPack, float packScale, float backpackScale)
        {
            if (mask != p.wornMask || (backpackScale != p.backpackScale && (mask & PackMask) != 0))
            {
                p.posedAt++;   // a backpack is placed by what is under it and its size (Packed), not only by the bones
            }
            p.wornMask = mask;
            p.gearPack = gearPack;
            p.packScale = packScale;
            p.backpackScale = backpackScale;
        }

        // Moves one piece's meshes to the pose worked out by End (only if it has changed), making them on first use.
        private PieceMeshes Pose(Posed p, int index)
        {
            PieceMeshes m = p.pieces[index];
            if (m == null)
            {
                m = NewPieceMeshes(pieces[index]);
                p.pieces[index] = m;
            }
            m.dressed = index != 0;
            PackRig rig = (PackMask & (index <= MaxHiding ? GarmentBit(index) : 0)) != 0 ? PackRigOf(index) : null;
            if (rig != null)
            {
                Packed(p, index, rig, m);
                return m;
            }
            if (pieces[index].heldOnBack)
            {
                Shield(p, index, m);
                return m;
            }
            Pose(p, pieces[index], OverHairShape(p.shape, index, GarmentShape(p.shape, index, p.wornMask), p.hairOver), m);
            return m;
        }

        private static void Pose(Posed p, Piece piece, PieceShape shape, PieceMeshes m)
        {
            if (GpuSkinning)
            {
                GpuPose(piece, shape, m);
                return;
            }
            if (m.gpuShape != null)
            {
                LeaveGpu(m);
            }
            if (m.posedAt == p.posedAt)
            {
                return;
            }
            m.posedAt = p.posedAt;
            Skin(p, piece, shape, m, true);
            m.solid.vertices = m.verts;
            m.solid.normals = m.normals;
            m.outline.vertices = m.hulled;
        }

        // Works out the piece's vertices and normals as the bones pose them into m's verts and normals, and with hulls its
        // outline's into hulled too.
        private static void Skin(Posed p, Piece piece, PieceShape shape, PieceMeshes m, bool hulls)
        {
            Vector3[] rest = shape.rest, hull = shape.hull, normal = shape.normal;
            float[] f = m.dressed && p.pylons ? p.dressRows : p.boneRows;
            int[] first = piece.firstWeight, boneOf = piece.weightBone;
            float[] weight = piece.weight;
            Vector3[] verts = m.verts, hulled = m.hulled, normals = m.normals;
            bool own = piece.shell == null;   // outlined by its own mesh
            for (int i = 0; i < rest.Length; i++)
            {
                Vector3 r = rest[i], d = normal[i], o = own ? hull[i] : Vector3.zero;
                float vx = 0f, vy = 0f, vz = 0f, hx = 0f, hy = 0f, hz = 0f, nx = 0f, ny = 0f, nz = 0f;
                for (int k = first[i]; k < first[i + 1]; k++)
                {
                    int b = 12 * boneOf[k];
                    float w = weight[k];
                    float m00 = f[b], m01 = f[b + 1], m02 = f[b + 2];
                    float m10 = f[b + 4], m11 = f[b + 5], m12 = f[b + 6];
                    float m20 = f[b + 8], m21 = f[b + 9], m22 = f[b + 10];
                    vx += (m00 * r.x + m01 * r.y + m02 * r.z + f[b + 3]) * w;
                    vy += (m10 * r.x + m11 * r.y + m12 * r.z + f[b + 7]) * w;
                    vz += (m20 * r.x + m21 * r.y + m22 * r.z + f[b + 11]) * w;
                    hx += (m00 * o.x + m01 * o.y + m02 * o.z) * w;
                    hy += (m10 * o.x + m11 * o.y + m12 * o.z) * w;
                    hz += (m20 * o.x + m21 * o.y + m22 * o.z) * w;
                    nx += (m00 * d.x + m01 * d.y + m02 * d.z) * w;   // left unnormalised: the shaders normalise per pixel
                    ny += (m10 * d.x + m11 * d.y + m12 * d.z) * w;
                    nz += (m20 * d.x + m21 * d.y + m22 * d.z) * w;
                }
                verts[i] = new Vector3(vx, vy, vz);
                if (own && hulls)
                {
                    hulled[i] = new Vector3(vx + hx, vy + hy, vz + hz);
                }
                normals[i] = new Vector3(nx, ny, nz);
            }
            if (!own && hulls)
            {
                Piece shell = piece.shell;
                Vector3[] shellRest = shape.shell.rest, shellHull = shape.shell.hull;
                for (int i = 0; i < shellRest.Length; i++)
                {
                    Vector3 r = shellRest[i], o = shellHull[i];
                    float vx = 0f, vy = 0f, vz = 0f;
                    for (int k = shell.firstWeight[i]; k < shell.firstWeight[i + 1]; k++)
                    {
                        int b = 12 * shell.weightBone[k];
                        float w = shell.weight[k];
                        vx += (f[b] * (r.x + o.x) + f[b + 1] * (r.y + o.y) + f[b + 2] * (r.z + o.z) + f[b + 3]) * w;
                        vy += (f[b + 4] * (r.x + o.x) + f[b + 5] * (r.y + o.y) + f[b + 6] * (r.z + o.z) + f[b + 7]) * w;
                        vz += (f[b + 8] * (r.x + o.x) + f[b + 9] * (r.y + o.y) + f[b + 10] * (r.z + o.z) + f[b + 11]) * w;
                    }
                    hulled[i] = new Vector3(vx, vy, vz);
                }
            }
        }

        // The most garments HideUnder can tell apart (bits of an Int128, GarmentMask.cs, the body's left out, and the sign's, so a mask is never
        // below 0).
        public const int MaxHiding = 126;

        // Leaves out of the body the triangles under the garments in mask (bit g: garment g is worn), so skin can't show
        // through where the two don't bend quite alike (the inside of a bent knee), and out of its outline also the head
        // inside the shell of the hair of hairStyle (null: none), which outlines the head itself. Call after End and before
        // drawing the body; the triangle lists for each mix are made once. The normals and outline hull are still made
        // from the whole body, so the skin left showing at a cuff shades and outlines as it did.
        public void HideUnder(Posed p, GarmentMask mask, HairDef hairStyle)
        {
            PieceMeshes m = Pose(p, 0);
            Piece hair = WornHair(Hair(hairStyle), mask, p);
            if (hair != null && hair.shell == null)
            {
                hair = null;
            }
            if (m.hidden == mask && m.hiddenHair == hair && m.tucked == p.tucked && m.bare == p.bare)
            {
                return;
            }
            // Without working out the bounds again: they were set once for culling (NewMesh), roomy enough for any pose.
            if (m.hidden != mask || m.tucked != p.tucked || m.bare != p.bare)
            {
                m.solid.SetTriangles(Tucked(p, BodyTris(mask, p.bare), pieces[0]), 0, false);
            }
            m.hidden = mask;
            m.hiddenHair = hair;
            m.tucked = p.tucked;
            m.bare = p.bare;
            m.outline.SetTriangles(Tucked(p, OutlineTris(mask, hair, p.bare), pieces[0]), 0, false);
        }

        private int[] OutlineTris(GarmentMask mask, Piece hair, int bare)
        {
            if (hair == null || hair.shell.hides.Length == 0)
            {
                return BodyTris(mask, bare);
            }
            (GarmentMask, Piece, int) key = (mask, hair, bare);
            int[] tris;
            if (!outlineTris.TryGetValue(key, out tris))
            {
                bool[] hide = HiddenBy(mask, bare);
                foreach (int k in hair.shell.hides)
                {
                    hide[k] = true;
                }
                tris = Kept(pieces[0].tris, hide);
                outlineTris[key] = tris;
            }
            return tris;
        }

        private int[] BodyTris(GarmentMask mask, int bare)
        {
            int[] tris;
            if (mask == 0)
            {
                return pieces[0].tris;
            }
            if (!bodyTris.TryGetValue((mask, bare), out tris))
            {
                tris = Kept(pieces[0].tris, HiddenBy(mask, bare));
                bodyTris[(mask, bare)] = tris;
            }
            return tris;
        }

        // The body triangles under the garments in mask; the shoes' not on a bare foot (bare, as Posed.bare).
        private bool[] HiddenBy(GarmentMask mask, int bare = 0)
        {
            bool[] hide = new bool[pieces[0].tris.Length / 3];
            for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((mask & (GarmentBit(g))) != 0)
                {
                    bool[] on = bare != 0 && pieces[g].withLegwear ? OnBareLeg(pieces[0], bare) : null;
                    int[] all = pieces[0].tris;
                    foreach (int k in pieces[g].hides)
                    {
                        hide[k] |= on == null || !(on[all[3 * k]] || on[all[3 * k + 1]] || on[all[3 * k + 2]]);
                    }
                }
            }
            return hide;
        }

        private static int[] Kept(int[] all, bool[] hide)
        {
            List<int> kept = new List<int>(all.Length);
            for (int k = 0; k < hide.Length; k++)
            {
                if (!hide[k])
                {
                    kept.Add(all[3 * k]);
                    kept.Add(all[3 * k + 1]);
                    kept.Add(all[3 * k + 2]);
                }
            }
            return kept.ToArray();
        }

        // Garments over garments: how far a garment under another may poke out through it at rest and still be hidden by
        // it, and how far out from it the one over it may lie, cells (as the exporter's HIDE_REACH and HIDE_LOOK for the body).
        private const float GarmentInside = 0.04f;
        private const float GarmentLook = 0.2f;
        private const float GarmentSame = 0.001f;   // cells: a corner this near a vertex of the garment over it is on it
        // The least share of a corner's weights on the bone a garment rides whole (a hat, a mask), or on the bones below it,
        // for the corner to count as under that garment, or as over it. A mask over the face reaches down to the collar of
        // a shirt, which moves with the neck and chest, not the head: hidden at rest, it opened as a hole when the head turned.
        private const float RidesWith = 0.5f;

        // Leaves out of each worn garment in mask (bit g: garment g, worn as worn[g]) the triangles under the worn garments
        // that go over it (GoesOver), as HideUnder does for the body, so a shirt can't poke through a duster's back as the
        // two bend apart, but shows where the duster hangs open over it. Call after End and before drawing the garments.
        // version: which answer of WalkFigures.WornGarments mask and worn are (the same number, the same mask and worn), so
        // what they lead to here is left as it is; -1 always works it out.
        public void HideGarments(Posed p, GarmentMask mask, Apparel[] worn, int version = -1)
        {
            if (version >= 0 && p.garmentsHiddenFor == version && p.garmentsHiddenTucked == p.tucked && p.garmentsHiddenBare == p.bare)
            {
                return;
            }
            p.garmentsHiddenFor = version;
            p.garmentsHiddenTucked = p.tucked;
            p.garmentsHiddenBare = p.bare;
            for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((mask & (GarmentBit(g))) == 0 || worn[g] == null)
                {
                    continue;
                }
                GarmentMask over = OverOf(g, mask, worn);
                PieceMeshes m = p.pieces[g];
                if (m == null)
                {
                    m = NewPieceMeshes(pieces[g]);
                    p.pieces[g] = m;
                }
                if (m.hidden != over || m.tucked != p.tucked || m.bare != p.bare)
                {
                    int[] tris = Unshod(p, g, Tucked(p, GarmentTris(g, over), pieces[g]));
                    m.solid.SetTriangles(Unshod(p, g, Tucked(p, GarmentSolidTris(g, over), pieces[g])), 0, false);
                    m.outline.SetTriangles(tris, 0, false);
                    m.hidden = over;
                    m.tucked = p.tucked;
                    m.bare = p.bare;
                }
            }
        }

        // The Furskin gene's tufts (anim_scripts/make_fur_edges.py), a piece drawn for the gene rather than for apparel; -1: the
        // export has none.
        public int FurPiece
        {
            get
            {
                int g;
                return garmentOf.TryGetValue(FurDef, out g) ? g : -1;
            }
        }

        private const string FurDef = "Gene_Furskin";
        private readonly Dictionary<Piece, bool[]> furUnder = new Dictionary<Piece, bool[]>();   // by what lies over it (FurUnder)
        private readonly Dictionary<(GarmentMask, Piece, int), int[]> furTris = new Dictionary<(GarmentMask, Piece, int), int[]>();   // by garments and hair over it, and prosthetics under it

        // Leaves out of the fur the tufts under the garments in mask and the hair (WornHair's, null: none), as they stand off
        // the skin further than cloth does and would come through it. Call after End and before drawing it.
        public void HideFur(Posed p, int fur, GarmentMask mask, HairDef hairStyle)
        {
            PieceMeshes m = Pose(p, fur);
            Piece hair = WornHair(Hair(hairStyle), mask, p);
            if (m.hidden == mask && m.hiddenHair == hair && m.tucked == p.tucked && m.hiddenLimbs == p.limbs)
            {
                return;
            }
            (GarmentMask, Piece, int) key = (mask, hair, p.limbs);
            int[] tris;
            if (!furTris.TryGetValue(key, out tris))
            {
                bool[] hide = new bool[pieces[fur].tris.Length / 3];
                for (int h = 1; h < pieces.Length && h <= MaxHiding; h++)
                {
                    if ((mask & ~HeldMask & (GarmentBit(h))) != 0)
                    {
                        Hide(hide, FurUnder(fur, pieces[h]));
                    }
                }
                if (hair != null)
                {
                    Hide(hide, FurUnder(fur, hair));
                }
                if (p.limbs != 0)
                {
                    // No fur on a prosthetic limb (Prosthetics.cs): a tuft with any corner on one goes.
                    bool[] on = OnLimbs(pieces[fur], p.limbs);
                    int[] all = pieces[fur].tris;
                    for (int k = 0; k < hide.Length; k++)
                    {
                        hide[k] |= on[all[k * 3]] || on[all[k * 3 + 1]] || on[all[k * 3 + 2]];
                    }
                }
                tris = Kept(pieces[fur].tris, hide);
                furTris[key] = tris;
            }
            tris = Tucked(p, tris, pieces[fur]);
            m.solid.SetTriangles(tris, 0, false);
            m.outline.SetTriangles(tris, 0, false);
            m.hidden = mask;
            m.hiddenHair = hair;
            m.tucked = p.tucked;
            m.hiddenLimbs = p.limbs;
        }

        private static void Hide(bool[] hide, bool[] under)
        {
            for (int k = 0; k < hide.Length; k++)
            {
                hide[k] |= under[k];
            }
        }

        // The fur's triangles under this piece, made once per piece at rest in the base shape. A tuft goes whole, by the test
        // UnderOf has for a garment's corner from its middle along the skin's normal (the exported n lines): its own
        // faces point every way, and a tuft half left out would show its inside.
        private bool[] FurUnder(int fur, Piece outer)
        {
            bool[] hide;
            if (furUnder.TryGetValue(outer, out hide))
            {
                return hide;
            }
            Piece inner = pieces[fur];
            Vector3[] normal = inner.normal ?? Normals(inner.rest, inner.tris);
            int count = outer.tris.Length / 3;
            Vector3[] low = new Vector3[count], high = new Vector3[count];
            for (int k = 0; k < count; k++)
            {
                Vector3 a = outer.rest[outer.tris[3 * k]], b = outer.rest[outer.tris[3 * k + 1]], c = outer.rest[outer.tris[3 * k + 2]];
                low[k] = Vector3.Min(a, Vector3.Min(b, c));
                high[k] = Vector3.Max(a, Vector3.Max(b, c));
            }
            // Each tuft is its own island of vertices; every vertex takes the lowest number on its island.
            int[] island = new int[inner.rest.Length];
            for (int i = 0; i < island.Length; i++)
            {
                island[i] = i;
            }
            bool moved = true;
            while (moved)
            {
                moved = false;
                for (int k = 0; k + 2 < inner.tris.Length; k += 3)
                {
                    int a = inner.tris[k], b = inner.tris[k + 1], c = inner.tris[k + 2];
                    int least = Mathf.Min(island[a], Mathf.Min(island[b], island[c]));
                    moved |= island[a] != least || island[b] != least || island[c] != least;
                    island[a] = island[b] = island[c] = least;
                }
            }
            Vector3[] middle = new Vector3[island.Length], way = new Vector3[island.Length];
            int[] corners = new int[island.Length];
            for (int i = 0; i < island.Length; i++)
            {
                middle[island[i]] += inner.rest[i];
                way[island[i]] += normal[i];
                corners[island[i]]++;
            }
            // A tuft goes under a garment riding one bone whole (a hat, a mask) only if it moves with that bone (RidesWith):
            // its root's weights, by its vertices' mean.
            int outerBone = RiddenBone(outer);
            float[] with = new float[island.Length];
            if (outerBone >= 0)
            {
                for (int i = 0; i < island.Length; i++)
                {
                    with[island[i]] += WeightBelow(inner, i, outerBone);
                }
            }
            bool[] under = new bool[island.Length];
            for (int i = 0; i < island.Length; i++)
            {
                under[i] = corners[i] > 0 && (outerBone < 0 || with[i] / corners[i] >= RidesWith)
                    && Meets(outer, low, high, middle[i] / corners[i], way[i].normalized);
            }
            hide = new bool[inner.tris.Length / 3];
            for (int k = 0; k < hide.Length; k++)
            {
                hide[k] = under[island[inner.tris[3 * k]]];
            }
            furUnder[outer] = hide;
            return hide;
        }

        // The worn garments over garment g (bit h: garment h), which is worn.
        private GarmentMask OverOf(int g, GarmentMask mask, Apparel[] worn)
        {
            GarmentMask over = 0;
            for (int h = 1; h < pieces.Length && h <= MaxHiding; h++)
            {
                if (h != g && (mask & (GarmentBit(h))) != 0 && (HeldMask & (GarmentBit(h))) == 0 && worn[h] != null && worn[h] != worn[g]
                    && GoesOver(worn[h].def.apparel, worn[g].def.apparel))
                {
                    over |= GarmentBit(h);
                }
            }
            return over;
        }

        // True if apparel outer goes over apparel inner: on an outer layer, or on the same layer the one on the torso over
        // the one that isn't. Shirts and trousers are cut from the same body faces at the same offset, so where a shirt
        // overlaps the waist the two lie exactly on each other, and without this they fight for the same pixels.
        private static bool GoesOver(ApparelProperties outer, ApparelProperties inner)
        {
            float outerLayer = OuterLayer(outer), innerLayer = OuterLayer(inner);
            return outerLayer != innerLayer ? outerLayer > innerLayer : OnTorso(outer) && !OnTorso(inner);
        }

        private static bool OnTorso(ApparelProperties ap)
        {
            return ap.bodyPartGroups != null && ap.bodyPartGroups.Contains(BodyPartGroupDefOf.Torso);
        }

        private int[] GarmentTris(int g, GarmentMask over)
        {
            if (over == 0)
            {
                return pieces[g].tris;
            }
            (int, GarmentMask) key = (g, over);
            int[] tris;
            if (!garmentTris.TryGetValue(key, out tris))
            {
                tris = Kept(pieces[g].tris, GarmentHidden(g, over));
                garmentTris[key] = tris;
            }
            return tris;
        }

        // GarmentTris less the garment's glass (its visor's, drawn on its own by DrawVisor).
        private int[] GarmentSolidTris(int g, GarmentMask over)
        {
            Piece piece = pieces[g];
            if (piece.glass == null)
            {
                return GarmentTris(g, over);
            }
            if (over == 0)
            {
                return piece.shellTris;
            }
            (int, GarmentMask) key = (g, over);
            int[] tris;
            if (!garmentSolidTris.TryGetValue(key, out tris))
            {
                bool[] hide = GarmentHidden(g, over);
                for (int k = 0; k < hide.Length; k++)
                {
                    hide[k] |= piece.glass[k];
                }
                tris = Kept(piece.tris, hide);
                garmentSolidTris[key] = tris;
            }
            return tris;
        }

        // The triangles of garment g under the garments in over.
        private bool[] GarmentHidden(int g, GarmentMask over)
        {
            bool[] hide = new bool[pieces[g].tris.Length / 3];
            for (int h = 1; h < pieces.Length && h <= MaxHiding; h++)
            {
                if ((over & (GarmentBit(h))) != 0)
                {
                    bool[] under = UnderOf(g, h);
                    for (int k = 0; k < hide.Length; k++)
                    {
                        hide[k] |= under[k];
                    }
                }
            }
            return hide;
        }

        // The triangles of garment g under garment h, tested once, at rest in the base shape, as the exporter tests the body
        // under a garment (hidden_under): a point is under h if a line straight out from g's surface there, from
        // GarmentInside under it to GarmentLook out, meets h. Not by nearness alone, which would hide the shirt in the
        // duster's open front, where the lapels are near but nothing lies in front of it. A triangle is under h if its
        // corners and middle all are.
        private bool[] UnderOf(int g, int h)
        {
            long key = ((long)g << 32) | (uint)h;
            bool[] hide;
            if (garmentUnder.TryGetValue(key, out hide))
            {
                return hide;
            }
            Piece inner = pieces[g];
            int[] outerTris = pieces[h].tris;
            // Webbing over g is tested where it is drawn, let out over g (LetOut.cs).
            Vector3[] outerRest = LetOutOver(h, g) ?? pieces[h].rest;
            Vector3[] normal = Normals(inner.rest, inner.tris);
            // Each of h's triangles' bounds, so most are passed over without the full test.
            int count = outerTris.Length / 3;
            Vector3[] low = new Vector3[count], high = new Vector3[count];
            for (int k = 0; k < count; k++)
            {
                Vector3 a = outerRest[outerTris[3 * k]], b = outerRest[outerTris[3 * k + 1]], c = outerRest[outerTris[3 * k + 2]];
                low[k] = Vector3.Min(a, Vector3.Min(b, c));
                high[k] = Vector3.Max(a, Vector3.Max(b, c));
            }
            // A garment riding one bone whole moves apart from what moves with other bones, so only what moves with it can
            // stay under it, or over it (RidesWith).
            int outerBone = RiddenBone(pieces[h]), innerBone = RiddenBone(inner);
            bool[] covered = new bool[inner.rest.Length];
            for (int i = 0; i < covered.Length; i++)
            {
                if (outerBone >= 0 && WeightBelow(inner, i, outerBone) < RidesWith)
                {
                    continue;
                }
                int hit = Hit(outerRest, outerTris, low, high, inner.rest[i], normal[i]);
                if (hit >= 0 && innerBone >= 0 && outerBone < 0)
                {
                    Piece outer = pieces[h];
                    float share = (WeightBelow(outer, outerTris[3 * hit], innerBone) + WeightBelow(outer, outerTris[3 * hit + 1], innerBone)
                        + WeightBelow(outer, outerTris[3 * hit + 2], innerBone)) / 3f;
                    covered[i] = share >= RidesWith;
                }
                else
                {
                    covered[i] = hit >= 0 || OnVertex(outerRest, inner.rest[i]);
                }
            }
            hide = new bool[inner.tris.Length / 3];
            for (int k = 0; k < hide.Length; k++)
            {
                int a = inner.tris[3 * k], b = inner.tris[3 * k + 1], c = inner.tris[3 * k + 2];
                if (covered[a] && covered[b] && covered[c])
                {
                    Vector3 middle = (inner.rest[a] + inner.rest[b] + inner.rest[c]) / 3f;
                    hide[k] = Meets(outerRest, outerTris, low, high, middle, (normal[a] + normal[b] + normal[c]).normalized);
                }
            }
            garmentUnder[key] = hide;
            return hide;
        }

        // The bone every vertex of the piece rides whole (a hat or a mask on the head bone), -1 if it isn't one piece on one bone.
        private static int RiddenBone(Piece piece)
        {
            if (piece.firstWeight == null || piece.weightBone.Length == 0)
            {
                return -1;
            }
            int bone = piece.weightBone[0];
            for (int k = 0; k < piece.weightBone.Length; k++)
            {
                if (piece.weightBone[k] != bone && piece.weight[k] > 1e-3f)
                {
                    return -1;
                }
            }
            return bone;
        }

        // The share of vertex i's weights on bone or the bones below it.
        private float WeightBelow(Piece piece, int i, int bone)
        {
            float share = 0f;
            for (int k = piece.firstWeight[i]; k < piece.firstWeight[i + 1]; k++)
            {
                for (int b = piece.weightBone[k]; b >= 0; b = boneParent[b])
                {
                    if (b == bone)
                    {
                        share += piece.weight[k];
                        break;
                    }
                }
            }
            return share;
        }

        // True if point is on one of the piece's vertices. A corner on a garment's open edge, as where trousers cut from the
        // same faces as a shirt meet its hem, can have its line miss by rounding, which would leave that triangle drawn.
        private static bool OnVertex(Vector3[] rest, Vector3 point)
        {
            for (int i = 0; i < rest.Length; i++)
            {
                if ((rest[i] - point).sqrMagnitude < GarmentSame * GarmentSame)
                {
                    return true;
                }
            }
            return false;
        }

        // True if the line along n from GarmentInside behind point to GarmentLook in front of it meets the piece (either
        // side of its triangles); low and high bound each triangle.
        private static bool Meets(Piece piece, Vector3[] low, Vector3[] high, Vector3 point, Vector3 n)
        {
            return Meets(piece.rest, piece.tris, low, high, point, n);
        }

        private static bool Meets(Vector3[] rest, int[] tris, Vector3[] low, Vector3[] high, Vector3 point, Vector3 n)
        {
            return Hit(rest, tris, low, high, point, n) >= 0;
        }

        // The first of the piece's triangles the line Meets tests meets, -1 for none.
        private static int Hit(Vector3[] rest, int[] tris, Vector3[] low, Vector3[] high, Vector3 point, Vector3 n)
        {
            Vector3 from = point - n * GarmentInside, to = point + n * GarmentLook;
            Vector3 lo = Vector3.Min(from, to), hi = Vector3.Max(from, to), d = to - from;
            for (int k = 0; k < low.Length; k++)
            {
                if (high[k].x < lo.x || low[k].x > hi.x || high[k].y < lo.y || low[k].y > hi.y || high[k].z < lo.z || low[k].z > hi.z)
                {
                    continue;
                }
                // Moller and Trumbore's test, for t along the line from 0 to 1.
                Vector3 a = rest[tris[3 * k]];
                Vector3 e1 = rest[tris[3 * k + 1]] - a, e2 = rest[tris[3 * k + 2]] - a;
                Vector3 q = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, q);
                if (Mathf.Abs(det) < 1e-12f)
                {
                    continue;
                }
                float inv = 1f / det;
                Vector3 s = from - a;
                float u = Vector3.Dot(s, q) * inv;
                if (u < 0f || u > 1f)
                {
                    continue;
                }
                Vector3 r = Vector3.Cross(s, e1);
                float v = Vector3.Dot(d, r) * inv;
                if (v < 0f || u + v > 1f)
                {
                    continue;
                }
                float t = Vector3.Dot(e2, r) * inv;
                if (t >= 0f && t <= 1f)
                {
                    return k;
                }
            }
            return -1;
        }

        // Draws one piece (0 the body, else a garment) posed as End left it, as Api.DrawModel would draw a model: position
        // true space (cells), yaw degrees about the vertical, size the model's scale, tint its colour.
        // A textured garment is drawn with its image times tint (white: as it is), its outline in its mean colour times tint.
        public void Draw(Posed p, int piece, Vector3 position, float yaw, float size, Color tint, bool isSkin = false)
        {
            if (TuckedAway(p, piece))
            {
                return;   // all of it under the blanket
            }
            Piece g = pieces[piece];
            PieceMeshes m = Pose(p, piece);
            m.isSkin = isSkin;
            // Fetched each time: IsoRim makes its materials once the 3D view is up, which may be after this pawn was first seen.
            Material textured = g.texture != null ? TexturedMaterial(g) : null;
            if (textured == null)
            {
                DrawMeshes(p, m, g.rest.Length, SolidMaterial(), position, yaw, size, g.texture != null ? tint * g.mean : tint);
            }
            else
            {
                if (!m.textureMarked && g.mark != null)
                {
                    // The same mesh again in its light pass, drawn with the same matrix by DrawParts.
                    m.parts.Add(new MeshPart { mesh = m.solid, material = g.mark, outline = false });
                    m.textureMarked = true;
                }
                DrawMeshes(p, m, g.rest.Length, textured, position, yaw, size, tint, null, default(Color), null, default(Color), tint * g.mean);
            }
            if (g.visor != null)
            {
                DrawVisor(p, g, position, yaw, size);
            }
        }

        // A visor's glass (VisorOf), posed as the garment is and drawn see-through over what is behind it (MarusGlass: after
        // the face layers and the wounds, writing no depth). It has no light pass of its own: the head and the helmet's
        // inside behind it keep the depth, so their light passes (queue 3099) light the glass with them.
        private void DrawVisor(Posed p, Piece garment, Vector3 position, float yaw, float size)
        {
            Material glass = GlassMaterial();
            if (glass == null)
            {
                return;
            }
            Piece visor = garment.visor;
            PieceMeshes m;
            if (!p.visors.TryGetValue(visor, out m))
            {
                m = NewPieceMeshes(visor);
                p.visors[visor] = m;
            }
            if (garment.heldOnBack)
            {
                ShieldVisor(p, garment, m);   // placed whole with the shield
            }
            else
            {
                PieceShape shape;
                if (!p.shape.hairs.TryGetValue(visor, out shape))
                {
                    shape = ShapePiece(visor, p.shape.mix);
                    p.shape.hairs[visor] = shape;
                }
                Pose(p, visor, shape, m);
            }
            Matrix4x4 local = Matrix4x4.Scale(new Vector3(size, size, size));
            DrawSubmesh(m.solid, 0, glass, position, yaw, local, m.gpuShape != null ? BlockOf(p, m.dressed) : null);
        }

        // A garment's glass triangles as a piece of their own: their vertices with the garment's weights, keys and texture
        // places, so it takes the same shape and pose.
        private static Piece VisorOf(Piece g)
        {
            Dictionary<int, int> at = new Dictionary<int, int>();
            List<int> tris = new List<int>(), from = new List<int>();
            for (int k = 0; k < g.glass.Length; k++)
            {
                if (!g.glass[k])
                {
                    continue;
                }
                for (int c = 0; c < 3; c++)
                {
                    int i = g.tris[3 * k + c], j;
                    if (!at.TryGetValue(i, out j))
                    {
                        j = from.Count;
                        at[i] = j;
                        from.Add(i);
                    }
                    tris.Add(j);
                }
            }
            int n = from.Count;
            Piece v = new Piece { name = g.name + "_visor", defs = new string[0], hides = new int[0], tris = tris.ToArray() };
            v.rest = new Vector3[n];
            v.firstWeight = new int[n + 1];
            List<int> bone = new List<int>();
            List<float> weight = new List<float>();
            for (int j = 0; j < n; j++)
            {
                int i = from[j];
                v.rest[j] = g.rest[i];
                v.firstWeight[j] = bone.Count;
                for (int w = g.firstWeight[i]; w < g.firstWeight[i + 1]; w++)
                {
                    bone.Add(g.weightBone[w]);
                    weight.Add(g.weight[w]);
                }
            }
            v.firstWeight[n] = bone.Count;
            v.weightBone = bone.ToArray();
            v.weight = weight.ToArray();
            foreach (KeyValuePair<string, Vector3[]> key in g.keys)
            {
                Vector3[] moves = new Vector3[n];
                for (int j = 0; j < n; j++)
                {
                    moves[j] = key.Value[from[j]];
                }
                v.keys[key.Key] = moves;
            }
            if (g.uvs != null)
            {
                v.uvs = new Vector2[n];
                for (int j = 0; j < n; j++)
                {
                    v.uvs[j] = g.uvs[from[j]];
                }
            }
            return v;
        }

        // The tint and opacity of a visor's glass where it faces the camera; it is more opaque, and lighter, toward its
        // edges (MarusSmooth.frag.glsl's glass), and catches the sun.
        private static readonly Color GlassColour = new Color(0.10f, 0.17f, 0.23f, 0.35f);
        private static Material glassMaterial;
        private static bool glassTried;

        // The visors' material (MarusGlass); null until IsoRim's materials exist, or if the shader is missing (the glass is
        // then left out: the face shows through an open visor).
        private static Material GlassMaterial()
        {
            if (glassMaterial != null || glassTried)
            {
                return glassMaterial;
            }
            ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(GlassDef);
            if (def == null || def.Shader == null)
            {
                glassTried = true;
                Log.Warning(Tag + "the " + GlassDef + " shader is missing, so helmet visors are drawn without their glass.");
                return null;
            }
            Material m = new Material(def.Shader);
            m.name = "Marus_Glass";
            m.color = GlassColour;
            m.SetFloat("_Glass", 1f);
            m.SetFloat("_ShadeBands", ShadeBands);
            if (!Api.ConfigureFigureMaterial(m, FigureMaterialKind.Unlit))
            {
                return null;   // IsoRim's materials aren't made yet: again next time
            }
            // The model curve, but no light pass of its own: the head's and the helmet's behind it light it (DrawVisor).
            m.SetFloat("_LightMode", LightModelCurve);
            m.SetFloat("_GlowMode", GlowOverlay);
            glassMaterial = m;
            return glassMaterial;
        }

        // True if this textured garment, worn as this apparel, is tinted by the apparel's colour: as its file says, else if
        // the apparel is made from stuff (cloth, leather) or has been dyed. Not, for something of one look (power armour).
        public bool Tinted(int piece, Apparel apparel)
        {
            Piece g = pieces[piece];
            if (g.texture == null || g.tint == 1)
            {
                return true;
            }
            if (g.tint == 0)
            {
                return false;
            }
            CompColorable dye = apparel.TryGetComp<CompColorable>();
            return apparel.def.MadeFromStuff || (dye != null && dye.Active);
        }

        // Draws the body as Draw does, with the scalp (Scalp) in the hair colour shaded ScalpShade if it has hair
        // (hairStyle, null: none): where the head shows through the hair, or pokes out of it as it bends, it reads as hair
        // in shadow.
        public void DrawBody(Posed p, HairDef hairStyle, Vector3 position, float yaw, float size, Color tint, Color hairTint)
        {
            Piece hair = Hair(hairStyle);
            bool[] scalp = hair != null ? Scalp() : null;
            if (p.face != null && p.faceScalp != scalp)
            {
                // Facial Animation's layers cover the face patch right up over the head, its skin-coloured ones too:
                // where the hair lets the head show through, they showed as skin instead of the scalp.
                int[] tris = scalp != null ? FaceTrisOff(scalp) : faceTris;
                p.face.face.SetTriangles(tris, 0, false);
                p.face.mouth.SetTriangles(tris, 0, false);
                p.face.mouthLow.SetTriangles(tris, 0, false);
                p.faceScalp = scalp;
            }
            PieceMeshes body = Pose(p, 0);
            body.isSkin = true;
            DrawMeshes(p, body, pieces[0].rest.Length, SolidMaterial(), position, yaw, size, tint, null, default(Color),
                scalp, hairTint, null, PaintOf(p.limbs));
        }

        // The face patch's triangles less those made from a scalp vertex.
        private int[] FaceTrisOff(bool[] scalp)
        {
            int[] tris;
            if (faceTrisOff.TryGetValue(scalp, out tris))
            {
                return tris;
            }
            List<int> kept = new List<int>(faceTris.Length);
            for (int k = 0; k + 2 < faceTris.Length; k += 3)
            {
                if (!scalp[faceFrom[faceTris[k]]] && !scalp[faceFrom[faceTris[k + 1]]] && !scalp[faceFrom[faceTris[k + 2]]])
                {
                    kept.Add(faceTris[k]);
                    kept.Add(faceTris[k + 1]);
                    kept.Add(faceTris[k + 2]);
                }
            }
            tris = kept.ToArray();
            faceTrisOff[scalp] = tris;
            return tris;
        }

        // The body's scalp vertices (made once): the rig's Scalp group where the export has one (skin.txt's scalp line),
        // else the head's vertices above the hairline. The same for every hairstyle: one worked out from each style's
        // hair missed the gaps in it (the hair isn't over the head there), which were what needed hiding.
        internal bool[] Scalp()
        {
            if (scalp != null)
            {
                return scalp;
            }
            Vector3[] v = pieces[0].rest;
            if (scalpGiven.Count > 0)
            {
                // Painted on the rig (its Scalp group), so the hairline is wherever it was drawn.
                scalp = new bool[v.Length];
                foreach (int i in scalpGiven)
                {
                    if (i >= 0 && i < v.Length)
                    {
                        scalp[i] = true;
                    }
                }
                return scalp;
            }
            bool[] head = OnHead();
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < v.Length; i++)
            {
                if (head[i])
                {
                    lo = Vector3.Min(lo, v[i]);
                    hi = Vector3.Max(hi, v[i]);
                }
            }
            Vector3 mid = (lo + hi) / 2f;
            bool[] above = new bool[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                bool forehead = v[i].z > mid.z && Mathf.Abs(v[i].x - mid.x) < ScalpForeheadHalf;
                above[i] = head[i] && v[i].y - mid.y > ScalpSlope * (v[i].z - mid.z) + (forehead ? ScalpForeheadRaise : 0f);
            }
            scalp = (bool[])above.Clone();
            int[] tris = pieces[0].tris;
            for (int k = 0; k < tris.Length; k += 3)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int a = tris[k + corner], b = tris[k + (corner + 1) % 3];
                    if (above[a] != above[b])
                    {
                        int top = above[a] ? a : b, skin = above[a] ? b : a;
                        if (v[top].y - v[skin].y > ScalpEdgeDrop)
                        {
                            scalp[top] = false;
                        }
                    }
                }
            }
            return scalp;
        }

        // Draws the hair of this style on the head as End left it, in the hair colour; nothing while it is being built or
        // if there is none to be had.
        // Cut under a hat in mask that tucks it (HairUnderHat), which can leave none of a short style.
        public void DrawHair(Posed p, HairDef style, GarmentMask mask, Vector3 position, float yaw, float size, Color tint, Color skin)
        {
            Piece whole = Hair(style);
            Material solid = whole != null ? TexturedMaterial(whole) : null;
            if (solid == null)
            {
                return;
            }
            Piece hair = WornHair(whole, mask, p);
            if (hair.tris.Length == 0)
            {
                return;
            }
            hair.mark = whole.mark;
            if (p.hairPiece != hair)
            {
                if (p.hair != null)
                {
                    Api.RetireMesh(p.hair.solid);
                    Api.RetireMesh(p.hair.outline);
                }
                p.hair = NewPieceMeshes(hair);
                p.hair.tinted = false;
                if (hair.mark != null)
                {
                    // The same mesh again in the light pass, drawn with the same matrix by DrawParts.
                    p.hair.parts.Add(new MeshPart { mesh = p.hair.solid, material = hair.mark, outline = false });
                }
                p.hairPiece = hair;
            }
            PieceShape shape;
            if (!p.shape.hairs.TryGetValue(hair, out shape))
            {
                shape = ShapePiece(hair, p.shape.mix);
                p.shape.hairs[hair] = shape;
            }
            Pose(p, hair, shape, p.hair);
            DrawMeshes(p, p.hair, hair.rest.Length, solid, position, yaw, size, tint, hair.shell, skin);
        }

        // Draws the beard of this style as DrawHair draws hair; its outline is from its own mesh, as it has no shell. Nothing
        // while it is being built or if there is none to be had.
        public void DrawBeard(Posed p, BeardDef style, Vector3 position, float yaw, float size, Color tint)
        {
            Piece beard = Beard(style);
            Material solid = beard != null ? TexturedMaterial(beard) : null;
            if (solid == null)
            {
                return;
            }
            if (p.beardPiece != beard)
            {
                if (p.beard != null)
                {
                    Api.RetireMesh(p.beard.solid);
                    Api.RetireMesh(p.beard.outline);
                }
                p.beard = NewPieceMeshes(beard);
                p.beard.tinted = false;
                if (beard.mark != null)
                {
                    p.beard.parts.Add(new MeshPart { mesh = p.beard.solid, material = beard.mark, outline = false });
                }
                p.beardPiece = beard;
            }
            PieceShape shape;
            if (!p.shape.hairs.TryGetValue(beard, out shape))
            {
                shape = ShapePiece(beard, p.shape.mix);
                p.shape.hairs[beard] = shape;
            }
            Pose(p, beard, shape, p.beard);
            DrawMeshes(p, p.beard, beard.rest.Length, solid, position, yaw, size, tint);
        }

        // Rounded as DynamicModels.PartsFor rounds a model's tint (FaceLayers' lid cover matches it exactly).
        private static Color32 Rounded(Color c)
        {
            Color32 t = c;
            t.r = (byte)(t.r & 0xF8);
            t.g = (byte)(t.g & 0xF8);
            t.b = (byte)(t.b & 0xF8);
            t.a = 255;
            return t;
        }

        // A colour times shade, as IsoRim shades its models' vertex colours (Api.ModelVertexColour).
        private static Color32 Shade(Color c, float shade)
        {
            return new Color32((byte)Mathf.Clamp(c.r * shade * 255f, 0f, 255f), (byte)Mathf.Clamp(c.g * shade * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.b * shade * 255f, 0f, 255f), 255);
        }

        // IsoRim's outline shade (its models' outlines are their tint times this), read from Api.ModelVertexColour.
        private static float LineShade
        {
            get
            {
                if (lineShade < 0f)
                {
                    Color32 solidWhite = Api.ModelVertexColour(Color.white, false), lineWhite = Api.ModelVertexColour(Color.white, true);
                    lineShade = solidWhite.r > 0 ? lineWhite.r / (float)solidWhite.r : 0.25f;
                }
                return lineShade;
            }
        }
        private static float lineShade = -1f;

        private static bool Same(Color32 a, Color32 b)
        {
            return a.r == b.r && a.g == b.g && a.b == b.b;
        }

        // shell (a hair's, or null): the outline is its shell's, its skin vertices in skin's colour.
        // scalp (the body's, or null): those vertices in scalpTint, shaded ScalpShade.
        // lineTint (null: tint): the outline's colour, before it is shaded.
        // p: the pawn, whose bones a mesh skinned on the graphics card is drawn with.
        private static void DrawMeshes(Posed p, PieceMeshes m, int n, Material solid, Vector3 position, float yaw, float size, Color tint,
            Piece shell = null, Color skin = default(Color), bool[] scalp = null, Color scalpTint = default(Color), Color? lineTint = null,
            Color32[] paint = null)
        {
            Color32 t = Rounded(tint), sk = Rounded(skin), st = Rounded(scalpTint), lt = Rounded(lineTint ?? tint);
            if (!m.tinted || !Same(t, m.tint) || !Same(lt, m.lineTint) || (shell != null && !Same(sk, m.skin)) || m.scalp != scalp
                || (scalp != null && !Same(st, m.scalpTint)) || m.paint != paint)
            {
                m.tint = t;
                m.lineTint = lt;
                m.skin = sk;
                m.scalp = scalp;
                m.scalpTint = st;
                m.paint = paint;
                m.tinted = true;
                Color32[] colours = Fill(Shade(t, 1f), n);
                if (scalp != null)
                {
                    Color32 under = Shade(st, ScalpShade);
                    for (int i = 0; i < n && i < scalp.Length; i++)
                    {
                        if (scalp[i])
                        {
                            colours[i] = under;
                        }
                    }
                }
                for (int i = 0; paint != null && i < n && i < paint.Length; i++)
                {
                    if (paint[i].a != 0)
                    {
                        colours[i] = paint[i];   // a prosthetic limb (Prosthetics.cs)
                    }
                }
                m.solid.colors32 = colours;
                Color32 line = Shade(lt, LineShade);
                Color32[] lines = Fill(line, m.hulled.Length);
                for (int i = 0; paint != null && lines.Length == n && i < paint.Length; i++)
                {
                    if (paint[i].a != 0)
                    {
                        lines[i] = Shade((Color)paint[i], LineShade);
                    }
                }
                if (shell != null && shell.skinVert != null)
                {
                    Color32 skinLine = Shade(sk, LineShade);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (shell.skinVert[i])
                        {
                            lines[i] = skinLine;
                        }
                    }
                }
                m.outline.colors32 = lines;
            }
            if (solid == null || OutlineMaterial() == null)
            {
                return;
            }
            SetSlot(m, solid, OutlineMaterial());
            if (m.outlineMarkAt < 0 && outlineMark != null)
            {
                // The outline again in its light pass, drawn with the same matrix by DrawParts.
                m.outlineMarkAt = m.parts.Count;
                m.parts.Add(new MeshPart { mesh = m.outline, material = outlineMark, outline = true });
            }
            if (m.outlineMarkAt >= 0)
            {
                // Set each draw, as the option can change: each outline's push has its own light pass, to meet it exactly.
                m.parts[m.outlineMarkAt].material = Silhouette ? outlineMark : linesMark;
            }
            List<MeshPart> parts = m.parts;
            bool skipAfter = m.afterAt >= 0 && (!OcclusionShows || (ClothesOcclusion && !m.isSkin));
            if (!Outlines || skipAfter)
            {
                // Only the parts wanted (the solid ones and their light passes, without the outlines; the redraw only after
                // an occlusion pass): DrawParts draws whatever it is given.
                solidParts.Clear();
                for (int i = 0; i < m.parts.Count; i++)
                {
                    if ((Outlines || !m.parts[i].outline) && !(skipAfter && i == m.afterAt))
                    {
                        solidParts.Add(m.parts[i]);
                    }
                }
                parts = solidParts;
            }
            Matrix4x4 local = Matrix4x4.Scale(new Vector3(size, size, size));
            if (!FigureOnScreen(position, yaw, local))
            {
                return;
            }
            DrawParts(parts, position, yaw, local, m.gpuShape != null ? BlockOf(p, m.dressed) : null, p.flash);
        }

        public static bool ClothesOcclusion   // true: IsoRim's ambient occlusion and screen-space shadows show on clothes and hair, never the skin (the mod's options set it)
        {
            get { return SettingsMod.Settings != null && SettingsMod.Settings.clothesOcclusion; }
        }

        public static bool Outlines   // false: the chibi is drawn without its outline (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.outlines; }
        }
        public static bool Silhouette   // false: the outline is drawn as IsoRim draws its figures', over the body too (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.silhouetteOutlines; }
        }
        private static readonly List<MeshPart> solidParts = new List<MeshPart>();

        private static Material outline, outlineMark;   // the silhouette
        private static Material lines, linesMark;       // and the outline with lines over the body (Silhouette off)
        private static bool outlineTried;

        // The outline's material (MarusOutline, or MarusOutlineLines with Silhouette off) with its light pass (outlineMark,
        // linesMark), made once IsoRim has made its own materials, set up as IsoRim's moving outlines (unlit: the vertex colour
        // is the line's shade already; pushed away from the camera by OutlinePush, or IsoRim's push for the lines), but lit
        // at night by its own light pass, a part of its own (DrawMeshes). Null until then, or without the shaders (the
        // figure is then left to IsoRim, WalkFigures.MaterialsReady).
        private static Material OutlineMaterial()
        {
            if (!outlineTried && outline == null)
            {
                ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(OutlineDef);
                ShaderDef linesDef = DefDatabase<ShaderDef>.GetNamedSilentFail(OutlineLinesDef);
                ShaderDef markDef = DefDatabase<ShaderDef>.GetNamedSilentFail(OutlineMarkDef);
                if (def == null || def.Shader == null || linesDef == null || linesDef.Shader == null || markDef == null || markDef.Shader == null)
                {
                    outlineTried = true;
                    Log.Warning(Tag + "the " + OutlineDef + " shaders are missing, so IsoRim draws its own figures.");
                    return null;
                }
                Material o = OutlineWith(def.Shader, "Marus_Outline", OutlinePush, false);
                if (o == null)
                {
                    return null;   // IsoRim's materials aren't made yet: again next time
                }
                outlineMark = OutlineWith(markDef.Shader, "Marus_Outline_Mark", OutlinePush, true);
                // IsoRim's push: it is what hides this outline where one part tucks into another.
                lines = OutlineWith(linesDef.Shader, "Marus_Outline_Lines", Api.OutlinePush, false);
                linesMark = OutlineWith(markDef.Shader, "Marus_Outline_Lines_Mark", Api.OutlinePush, true);
                outline = o;
                outlineTried = true;
            }
            return Silhouette ? outline : lines;
        }

        // One of the outline's materials, set up by IsoRim as an outline (or a light pass's as unlit), then given the figure
        // shader contract's settings it needs here: its push, own-cell light (by the light pass drawn as a part of its own)
        // and, for that pass, _MarkPass. Null until IsoRim's materials exist.
        private static Material OutlineWith(Shader shader, string name, float push, bool mark)
        {
            Material m = new Material(shader);
            m.name = name;
            if (!Api.ConfigureFigureMaterial(m, mark ? FigureMaterialKind.Unlit : FigureMaterialKind.Outline))
            {
                return null;
            }
            m.SetFloat("_LightMode", 0f);
            m.SetFloat("_OutlinePush", push);
            m.SetFloat("_GlowMode", GlowOwnCell);
            if (mark)
            {
                m.SetFloat("_MarkPass", 1f);
            }
            return m;
        }

        // The figure shader contract's (Docs/Api.md part 43) values: the model light curve, and own-cell light by a light pass.
        private const float LightModelCurve = 1f, GlowOwnCell = 1f, GlowOverlay = 0f;

        private static Material smooth;
        private static bool smoothTried;

        // The solid copy's material (MarusSmooth), made once IsoRim has made its own and set up as a moving model's (the model
        // curve, the light of its own cell at night by IsoRim's light pass, given MarusSmooth's own program, SkinMarkShader,
        // as the graphics card may pose the mesh). Null until then, or without the shader (the figure is then left to IsoRim).
        private static Material SolidMaterial()
        {
            if (smooth != null || smoothTried)
            {
                return smooth;
            }
            ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(SmoothDef);
            Shader shader = def != null ? def.Shader : null;
            if (shader == null)
            {
                smoothTried = true;
                Log.Warning(Tag + "the " + SmoothDef + " shader is missing, so IsoRim draws its own figures.");
                return null;
            }
            Material m = new Material(shader);
            m.name = "Marus_Smooth";
            m.SetFloat("_ShadeBands", ShadeBands);
            if (!Api.ConfigureFigureMaterial(m, FigureMaterialKind.Solid, SkinMarkShader()))
            {
                return null;   // IsoRim's materials aren't made yet: again next time
            }
            smooth = m;
            return smooth;
        }

        // Whether the figure's own materials can be had now: IsoRim's are made (after the 3D view first comes up) and ours'
        // shaders are there. Until then (or for good, a shader missing) IsoRim draws its own figures.
        public static bool MaterialsReady
        {
            get { return SolidMaterial() != null && OutlineMaterial() != null; }
        }

        // The name a HairDef's hair goes by: its texPath, less HairTextures when it starts with that.
        private static string StyleOf(HairDef def)
        {
            string path = def != null ? def.texPath : null;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            return path.StartsWith(HairTextures) ? path.Substring(HairTextures.Length) : path;
        }

        // A beard's name: its texPath, less BeardTextures when it starts with that, in BeardFolder (of the cache, or a
        // M3AThair folder).
        private static string BeardStyleOf(BeardDef def)
        {
            string path = def != null && !def.noGraphic ? def.texPath : null;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            return BeardFolder + (path.StartsWith(BeardTextures) ? path.Substring(BeardTextures.Length) : path);
        }

        // A copy of a piece (hair or its shell) cut down by Decimate to this share of its triangles (built, they come out
        // at up to ten thousand each, and every vertex is posed on the CPU every frame); each vertex left keeps its own
        // weights and keys. Only built hair: hair from a M3AThair folder keeps the triangles its maker gave it.

        internal static Piece Decimated(Piece from, Vector2[] uvs, float share)
        {
            int[] kept, tris;
            int hairCut = -1;   // built hair's: its triangles once cut, the walls after them
            int target = Mathf.RoundToInt(from.tris.Length / 3 * share);
            int wallsAt = from.wallsFrom * 3;
            if (from.wallsFrom < 0 || wallsAt >= from.tris.Length)
            {
                Decimate.Run(from.rest, from.tris, uvs, target, out kept, out tris);
            }
            else
            {
                // Only the hair is cut, and its walls are then hung again from the cut hair's open edges. Cut with it,
                // their corners were merged into the hair's and took its texture places, and the walls came out as
                // smeared, crossing panels; kept whole, they still followed the edge's old steps where the cut had
                // straightened it, and the skin showed through the gaps between.
                int[] hair = new int[wallsAt], walls = new int[from.tris.Length - wallsAt];
                Array.Copy(from.tris, hair, wallsAt);
                Array.Copy(from.tris, wallsAt, walls, 0, walls.Length);
                int[] hairKept, hairTris;
                Decimate.Run(from.rest, hair, uvs, Mathf.RoundToInt(hair.Length / 3 * share), out hairKept, out hairTris);
                List<int> keptList = new List<int>(hairKept);
                List<int> all = new List<int>(hairTris);
                hairCut = hairTris.Length / 3;
                Rehang(from, hair, walls, hairKept, keptList, all);
                kept = keptList.ToArray();
                tris = all.ToArray();
            }
            Piece p = new Piece { name = from.name, layer = from.layer, groups = from.groups, defs = from.defs, hides = from.hides, wallUv = from.wallUv };
            p.tris = tris;
            if (hairCut >= 0 && hairCut * 3 < tris.Length)
            {
                p.wallsFrom = hairCut;
            }
            p.rest = new Vector3[kept.Length];
            List<int> first = new List<int>(), bone = new List<int>();
            List<float> weight = new List<float>();
            for (int i = 0; i < kept.Length; i++)
            {
                int o = kept[i];
                p.rest[i] = from.rest[o];
                first.Add(bone.Count);
                for (int k = from.firstWeight[o]; k < from.firstWeight[o + 1]; k++)
                {
                    bone.Add(from.weightBone[k]);
                    weight.Add(from.weight[k]);
                }
            }
            first.Add(bone.Count);
            p.firstWeight = first.ToArray();
            p.weightBone = bone.ToArray();
            p.weight = weight.ToArray();
            if (from.uvs != null)
            {
                p.uvs = Array.ConvertAll(kept, o => from.uvs[o]);
            }
            if (from.normal != null)
            {
                p.normal = Array.ConvertAll(kept, o => from.normal[o]);
            }
            if (from.skinVert != null)
            {
                p.skinVert = Array.ConvertAll(kept, o => from.skinVert[o]);
            }
            foreach (KeyValuePair<string, Vector3[]> key in from.keys)
            {
                p.keys[key.Key] = Array.ConvertAll(kept, o => key.Value[o]);
            }
            return p;
        }

        // Hangs built hair's walls again from the cut hair's open edges (all, by the file's vertices; kept, the cut hair's
        // vertices so far, and the triangles over them, both added to). The file has a wall under each of the uncut
        // edges, every one a quad from a top corner on a hair vertex down to a bottom corner, the same at a vertex
        // whichever edge it is under. A cut open edge runs between hair vertices the cut kept, so it gets a quad between
        // the walls the file has at its two ends; one with no wall at an end (below the chin) gets none.
        private static void Rehang(Piece from, int[] hair, int[] walls, int[] hairKept, List<int> kept, List<int> all)
        {
            HashSet<Vector3> hairAt = new HashSet<Vector3>();
            foreach (int o in hair)
            {
                hairAt.Add(from.rest[o]);
            }
            // Each place on the edge to its wall's top and bottom corner: a quad's two tops and two bottoms, paired
            // so the two sides don't cross.
            Dictionary<Vector3, int[]> wallAt = new Dictionary<Vector3, int[]>();
            List<int> tops = new List<int>(2), bottoms = new List<int>(2);
            for (int q = 0; q + 5 < walls.Length; q += 6)
            {
                tops.Clear();
                bottoms.Clear();
                for (int k = q; k < q + 6; k++)
                {
                    int o = walls[k];
                    List<int> to = hairAt.Contains(from.rest[o]) ? tops : bottoms;
                    if (!to.Contains(o))
                    {
                        to.Add(o);
                    }
                }
                if (tops.Count != 2 || bottoms.Count != 2)
                {
                    continue;
                }
                Vector3 t0 = from.rest[tops[0]], t1 = from.rest[tops[1]], b0 = from.rest[bottoms[0]], b1 = from.rest[bottoms[1]];
                bool swap = (t0 - b1).magnitude + (t1 - b0).magnitude < (t0 - b0).magnitude + (t1 - b1).magnitude;
                wallAt[t0] = new[] { tops[0], bottoms[swap ? 1 : 0] };
                wallAt[t1] = new[] { tops[1], bottoms[swap ? 0 : 1] };
            }
            // The cut hair's open edges, its copies along seams welded by place (they share places exactly), each in
            // its triangle's winding.
            Dictionary<long, int> count = new Dictionary<long, int>();
            Dictionary<long, Vector3[]> ends = new Dictionary<long, Vector3[]>();
            Dictionary<Vector3, int> weld = new Dictionary<Vector3, int>();
            int triCount = all.Count;
            for (int k = 0; k + 2 < triCount; k += 3)
            {
                for (int c = 0; c < 3; c++)
                {
                    Vector3 pa = from.rest[hairKept[all[k + c]]], pb = from.rest[hairKept[all[k + (c + 1) % 3]]];
                    int a, b;
                    if (!weld.TryGetValue(pa, out a))
                    {
                        weld[pa] = a = weld.Count;
                    }
                    if (!weld.TryGetValue(pb, out b))
                    {
                        weld[pb] = b = weld.Count;
                    }
                    if (a == b)
                    {
                        continue;
                    }
                    long e = (long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b);
                    int n;
                    count.TryGetValue(e, out n);
                    count[e] = n + 1;
                    ends[e] = new[] { pa, pb };
                }
            }
            Dictionary<int, int> index = new Dictionary<int, int>();
            Func<int, int> indexOf = o =>
            {
                int i;
                if (!index.TryGetValue(o, out i))
                {
                    index[o] = i = kept.Count;
                    kept.Add(o);
                }
                return i;
            };
            foreach (KeyValuePair<long, int> e in count)
            {
                int[] wa, wb;
                Vector3[] end = ends[e.Key];
                if (e.Value != 1 || !wallAt.TryGetValue(end[0], out wa) || !wallAt.TryGetValue(end[1], out wb))
                {
                    continue;
                }
                // As the builder hangs it under an edge a to b: its tops run b to a, so it faces out of the space
                // between the hair and the head.
                int ta = indexOf(wa[0]), tb = indexOf(wb[0]), ba = indexOf(wa[1]), bb = indexOf(wb[1]);
                all.AddRange(new[] { tb, ta, ba, tb, ba, bb });
            }
        }

        // The hair of a style, read the first time it is asked for (see the class comment for where from); null while it
        // is being built, and (remembered) if there is none to be had.
        private Piece Hair(HairDef def)
        {
            return Styled(def != null ? def.texPath : null, StyleOf(def), false);
        }

        // The same for a beard.
        private Piece Beard(BeardDef def)
        {
            return Styled(def != null ? def.texPath : null, BeardStyleOf(def), true);
        }

        // Draws the beard of this style laid flat on the face patch (FaceLayers.DrawFlat), instead of as a 3D mesh: no mesh
        // is built, and it is drawn only where the face is (the caller leaves it off under a mask).
        public void DrawFlatBeard(Posed p, BeardDef style, Vector3 position, float yaw, float size, Color tint)
        {
            if (p.face == null)
            {
                return;
            }
            Material flat = FlatBeardOf(style);
            if (flat != null)
            {
                FaceLayers.DrawFlat(p.face, flat, Shade(Rounded(tint), 1f), position, yaw, size);
            }
        }

        // Draws a design of the FacialRidges gene on the face patch, in the skin's colour: its texture is in the patch's
        // own layout, so no Facial Animation is needed, and it follows the head and its body keys as the face does.
        public void DrawRidges(Posed p, string design, Vector3 position, float yaw, float size, Color tint)
        {
            if (p.face == null)
            {
                return;
            }
            Material mat = RidgesMaterial(design);
            if (mat != null)
            {
                FaceLayers.DrawFlat(p.face, mat, Shade(Rounded(tint), 1f), position, yaw, size);
            }
        }

        // A design's material, its texture read the first time it is drawn. Null, and kept so, if the file is missing.
        private Material RidgesMaterial(string design)
        {
            Material mat;
            if (ridges.TryGetValue(design, out mat))
            {
                return mat;
            }
            mat = null;
            string path = Path.Combine(folder, string.Format(RidgeFile, design));
            if (File.Exists(path))
            {
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                texture.LoadImage(File.ReadAllBytes(path));
                texture.name = "Marus_ridges_" + design;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Trilinear;
                texture.Apply(true, true);   // the mipmaps made, and the copy in memory let go: it is only drawn
                mat = FaceLayers.RidgeMaterial(texture);
            }
            else
            {
                Log.Warning(Tag + "the facial ridges file " + path + " is missing, so they are not drawn.");
            }
            ridges[design] = mat;
            return mat;
        }

        // The texture of a beard laid flat, made the first time it is drawn (HairBuilder.FlatFace): the sprite's south view in
        // the face patch's layout, fitted to the head as the 3D beard is. Null, and kept so, if the sprite or the head can't
        // be read.
        private Material FlatBeardOf(BeardDef def)
        {
            string name = BeardStyleOf(def);
            Material mat;
            if (flatBeards.TryGetValue(name, out mat))
            {
                return mat;
            }
            mat = null;
            HairBuilder.Sprites sprites = HairBuilder.Read(def.texPath, true);
            HairBuilder builder = sprites != null ? Builder(true) : null;
            Color32[] pixels = builder != null ? builder.FlatFace(sprites, FlatBeardSize) : null;
            if (pixels != null)
            {
                Texture2D texture = new Texture2D(FlatBeardSize, FlatBeardSize, TextureFormat.ARGB32, false);
                texture.name = "Marus_flat_" + name;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                texture.SetPixels32(pixels);
                texture.Apply(false, true);   // the copy in memory let go: only the GPU draws it
                mat = FaceLayers.FlatMaterial(texture);
            }
            flatBeards[name] = mat;
            return mat;
        }

        private Piece Styled(string texPath, string style, bool beard)
        {
            if (style == null)
            {
                return null;
            }
            TakeBuilt();
            TakeHairDetail();
            Piece h;
            if (hairs.TryGetValue(style, out h))
            {
                return h;
            }
            HairBuilder running = beard ? beardBuilder : builder;
            if (running != null && running.Busy(style))
            {
                return null;
            }
            foreach (string root in HairRoots())
            {
                if (File.Exists(Path.Combine(root, style + ".txt")))
                {
                    h = LoadHair(root, style, false, beard);
                    hairs[style] = h;
                    return h;
                }
            }
            // Built from its sprites, or from the cache if they are as they were when it was.
            HairBuilder.Sprites sprites = HairBuilder.Read(texPath, beard);
            HairBuilder b = sprites != null ? Builder(beard) : null;
            if (b == null)
            {
                hairs[style] = null;
                return null;
            }
            string mesh = Path.Combine(b.Cache, style + ".txt"), none = Path.Combine(b.Cache, style + ".none");
            if (SourceOf(mesh) == sprites.hash)
            {
                h = LoadHair(b.Cache, style, true, beard);
                hairs[style] = h;
                return h;
            }
            if (SourceOf(none) == sprites.hash)
            {
                hairs[style] = null;
                return null;
            }
            if (!b.Start(style, sprites, b.Cache))
            {
                hairs[style] = null;
            }
            return null;
        }

        // When the options' hair detail has changed, every style is read and cut down again as it is next asked for. Each
        // keeps its texture and materials (hairImages), as OutlineOrder keeps its copies of a material for good.
        private void TakeHairDetail()
        {
            float detail = SettingsMod.HairDetail;
            if (detail == hairsDetail)
            {
                return;
            }
            hairsDetail = detail;
            foreach (KeyValuePair<string, Piece> hair in hairs)
            {
                if (hair.Value != null)
                {
                    hairImages[hair.Key] = hair.Value;
                }
            }
            hairs.Clear();
            tucked.Clear();
            outlineTris.Clear();
            foreach (Shape shape in shapes.Values)
            {
                shape.hairs.Clear();
            }
        }

        // Each active mod's M3AThair folder (not this one's: it ships none).
        private List<string> HairRoots()
        {
            if (hairRoots != null)
            {
                return hairRoots;
            }
            hairRoots = new List<string>();
            string own = Path.GetFullPath(folder);
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                string dir = Path.Combine(mod.RootDir, ModHairFolder);
                if (Directory.Exists(dir) && !own.StartsWith(Path.GetFullPath(mod.RootDir)))
                {
                    hairRoots.Add(dir);
                    Log.Message(Tag + "hair for the chibi from " + mod.Name + " (" + dir + ").");
                }
            }
            return hairRoots;
        }

        // The builder, made the first time a style needs building; null if it can't be.
        // The body's vertices the head bone moves most (made once).
        private bool[] OnHead()
        {
            if (onHead != null)
            {
                return onHead;
            }
            Piece body = pieces[0];
            onHead = new bool[body.rest.Length];
            for (int i = 0; i < onHead.Length; i++)
            {
                // The bone that moves the vertex most is the head's, as hair.py's on_head.
                int best = -1;
                float most = 0f;
                for (int k = body.firstWeight[i]; k < body.firstWeight[i + 1]; k++)
                {
                    if (body.weight[k] > most)
                    {
                        most = body.weight[k];
                        best = body.weightBone[k];
                    }
                }
                onHead[i] = best == headBone;
            }
            return onHead;
        }

        // The body's vertices moved most by an arm bone from the upper arm down. Hair rides the head bone, and the arms
        // are never where they are at rest (held out from the sides), so the hair builder leaves them out of the body it
        // keeps the hair off: a lock hanging over the rest pose's upper arm was pushed out through the arm onto its far
        // side, its vertices all on the one corner (AFUf003's flared tuft), a spike standing out from the shoulder.
        private bool[] OnArm()
        {
            if (onArm != null)
            {
                return onArm;
            }
            Piece body = pieces[0];
            bool[] limb = new bool[boneHead.Length];
            foreach (KeyValuePair<string, int> pair in boneIndex)
            {
                string name = pair.Key.Split('.')[0];
                limb[pair.Value] = name == "upper_arm" || name == "forearm" || name == "hand" || name == "thumb";
            }
            onArm = new bool[body.rest.Length];
            for (int i = 0; i < onArm.Length; i++)
            {
                int best = -1;
                float most = 0f;
                for (int k = body.firstWeight[i]; k < body.firstWeight[i + 1]; k++)
                {
                    if (body.weight[k] > most)
                    {
                        most = body.weight[k];
                        best = body.weightBone[k];
                    }
                }
                onArm[i] = best >= 0 && limb[best];
            }
            return onArm;
        }

        private HairBuilder Builder(bool beard)
        {
            HairBuilder made = beard ? beardBuilder : builder;
            if (made != null)
            {
                return made;
            }
            string what = beard ? "beards" : "hairstyles";
            try
            {
                Piece body = pieces[0];
                string cache = Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "hair", stamp);
                PruneCaches(Path.GetDirectoryName(cache), stamp);
                made = new HairBuilder(body.rest, body.tris, OnHead(), body.keys, turnRound, boneHead[headBone], cache, beard, Scalp(), OnArm());
                Log.Message(Tag + what + " without a file are built from their sprites into " + cache + ".");
            }
            catch (Exception e)
            {
                Log.Warning(Tag + what + " without a file can't be built, so they are left off: " + e.Message);
            }
            if (beard)
            {
                beardBuilder = made;
            }
            else
            {
                builder = made;
            }
            return made;
        }

        // The cache folder is named after skin.txt's hash, so each export that changes the body leaves the last one's folder
        // unread for good (tens of MB each). Once a session, on a background thread so a large cache doesn't hold up the
        // game, this deletes every folder beside the current one that is named as Stamp names them (16 hex digits), and
        // nothing else.
        private static bool pruned;

        private static void PruneCaches(string root, string keep)
        {
            if (pruned)
            {
                return;
            }
            pruned = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        return;
                    }
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string name = Path.GetFileName(dir);
                        if (name == keep || !IsStamp(name))
                        {
                            continue;
                        }
                        try
                        {
                            Directory.Delete(dir, true);
                        }
                        catch (Exception e)
                        {
                            Log.Warning(Tag + "couldn't delete the old hair cache " + dir + ": " + e.Message);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warning(Tag + "couldn't look for old hair caches in " + root + ": " + e.Message);
                }
            });
        }

        private static bool IsStamp(string name)
        {
            if (name.Length != 16)
            {
                return false;
            }
            foreach (char c in name)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        // Once a frame: lets the builder save what it has finished, so the next ask reads it from the cache.
        private void TakeBuilt()
        {
            if ((builder == null && beardBuilder == null) || finishedAt == Time.frameCount)
            {
                return;
            }
            finishedAt = Time.frameCount;
            if (builder != null)
            {
                builder.Finish();
            }
            if (beardBuilder != null)
            {
                beardBuilder.Finish();
            }
        }

        // The sprites' hash a built file was made from (its first line), or null.
        private static string SourceOf(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                using (StreamReader r = new StreamReader(path))
                {
                    string line = r.ReadLine();
                    return line != null && line.StartsWith("# source ") ? line.Substring("# source ".Length).Trim() : null;
                }
            }
            catch (IOException)
            {
                return null;
            }
        }

        // Reads <root>/<style>.txt, .png and .shell.txt (if there is one), the mesh and shell cut down if cut is set; null
        // (and a message) if they can't be.
        private Piece LoadHair(string root, string style, bool cut, bool beard)
        {
            Piece h = null;
            string mesh = Path.Combine(root, style + ".txt"), tex = Path.Combine(root, style + ".png");
            if (!File.Exists(tex))
            {
                Log.Warning(Tag + "the hair " + style + " in " + root + " has no " + style + ".png, it is left off.");
                return null;
            }
            try
            {
                // The image first, as the hairline walls keep their texture places on its art (HairlineWalls.cs).
                Texture2D texture;
                Material material = null, mark = null;
                WallAtlas atlas;
                Piece was;
                if (hairImages.TryGetValue(style, out was))
                {
                    texture = was.texture;
                    material = was.material;
                    mark = was.mark;
                    atlas = was.wallAtlas;
                }
                else
                {
                    Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    t.LoadImage(File.ReadAllBytes(tex));
                    t.wrapMode = TextureWrapMode.Clamp;
                    t.filterMode = FilterMode.Bilinear;
                    t.name = "Marus_Hair_" + style;
                    texture = t;
                    atlas = AtlasOf(t, style);
                }
                h = ReadMesh(mesh, false, null);
                // A beard is small and its edge is most of it, so it keeps at least BeardTriangles.
                float share = beard ? Mathf.Max(SettingsMod.HairDetail, (float)BeardTriangles / Mathf.Max(h.tris.Length / 3, 1)) : SettingsMod.HairDetail;
                // Cut down (a M3AThair style keeps all its triangles), with walls from the hairline up to the cut hair
                // in place of those under its edges (HairlineWalls.cs).
                float cover;
                h = CutWithWalls(h, cut ? share : 1f, beard, atlas, out cover);
                if (turnRound)
                {
                    TurnRound(h.tris);
                }
                h.texture = texture;
                h.material = material;
                h.mark = mark;
                h.wallAtlas = atlas;
                if (h.uvs == null || h.rest.Length == 0)
                {
                    throw new InvalidDataException(mesh + ": no texture places or no vertices");
                }
                string shellPath = Path.Combine(root, style + ".shell.txt");
                if (File.Exists(shellPath))
                {
                    Piece shell = ReadMesh(shellPath, false, null);
                    if (cut)
                    {
                        shell = Decimated(shell, null, SettingsMod.HairDetail);
                    }
                    if (turnRound)
                    {
                        TurnRound(shell.tris);
                    }
                    int bodyTriangles = pieces[0].tris.Length / 3;
                    foreach (int k in shell.hides)
                    {
                        if (k < 0 || k >= bodyTriangles)
                        {
                            throw new InvalidDataException(shellPath + ": hides body triangle " + k + ", but the body has " + bodyTriangles);
                        }
                    }
                    h.shell = shell;
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not read the hair " + style + ", it is left off: " + e.Message);
                h = null;
            }
            return h;
        }

        // A hair image's art for the hairline walls (WallAtlas); null, and the walls take their texture places where they
        // meet the hair, if its pixels can't be read.
        private static WallAtlas AtlasOf(Texture2D t, string style)
        {
            try
            {
                return WallAtlas.Of(t.GetPixels32(), t.width, t.height);
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "couldn't read the pixels of the hair " + style + " for its walls: " + e.Message);
                return null;
            }
        }

        private static Shader hairShader, hairMarkShader;
        private static bool hairTried;

        // A hair's material: MarusSmoothTex with its sprites, set up as SolidMaterial. Null until IsoRim's materials
        // exist, or if the shader is missing.
        //
        // At night each face takes the light of its own cell from a second, light pass drawn over it at the same depth
        // (IsoGlow); what that pass misses takes the game's overlay pulled toward the camera instead, the light of the
        // ground behind it on screen. IsoRim's textured light pass (IsoRimLitMarkTex, which IsoGlow.Use would give the
        // hair) missed whole triangles of it, so at night the hair came out in patches of the wrong light that changed
        // as the camera tilted. So the hair has its own (MarusSmoothTexMark): its very shaders, texture and settings,
        // with only the light pass's render state, so it lands on every pixel the hair drew. Without that shader the
        // hair is left to the overlay, as IsoGlow.Use leaves a face it has no light pass for.
        // A textured garment's is made the same way.
        private static Material TexturedMaterial(Piece hair)
        {
            if (hair.material != null)
            {
                return hair.material;
            }
            if (!hairTried)
            {
                hairTried = true;
                ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(HairDef);
                hairShader = def != null ? def.Shader : null;
                if (hairShader == null)
                {
                    Log.Warning(Tag + "the " + HairDef + " shader is missing, so the chibi is drawn without hair and its clothes untextured.");
                }
                def = DefDatabase<ShaderDef>.GetNamedSilentFail(HairMarkDef);
                hairMarkShader = def != null ? def.Shader : null;
                if (hairShader != null && hairMarkShader == null)
                {
                    Log.Warning(Tag + "the " + HairMarkDef + " shader is missing, so the hair and textured clothes take the game's light overlay at night.");
                }
            }
            if (hairShader == null)
            {
                return null;
            }
            // Set up by IsoRim as unlit (no light pass of IsoRim's: ours is drawn as a part of its own), then given the figure
            // shader contract's model curve and own-cell light.
            Material m = new Material(hairShader);
            m.name = hair.texture.name;
            m.mainTexture = hair.texture;
            m.SetFloat("_ShadeBands", ShadeBands);
            if (!Api.ConfigureFigureMaterial(m, FigureMaterialKind.Unlit))
            {
                return null;   // IsoRim's materials aren't made yet: again next time
            }
            m.SetFloat("_LightMode", LightModelCurve);
            m.SetFloat("_GlowMode", hairMarkShader != null ? GlowOwnCell : GlowOverlay);
            if (hairMarkShader != null)
            {
                Material mark = new Material(hairMarkShader);
                mark.name = m.name + "_Mark";
                mark.mainTexture = hair.texture;
                mark.SetFloat("_ShadeBands", ShadeBands);
                Api.ConfigureFigureMaterial(mark, FigureMaterialKind.Unlit);
                mark.SetFloat("_LightMode", LightModelCurve);
                mark.SetFloat("_GlowMode", GlowOwnCell);
                mark.SetFloat("_MarkPass", 1f);
                hair.mark = mark;
            }
            hair.material = m;
            return m;
        }

        private static Color32[] Fill(Color32 c, int n)
        {
            Color32[] a = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = c;
            }
            return a;
        }
    }
}
