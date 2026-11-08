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

        private void ReadType(string[] p)
        {
            if (p[0] == "weapon_type" && p.Length > 2)
            {
                weaponTypes[p[1]] = p[2];
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
        // (KindOf): a melee weapon one-handed below HeavyMelee kg and two-handed from it, any other its kind's in the
        // export (a pistol a handgun). Cached per ThingDef once the export is loaded (before, the options read anim.txt).
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
                    type = def.GetStatValueAbstract(StatDefOf.Mass) >= HeavyMelee ? "melee_2h" : "melee_1h";
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
            ThingDef def = weapon.def;
            string type = TypeOf(def);
            float scale;
            if (SettingsMod.Settings == null || !SettingsMod.Settings.weaponScales.TryGetValue(type, out scale))
            {
                scale = skin != null ? skin.TypeScale(type) : FileTypeScale(type);
                if (scale < 0f)
                {
                    return skin != null && skin.WeaponScale >= 0f ? skin.WeaponScale : WeaponScale;
                }
            }
            return scale / GroundRatio(weapon);
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
