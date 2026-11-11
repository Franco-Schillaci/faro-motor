namespace Aion2DpsMeter.Core.Data;

public enum JobClass
{
    Unknown,
    Gladiator,
    Templar,
    Ranger,
    Assassin,
    Sorcerer,
    Cleric,
    Elementalist,
    Chanter,
    Fighter,
}

public static class JobClasses
{
    /// <summary>Class from the party roster / self record encoding.</summary>
    public static JobClass FromRosterClass(uint value) => value switch
    {
        // Fork (Faro): the base codes 2-4 and the Brawler's 45-48, as listed by AcompanION's game data.
        2 or (>= 5 and <= 8) => JobClass.Gladiator,
        3 or (>= 9 and <= 12) => JobClass.Templar,
        4 or (>= 13 and <= 16) => JobClass.Ranger,
        >= 17 and <= 20 => JobClass.Assassin,
        >= 21 and <= 24 => JobClass.Elementalist,
        >= 25 and <= 28 => JobClass.Sorcerer,
        >= 29 and <= 32 => JobClass.Cleric,
        >= 33 and <= 36 => JobClass.Chanter,
        >= 45 and <= 48 => JobClass.Fighter,
        _ => JobClass.Unknown,
    };

    private static JobClass FromPrefix(int prefix) => prefix switch
    {
        11 => JobClass.Gladiator,
        12 => JobClass.Templar,
        13 => JobClass.Assassin,
        14 => JobClass.Ranger,
        15 => JobClass.Sorcerer,
        16 => JobClass.Elementalist,
        17 => JobClass.Cleric,
        18 => JobClass.Chanter,
        19 => JobClass.Fighter,
        _ => JobClass.Unknown,
    };

    /// <summary>
    /// Class from a skill code: class skills are 1X_XXX_XXX where X is the class prefix.
    /// Elementalist summons use their own bands.
    /// </summary>
    public static JobClass FromSkill(int skillCode)
    {
        if ((skillCode >= 100510 && skillCode <= 103500) || (skillCode >= 109300 && skillCode <= 109362))
            return JobClass.Elementalist;

        if (skillCode < 10_000_000 || skillCode > 19_999_999)
            return JobClass.Unknown;

        int prefix = skillCode / 1_000_000;
        int sub = (skillCode / 10000) % 100;
        if (sub == 0)
        {
            if (prefix == 16)
            {
                int command = (skillCode / 100) % 100;
                if (command >= 11 && command <= 13)
                    return JobClass.Elementalist;
            }
            return JobClass.Unknown;
        }

        if (prefix == 16)
        {
            bool pcRange = sub is (>= 1 and <= 8) or 14 or 15 or 17 or 19 or (>= 21 and <= 26)
                or 30 or 31 or 32 or 34 or 35 or 36 or 37 or (>= 70 and <= 76) or 80;
            return pcRange ? JobClass.Elementalist : JobClass.Unknown;
        }

        return FromPrefix(prefix);
    }

    /// <summary>Skills only players use: class skills, the alternate band and basic attacks.</summary>
    public static bool IsPlayerSkill(int skillCode) =>
        (skillCode >= 11_000_000 && skillCode <= 19_999_999)
        || (skillCode >= 3_000_000 && skillCode <= 3_999_999);
}
