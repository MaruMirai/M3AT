using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Verse;

namespace M3AT
{
    // Races that wear another race's Facial Animation faces. Facial Animation picks each face part (head, eyes, lids,
    // brows, mouth, skin, ...) from the defs whose raceName is the pawn's race, else from the plain (human) ones, by
    // FaceTypeGenerator<T>'s per-race list. A sub-race with no faces of its own would get human faces, though its parent
    // race's suit it: Milira's humanlike Milian_Race (a humanized Milian mech) is Milira_Race's child, and Milira Race
    // Facial Animation faces only Milira_Race. So its list is pointed at the parent's. Its comps come from
    // Patches/MilianFaces.xml, as Milian_Race has none of Facial Animation's.
    public static partial class FaceLayers
    {
        // Wearer: whose faces. Only where the wearer has no faces of its own and both races are loaded.
        private static readonly Dictionary<string, string> FaceStandIns = new Dictionary<string, string>
        {
            { "Milian_Race", "Milira_Race" },
        };

        private static readonly HashSet<string> borrowed = new HashSet<string>();

        // Whether this race wears another's Facial Animation faces (set at startup by BorrowFaces).
        internal static bool BorrowsFaces(string race)
        {
            return race != null && borrowed.Contains(race);
        }

        // From the startup patching: every face part type's per-race list for each wearer pointed at its parent's.
        // FaceTypeGenerator<T> builds its lists from the defs once, on first use, so it is made to now (the defs are loaded).
        public static void BorrowFaces()
        {
            try
            {
                if (!ModsConfig.IsActive("Nals.FacialAnimation"))
                {
                    return;
                }
                Type faceType = GenTypes.GetTypeInAnyAssembly("FacialAnimation.FaceTypeDef");
                Type generator = faceType != null ? faceType.Assembly.GetType("FacialAnimation.FaceTypeGenerator`1") : null;
                FieldInfo raceName = faceType != null ? faceType.GetField("raceName", BindingFlags.Public | BindingFlags.Instance) : null;
                Type drawComp = GenTypes.GetTypeInAnyAssembly("FacialAnimation.DrawFaceGraphicsComp");
                if (generator == null || raceName == null || drawComp == null)
                {
                    Log.Warning(Tag + "Facial Animation's face lists were not found; races that wear another's faces get human ones.");
                    return;
                }
                List<Type> parts = GenTypes.AllSubclassesNonAbstract(faceType).ToList();
                foreach (KeyValuePair<string, string> pair in FaceStandIns)
                {
                    ThingDef wearer = DefDatabase<ThingDef>.GetNamedSilentFail(pair.Key);
                    ThingDef owner = DefDatabase<ThingDef>.GetNamedSilentFail(pair.Value);
                    if (wearer == null || owner == null || wearer.comps == null || !wearer.comps.Any(c => c.compClass == drawComp))
                    {
                        continue;   // not loaded, or not faced by Facial Animation (Patches/MilianFaces.xml wants Milira Race Facial Animation)
                    }
                    bool any = false;
                    foreach (Type part in parts)
                    {
                        // A part the wearer has its own defs for keeps them.
                        bool own = false;
                        foreach (Def d in GenDefDatabase.GetAllDefsInDatabaseForDef(part))
                        {
                            if (raceName.GetValue(d) as string == pair.Key)
                            {
                                own = true;
                                break;
                            }
                        }
                        if (own)
                        {
                            continue;
                        }
                        Type made = generator.MakeGenericType(part);
                        RuntimeHelpers.RunClassConstructor(made.TypeHandle);
                        FieldInfo listField = made.GetField("raceFaceTypeList", BindingFlags.NonPublic | BindingFlags.Static);
                        IDictionary lists = listField != null ? listField.GetValue(null) as IDictionary : null;
                        if (lists == null || !lists.Contains(pair.Value))
                        {
                            continue;
                        }
                        lists[pair.Key] = lists[pair.Value];
                        any = true;
                    }
                    if (any && !BorrowAdjustment(wearer, owner))
                    {
                        any = false;
                    }
                    if (any)
                    {
                        borrowed.Add(pair.Key);
                        Log.Message(Tag + pair.Key + " wears " + pair.Value + "'s Facial Animation faces.");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "couldn't share Facial Animation faces between races: " + e.Message);
            }
        }

        // Facial Animation's face adjustment for the wearer (GraphicHelper.faceAdjustmentDict, by race): a copy of the
        // owner's face size and place, with the eye labels found on the wearer's own body. FA's eyeball controller looks
        // the eyes up by those labels with First(), and the Milian body calls them "right visual sensor" and "left visual
        // sensor", not "right eye": not found, it threw and the pawn got no face nodes at all. A wearer with its own
        // adjustment keeps it. False when the eyes can't be found, as FA would throw for the wearer.
        private static bool BorrowAdjustment(ThingDef wearer, ThingDef owner)
        {
            Type helper = GenTypes.GetTypeInAnyAssembly("FacialAnimation.GraphicHelper");
            FieldInfo dictField = helper != null ? helper.GetField("faceAdjustmentDict", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static) : null;
            if (dictField == null)
            {
                return false;
            }
            RuntimeHelpers.RunClassConstructor(helper.TypeHandle);
            IDictionary adjustments = dictField.GetValue(null) as IDictionary;
            if (adjustments == null || !adjustments.Contains(owner.defName))
            {
                return false;
            }
            Def ours = adjustments.Contains(wearer.defName) ? adjustments[wearer.defName] as Def : null;
            Def theirs = adjustments[owner.defName] as Def;
            if (theirs == null || (ours != null && ours.defName != "DefaultFaceSizeAndPositionDef"))
            {
                return ours != null;
            }
            FieldInfo rightField = theirs.GetType().GetField("RightEyeLabel");
            FieldInfo leftField = theirs.GetType().GetField("LeftEyeLabel");
            if (rightField == null || leftField == null || wearer.race == null || wearer.race.body == null)
            {
                return false;
            }
            string right = EyeLabel(wearer, rightField.GetValue(theirs) as string, "RightEye");
            string left = EyeLabel(wearer, leftField.GetValue(theirs) as string, "LeftEye");
            if (right == null || left == null)
            {
                Log.Warning(Tag + wearer.defName + "'s eyes were not found on its body; it keeps Facial Animation's plain faces.");
                return false;
            }
            Def copy = (Def)typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(theirs, null);
            rightField.SetValue(copy, right);
            leftField.SetValue(copy, left);
            adjustments[wearer.defName] = copy;
            return true;
        }

        // The label FA would look an eye up by on this race's body: its own if a part has it, else the part with the
        // game's eye wound anchor (RightEye, LeftEye).
        private static string EyeLabel(ThingDef race, string label, string anchor)
        {
            List<BodyPartRecord> parts = race.race.body.AllParts;
            if (parts.Any(p => p.untranslatedCustomLabel == label))
            {
                return label;
            }
            BodyPartRecord found = parts.FirstOrDefault(p => p.woundAnchorTag == anchor && !string.IsNullOrEmpty(p.untranslatedCustomLabel));
            return found != null ? found.untranslatedCustomLabel : null;
        }
    }
}
