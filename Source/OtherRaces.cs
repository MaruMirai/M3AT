using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using Verse;

namespace M3AT
{
    // Other humanlike races as chibis (the otherRaces mod option, on by default). IsoRim draws every race but Human and
    // CreepJoiner as a flat card of the game's sprite unless an alien pack (IsoRim.AlienModelDef) makes it a figure, and
    // only figures are offered to our drawer, so a race without a pack (Ratkin, Milira, Wolfein, MoeLotl, ...) stayed 2D
    // among the chibis. With the option on, an alien figure rule (Api 25, AddAlienFigureRule) answers Human for every
    // such race, so IsoRim treats it as a person and the chibi draws it as one: its skin colour, hairstyle, apparel and
    // Facial Animation face (a race's own FA textures, FaceGaze's mouth and eye moves), and its ears, tails, horns and the
    // like built from their sprites (AddonParts.cs), but not its own head and body art. A race with a pack is left to it (null), so the Kiiro pack
    // still decides Kiiro, and IsoRim's "Keep as paper" list wins over the rule, so a player can keep any one race a card.
    // Where we hand a pawn back (invisible, attached), IsoRim draws it with its own human figure, addon cards and all.
    public static partial class WalkFigures
    {
        private static Func<ThingDef, FigureMode?> raceRule;
        private static bool raceRuleOn;

        internal static bool OtherRaces
        {
            get { return SettingsMod.Settings != null && SettingsMod.Settings.otherRaces; }
        }

        private static void AddRaceRule()
        {
            raceRule = RaceRule;   // one delegate, so the same one can be taken out again
            raceRuleOn = OtherRaces;
            if (raceRuleOn)
            {
                Api.AddAlienFigureRule(raceRule);
            }
        }

        // From the options window closing: IsoRim caches each race's mode, and adding or removing a rule asks them all again.
        internal static void OtherRacesChanged()
        {
            if (raceRule == null || raceRuleOn == OtherRaces)
            {
                return;
            }
            raceRuleOn = OtherRaces;
            try
            {
                if (raceRuleOn)
                {
                    Api.AddAlienFigureRule(raceRule);
                }
                else
                {
                    Api.RemoveAlienFigureRule(raceRule);
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not change how other races are drawn: " + e.Message);
            }
        }

        // Asked once per humanlike race but people (cached by IsoRim until a rule or the Keep as paper list changes).
        private static FigureMode? RaceRule(ThingDef race)
        {
            if (!OtherRaces || race == null || HasAlienPack(race.defName))
            {
                return null;
            }
            return FigureMode.Human;
        }

        private static HashSet<string> packedRaces;

        private static bool HasAlienPack(string race)
        {
            if (packedRaces == null)
            {
                HashSet<string> races = new HashSet<string>();
                foreach (AlienModelDef d in DefDatabase<AlienModelDef>.AllDefsListForReading)
                {
                    if (!d.alien.NullOrEmpty())
                    {
                        races.Add(d.alien);
                    }
                    if (d.aliens != null)
                    {
                        races.UnionWith(d.aliens);
                    }
                }
                packedRaces = races;
            }
            return packedRaces.Contains(race);
        }

        // The body key a race's own body type is drawn as. The game's (Male, Female, Thin, Fat, Hulk, Child, Baby) are
        // their own; a mod's by a word in its name, as IsoRim's alien packs default to (Milira_Female, RK_Thin), else
        // the gender's, as the base body with no key would be sexless.
        private static readonly Dictionary<BodyTypeDef, string> bodyKeys = new Dictionary<BodyTypeDef, string>();

        private static string BodyKeyOf(BodyTypeDef body, Gender gender)
        {
            if (body == null)
            {
                return null;
            }
            string key;
            if (bodyKeys.TryGetValue(body, out key))
            {
                return key ?? (gender == Gender.Female ? "Female" : "Male");
            }
            string name = body.defName;
            switch (name)
            {
                case "Male": case "Female": case "Thin": case "Fat": case "Hulk": case "Child": case "Baby":
                    key = name;
                    break;
                default:
                    key = Has(name, "female") || Has(name, "woman") ? "Female" : Has(name, "thin") || Has(name, "slim") ? "Thin"
                        : Has(name, "fat") ? "Fat" : Has(name, "hulk") || Has(name, "muscle") ? "Hulk" : Has(name, "child") || Has(name, "kid") ? "Child"
                        : Has(name, "baby") ? "Baby" : Has(name, "male") || Has(name, "man") ? "Male" : null;
                    break;
            }
            bodyKeys[body] = key;
            return key ?? (gender == Gender.Female ? "Female" : "Male");
        }

        private static bool Has(string name, string word)
        {
            return name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
