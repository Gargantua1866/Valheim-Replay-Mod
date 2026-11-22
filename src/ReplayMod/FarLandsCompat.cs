using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

namespace ReplayMod
{
    // FarLands builds the distant land on worker threads in real time, then eases it in. An export renders on a
    // fixed clock, so its frames reached the far view before the land did. Export holds its time while FarLands
    // is still building (see Exporter). Read by reflection; without FarLands, never building.
    internal static class FarLandsCompat
    {
        private static readonly Type Diagnostics = AccessTools.TypeByName("FarLands.FarLandsDiagnostics");
        private static readonly FieldInfo CompleteField = Diagnostics != null ? AccessTools.Field(Diagnostics, "Complete") : null;
        private static readonly FieldInfo FailedField = Diagnostics != null ? AccessTools.Field(Diagnostics, "Failed") : null;
        private static readonly Type PluginType = AccessTools.TypeByName("FarLands.FarLandsPlugin");
        private static readonly FieldInfo EnabledField = PluginType != null ? AccessTools.Field(PluginType, "Enabled") : null;

        public static bool Present => CompleteField != null;

        // The distant land isn't all there yet.
        public static bool Building
        {
            get
            {
                if (CompleteField == null) return false;
                try
                {
                    if (EnabledField?.GetValue(null) is ConfigEntry<bool> enabled && !enabled.Value) return false;
                    if (FailedField != null && (bool)FailedField.GetValue(null)) return false;
                    return !(bool)CompleteField.GetValue(null);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
