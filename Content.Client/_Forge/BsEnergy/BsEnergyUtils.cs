using Content.Shared._Forge.BsEnergy;

namespace Content.Client._Forge.BsEnergy;

public sealed class BsEnergyUtils
{
    private const int KvtConst = BsEnergySettings.KvtConst;
    private const int MvtConst = BsEnergySettings.MvtConst;

    public static string GetPowerText(int watts)
    {
        string prefix;
        string textValue;
        if (watts >= MvtConst)
        {
            prefix = Loc.GetString("ui-bs-energy-power-mvt");
            textValue = $"{(float)watts / MvtConst:F2}";
        }
        else
        {
            prefix = Loc.GetString("ui-bs-energy-power-kvt");
            textValue = $"{watts / KvtConst}";
        }

        return $"{textValue} {prefix}";
    }
}
