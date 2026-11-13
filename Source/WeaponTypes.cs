using System;
using System.Collections.Generic;
using System.IO;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Weapon types: every weapon is one of a few (handgun, rifle, bow, grenade, melee_1h, melee_2h; walk.WEAPON_TYPES),
    // which sets how big it is drawn, held or holstered (walk.TYPE_SCALE, the mod options can change each), and which
    // holster slots it takes (walk.TYPE_HOLSTER). anim.txt's weapon_type and type_scale lines.
    public sealed partial class SkinnedFigure
    {
        private readonly Dictionary<string, string> weaponTypes = new Dictionary<string, string>();   // by weapon name or drafted kind
        private readonly Dictionary<string, float> typeScales = new Dictionary<string, float>();
        private readonly Dictionary<string, string> typeHolsters = new Dictionary<string, string>();
        private readonly List<string> typeOrder = new List<string>();
        private readonly Dictionary<string, string> holsterWeapons = new Dictionary<string, string>();   // by type

        // The weapon a type's holster slots and draw clips are posed for (walk.HOLSTER_WEAPON, anim.txt's holster_weapon
        // lines; an older export's are walk.py's as they were); null for none.
        public string HolsterWeapon(string type)
        {
            string name;
            if (type == null)
            {
                return null;
            }
            if (holsterWeapons.Count == 0)
            {
                return OldHolsterWeapons.TryGetValue(type, out name) ? name : null;
            }
            return holsterWeapons.TryGetValue(type, out name) ? name : null;
        }

        private static readonly Dictionary<string, string> OldHolsterWeapons = new Dictionary<string, string>
        {
            { "handgun", "Gun_Autopistol" }, { "rifle", "Gun_AssaultRifle" }, { "bow", "Bow_Recurve" },
            { "grenade", "Weapon_GrenadeFrag" }, { "melee_1h", "MeleeWeapon_Gladius" }, { "melee_2h", "MeleeWeapon_LongSword" },
        };

        private void ReadType(string[] p)
        {
            if (p[0] == "weapon_type" && p.Length > 2)
            {
                weaponTypes[p[1]] = p[2];
            }
            else if (p[0] == "holster_weapon" && p.Length > 2)
            {
                holsterWeapons[p[1]] = p[2];
            }
            else if (p[0] == "type_scale" && p.Length > 3)
            {
                if (!typeScales.ContainsKey(p[1]))
                {
                    typeOrder.Add(p[1]);
                }
                typeScales[p[1]] = Num(p[2]);
                typeHolsters[p[1]] = p[3];
            }
        }

        // The type the export gives this weapon name or drafted kind; null if it doesn't say.
        public string WeaponTypeOf(string name)
        {
            string type;
            return weaponTypes.TryGetValue(name, out type) ? type : null;
        }

        // A type's size as exported, against the weapon on the floor (the mod option's default); -1 if it has none.
        public float TypeScale(string type)
        {
            float scale;
            return type != null && typeScales.TryGetValue(type, out scale) ? scale : -1f;
        }

        // The holster slot size a type takes; null if the export doesn't say.
        public string TypeHolster(string type)
        {
            string size;
            return type != null && typeHolsters.TryGetValue(type, out size) ? size : null;
        }

        public List<string> Types
        {
            get { return typeOrder; }
        }
    }

    public static partial class WalkFigures
    {
        private static readonly Dictionary<ThingDef, string> typeOf = new Dictionary<ThingDef, string>();

        // A weapon's type: its own in the export (by WeaponName), else a grenade if it is thrown, else by its drafted kind
        // (KindOf): a melee weapon one-handed if Combat Extended marks it so (OneHandedByTag) or below HeavyMelee kg, else
        // two-handed, any other its kind's in the export (a pistol a handgun). Cached per ThingDef once the export is loaded (before, the options read anim.txt).
        private static string TypeOf(ThingDef def)
        {
            string type;
            if (typeOf.TryGetValue(def, out type))
            {
                return type;
            }
            type = ExportedType(WeaponName(def));
            if (type == null)
            {
                string kind = KindOf(def);
                if (Thrown(def))
                {
                    type = "grenade";
                }
                else if (kind == "melee")
                {
                    type = !OneHandedByTag(def) && def.GetStatValueAbstract(StatDefOf.Mass) >= HeavyMelee ? "melee_2h" : "melee_1h";
                }
                else
                {
                    type = ExportedType(kind) ?? (kind == "pistol" ? "handgun" : kind == "bow" ? "bow" : "rifle");
                }
            }
            if (skin != null)
            {
                typeOf[def] = type;
            }
            return type;
        }

        // The type the export gives a weapon name or drafted kind: the loaded figure's, else anim.txt's weapon_type lines.
        private static string ExportedType(string name)
        {
            if (skin != null)
            {
                return skin.WeaponTypeOf(name);
            }
            ReadTypeFile();
            string type;
            return fileTypes.TryGetValue(name, out type) ? type : null;
        }

        // How big a weapon is drawn, held or holstered, a share of its sprite's drawn size (DrawSlab): its type's size
        // against the weapon on the floor, in the mod options, else the export's, over this weapon's own GroundRatio
        // (for a weapon with an IsoRim model its size on the floor is smaller than its sprite's); else WeaponScale.
        // The figure may not be loaded yet (a weapon still in flight from a saved game, drawn before any pawn is), or
        // ever (the baked frames), so then anim.txt's type_scale lines stand in for its sizes.
        private static float ScaleOf(Thing weapon)
        {
            float scale = TypeSize(TypeOf(weapon.def));
            return scale < 0f ? (skin != null && skin.WeaponScale >= 0f ? skin.WeaponScale : WeaponScale) : scale / GroundRatio(weapon);
        }

        // A type's size against the weapon on the floor: the mod options', else the export's; -1 for neither.
        private static float TypeSize(string type)
        {
            float scale;
            if (SettingsMod.Settings == null || !SettingsMod.Settings.weaponScales.TryGetValue(type, out scale))
            {
                scale = skin != null ? skin.TypeScale(type) : FileTypeScale(type);
            }
            return scale;
        }

        // ---- Grips found in the art (GripFinder), for a weapon with no hold of its own.

        // The game's own weapon whose sprite each kind's hold is made on (preview.py's KIND_LOOK), to measure from.
        private static readonly Dictionary<string, string> GripReference = new Dictionary<string, string>
        {
            { "rifle", "Gun_AssaultRifle" },
            { "melee", "MeleeWeapon_Knife" },
        };

        public static bool AutoGrips   // false: a weapon with no hold of its own takes its kind's as it is (the mod's options set it)
        {
            get { return !NoAutoGrips && (SettingsMod.Settings == null || SettingsMod.Settings.autoGrips); }
        }

        // M3AT_NO_AUTOGRIP=1 turns it off for a run without touching the saved options (to compare autotest pictures).
        private static readonly bool NoAutoGrips = Environment.GetEnvironmentVariable("M3AT_NO_AUTOGRIP") == "1";

        // How far to slide the slab of a weapon with no hold of its own (held by its kind's) along its sprite's own
        // axes (x along the art's right, y its up; the same units as the slab's draw size, DrawSlab's), so that the grip
        // found in its art (GripFinder) comes to the fist as the grip found on the kind's own sprite does under that hold:
        // the fist's place on the kind's sprite, from that sprite's grip, put on this one's grip. fist, hand, a and top:
        // the fist and the hold's place and line as DrawWeapon has them (before the slab is laid down or holstered); size:
        // the figure's. Zero when it doesn't apply or the grip isn't clear in either sprite.
        private static Vector2 GripSlide(Thing weapon, string holdName, bool slab, Vector3 fist, Vector3 hand, Vector3 a, Vector3 top, float behind, float size)
        {
            ThingDef def = weapon.def;
            if (!AutoGrips || Thrown(def) || HoldOf(WeaponName(def), slab) != null)
            {
                return Vector2.zero;
            }
            string kind = KindOf(def), refName;
            if (holdName != kind || !GripReference.TryGetValue(kind, out refName))
            {
                return Vector2.zero;   // held by another kind's hold than its own (the rifle's, by default): nothing to measure against
            }
            ThingDef refDef = DefDatabase<ThingDef>.GetNamedSilentFail(refName);
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            if (refDef == null || refDef == def || refDef.graphicData == null || mat == null || mat.mainTexture == null)
            {
                return Vector2.zero;
            }
            string type = TypeOf(def);
            string find = kind == "rifle" ? "gun" : type == "melee_2h" ? "melee2" : "melee";
            Texture refArt = OwnArt(refDef.graphicData.texPath);
            Vector2 grip, refGrip;
            if (!GripFinder.Find(mat.mainTexture, find, def.equippedAngleOffset, out grip)
                || !GripFinder.Find(refArt, kind == "rifle" ? "gun" : "melee", refDef.equippedAngleOffset, out refGrip))
            {
                return Vector2.zero;
            }
            float scale = ScaleOf(weapon), own = TypeSize(type), theirs = TypeSize(TypeOf(refDef));
            Vector2 draw = g.drawSize * scale / size;
            Vector2 refDraw = refDef.graphicData.drawSize * scale * (own > 0f && theirs > 0f ? theirs / own : 1f) / size;
            // The fist's place from the slab's middle in each sprite's axes, as DrawSlab lays each out from this hold.
            Vector2 here = FistOnSprite(fist, hand, a, top, behind, def.equippedAngleOffset, draw);
            Vector2 there = FistOnSprite(fist, hand, a, top, behind, refDef.equippedAngleOffset, refDraw);
            Vector2 want = Vector2.Scale(grip - new Vector2(0.5f, 0.5f), draw) + there - Vector2.Scale(refGrip - new Vector2(0.5f, 0.5f), refDraw);
            return want - here;
        }

        // The fist from the slab's middle along a sprite's own axes (the slab's x and y, unit), as DrawSlab places a sprite
        // drawn draw big with this angle offset from this hold.
        private static Vector2 FistOnSprite(Vector3 fist, Vector3 hand, Vector3 a, Vector3 top, float behind, float offset, Vector2 draw)
        {
            Vector3 x, y;
            SpriteAxes(a, top, offset, out x, out y);
            Vector3 middle = hand + a * (behind * Mathf.Max(draw.x, draw.y));
            return new Vector2(Vector3.Dot(fist - middle, x), Vector3.Dot(fist - middle, y));
        }

        // A sprite's own axes on its slab, unit (DrawSlab's x and y before their size).
        private static void SpriteAxes(Vector3 a, Vector3 top, float offset, out Vector3 x, out Vector3 y)
        {
            float o = offset * Mathf.Deg2Rad, c = Mathf.Cos(o), sn = Mathf.Sin(o);
            x = (a * c - top * sn).normalized;
            y = (a * sn + top * c).normalized;
        }

        // A type's size from anim.txt's type_scale lines; -1 if it has none.
        private static float FileTypeScale(string type)
        {
            ReadTypeFile();
            foreach (KeyValuePair<string, float> t in typeDefaults)
            {
                if (t.Key == type)
                {
                    return t.Value;
                }
            }
            return -1f;
        }

        private static List<KeyValuePair<string, float>> typeDefaults;
        private static Dictionary<string, string> fileTypes;

        // Each type and its exported size, for the mod options: from the loaded figure, else read from anim.txt's
        // type_scale lines (the options can be opened before any pawn is drawn). Empty if neither has any.
        public static List<KeyValuePair<string, float>> TypeDefaults()
        {
            if (skin != null && skin.Types.Count > 0)
            {
                List<KeyValuePair<string, float>> found = new List<KeyValuePair<string, float>>();
                foreach (string type in skin.Types)
                {
                    found.Add(new KeyValuePair<string, float>(type, skin.TypeScale(type)));
                }
                return found;
            }
            ReadTypeFile();
            return typeDefaults;
        }

        // anim.txt's type_scale and weapon_type lines, once, for when the figure isn't loaded.
        private static void ReadTypeFile()
        {
            if (typeDefaults != null)
            {
                return;
            }
            typeDefaults = new List<KeyValuePair<string, float>>();
            fileTypes = new Dictionary<string, string>();
            try
            {
                string path = content != null ? Path.Combine(content.RootDir, Folder, "anim.txt") : null;
                if (path != null && File.Exists(path))
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        if (line.StartsWith("weapon_type "))
                        {
                            string[] w = line.Split(' ');
                            if (w.Length > 2)
                            {
                                fileTypes[w[1]] = w[2];
                            }
                            continue;
                        }
                        if (!line.StartsWith("type_scale "))
                        {
                            continue;
                        }
                        string[] p = line.Split(' ');
                        float scale;
                        if (p.Length > 2 && float.TryParse(p[2], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out scale))
                        {
                            typeDefaults.Add(new KeyValuePair<string, float>(p[1], scale));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "reading the weapon sizes from anim.txt failed: " + e.Message);
            }
        }

        private static readonly Dictionary<ThingDef, float> groundRatios = new Dictionary<ThingDef, float>();
        private static bool groundRatiosOn;

        // How many times bigger a weapon is drawn held than on the ground at a scale of 1, so its size can be given against
        // the ground. The scale is a share of the sprite's drawn size (ScaleOf), and a weapon with an IsoRim model has it
        // fitted to the sprite's length (WeaponFit), while IsoRim draws it on the ground at its ModelDef's scale: about 0.73
        // of the sprite for IsoRim's own models. 1 for a weapon without a model (the sprite both ways) or a thrown one
        // (WeaponFit.AtGroundSize sizes that as on the ground already). Each weapon's own, as an average over its type let
        // the modelled ones be drawn bigger than on the ground wherever the type also had weapons without a model. Worked
        // out again when IsoRim's weapon models setting changes.
        public static float GroundRatio(Thing weapon)
        {
            bool on = ModelsOn();
            if (groundRatiosOn != on)
            {
                groundRatiosOn = on;
                groundRatios.Clear();
            }
            float ratio;
            if (!groundRatios.TryGetValue(weapon.def, out ratio))
            {
                ratio = on ? GroundRatioOf(weapon) : 1f;
                groundRatios[weapon.def] = ratio;
            }
            return ratio;
        }

        // IsoRim's weapon models setting ("Weapons as 3D models"), by its field name.
        private static bool ModelsOn()
        {
            object on = Api.GetOption("weaponModels3D");
            return !(on is bool) || (bool)on;
        }

        // One weapon's held size over its ground size at a scale of 1: its model's fit to the sprite (drawn cells per model
        // unit, as WeaponFit.AtGroundSize measures it) over its ModelDef's scale (IsoRim's ground size, cells per model unit).
        // By the first of its kind drawn: the same for every one of a def.
        private static float GroundRatioOf(Thing weapon)
        {
            ThingModelInfo info;
            WeaponFit.Hold h;
            float ground = GroundScaleOf(weapon);
            if (Thrown(weapon.def) || ground <= 0f || !HeldModel(weapon, out info, out h))
            {
                return 1f;
            }
            Graphic g = weapon.Graphic;
            Matrix4x4 drawn = Matrix4x4.Scale(new Vector3(g.drawSize.x, g.drawSize.y, 1f)) * h.slab;
            return ((Vector3)drawn.GetColumn(0)).magnitude / ground;
        }
    }
}
