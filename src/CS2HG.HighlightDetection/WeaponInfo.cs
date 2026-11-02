namespace CS2HG.HighlightDetection;

public static class WeaponInfo
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ak47"] = "AK-47",
        ["m4a1"] = "M4A4",
        ["m4a1_silencer"] = "M4A1-S",
        ["m4a1_silencer_off"] = "M4A1-S",
        ["awp"] = "AWP",
        ["ssg08"] = "SSG 08",
        ["scar20"] = "SCAR-20",
        ["g3sg1"] = "G3SG1",
        ["galilar"] = "Galil AR",
        ["famas"] = "FAMAS",
        ["aug"] = "AUG",
        ["sg556"] = "SG 553",
        ["deagle"] = "Desert Eagle",
        ["revolver"] = "R8 Revolver",
        ["glock"] = "Glock-18",
        ["hkp2000"] = "P2000",
        ["usp_silencer"] = "USP-S",
        ["usp_silencer_off"] = "USP-S",
        ["p250"] = "P250",
        ["fiveseven"] = "Five-SeveN",
        ["tec9"] = "Tec-9",
        ["cz75a"] = "CZ75-Auto",
        ["elite"] = "Dual Berettas",
        ["mac10"] = "MAC-10",
        ["mp9"] = "MP9",
        ["mp7"] = "MP7",
        ["mp5sd"] = "MP5-SD",
        ["ump45"] = "UMP-45",
        ["p90"] = "P90",
        ["bizon"] = "PP-Bizon",
        ["nova"] = "Nova",
        ["xm1014"] = "XM1014",
        ["sawedoff"] = "Sawed-Off",
        ["mag7"] = "MAG-7",
        ["m249"] = "M249",
        ["negev"] = "Negev",
        ["taser"] = "Zeus x27",
        ["hegrenade"] = "HE Grenade",
        ["inferno"] = "Molotov",
        ["molotov"] = "Molotov",
        ["incgrenade"] = "Incendiary",
        ["flashbang"] = "Flashbang",
        ["smokegrenade"] = "Smoke",
        ["decoy"] = "Decoy",
        ["planted_c4"] = "C4",
        ["world"] = "World",
    };

    public static string DisplayName(string weapon)
    {
        if (IsKnife(weapon)) return Core.Localization.L.T("weapon.knife");
        if (weapon == "world") return Core.Localization.L.T("weapon.world");
        if (weapon == "incgrenade") return Core.Localization.L.T("weapon.incendiary");
        return Names.TryGetValue(weapon, out var n) ? n : weapon.ToUpperInvariant();
    }

    public static bool IsKnife(string weapon) =>
        weapon.Contains("knife", StringComparison.OrdinalIgnoreCase) || weapon.Equals("bayonet", StringComparison.OrdinalIgnoreCase);

    public static bool IsZeus(string weapon) => weapon.Equals("taser", StringComparison.OrdinalIgnoreCase);

    public static bool IsAwp(string weapon) => weapon.Equals("awp", StringComparison.OrdinalIgnoreCase);

    public static bool IsHe(string weapon) => weapon.Equals("hegrenade", StringComparison.OrdinalIgnoreCase);

    public static bool IsFire(string weapon) =>
        weapon is "inferno" or "molotov" or "incgrenade";

    public static bool IsScoped(string weapon) => weapon is "awp" or "ssg08" or "scar20" or "g3sg1";
}
