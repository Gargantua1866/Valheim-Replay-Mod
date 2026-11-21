using System;
using System.Reflection;
using HarmonyLib;

namespace ReplayMod
{
    // Worldgen mods built on Worldhalla (Riverheim, for one) keep their generator on the World object, read
    // from the world file, so a replay world made from the seed alone got vanilla terrain. The recording keeps
    // that data as Worldhalla writes it into a world file, and the replay world gets it back. All by reflection:
    // without Worldhalla nothing here does anything.
    internal static class CustomWorldgen
    {
        private static readonly Type Extension = AccessTools.TypeByName("Worldhalla.Plugin.Core.Extensions.WorldExtension");
        private static readonly Type Patch = AccessTools.TypeByName("Worldhalla.Plugin.Core.Patches.WorldPatch");
        private static readonly MethodInfo GetGenerator = Extension != null ? AccessTools.Method(Extension, "GetGenerator") : null;
        private static readonly MethodInfo SetGenerator = Extension != null ? AccessTools.Method(Extension, "SetGenerator") : null;
        private static readonly MethodInfo WriteHeader = Patch != null ? AccessTools.Method(Patch, "WriteHeader") : null;
        private static readonly MethodInfo WriteWorldData = Patch != null ? AccessTools.Method(Patch, "WriteWorldData") : null;
        private static readonly MethodInfo ReadHeader = Patch != null ? AccessTools.Method(Patch, "ReadHeader") : null;
        private static readonly MethodInfo ReadWorldData = Patch != null ? AccessTools.Method(Patch, "ReadWorldData") : null;

        private static object Generator(World world) => GetGenerator?.Invoke(null, new object[] { world });

        // The world's custom generator data, or empty for a vanilla world.
        public static byte[] Save(World world)
        {
            if (world == null || WriteHeader == null || WriteWorldData == null) return Array.Empty<byte>();
            try
            {
                if (Generator(world) == null) return Array.Empty<byte>();
                var pkg = new ZPackage();
                WriteHeader.Invoke(null, new object[] { pkg, world });
                WriteWorldData.Invoke(null, new object[] { pkg, world });
                return pkg.GetArray();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"couldn't record the world's custom generator: {e.InnerException?.Message ?? e.Message}");
                return Array.Empty<byte>();
            }
        }

        // Puts the recorded generator on the replay world; for older recordings, the one of the saved world with
        // the same name and seed. False = vanilla terrain.
        public static bool Apply(World world, byte[] recorded, string worldName, int seed)
        {
            if (GetGenerator == null) return false;
            try
            {
                if (recorded != null && recorded.Length > 0 && ReadHeader != null && ReadWorldData != null)
                {
                    var pkg = new ZPackage(recorded);
                    object[] header = { world.m_name, pkg, null };
                    if ((bool)ReadHeader.Invoke(null, header))
                        ReadWorldData.Invoke(null, new object[] { world.m_name, pkg, world, header[2] });
                    if (Usable(world, "the recording")) return true;
                }
                else if (SaveSystem.TryGetSaveByName(worldName, SaveDataType.World, out SaveWithBackups save) && !save.IsDeleted)
                {
                    World saved = World.LoadWorld(save);
                    object generator = saved != null && saved.m_seed == seed ? Generator(saved) : null;
                    if (generator != null)
                    {
                        SetGenerator.Invoke(null, new object[] { world, generator });
                        if (Usable(world, $"the saved world '{worldName}'")) return true;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"couldn't set the replay world's custom generator: {e.InnerException?.Message ?? e.Message}");
            }
            Clear(world);
            return false;
        }

        // A generator that isn't installed comes back as Worldhalla's "incompatible" stand-in, which would also
        // mark the world unloadable; the replay goes on with vanilla terrain instead.
        private static bool Usable(World world, string from)
        {
            object generator = Generator(world);
            if (generator == null) return false;
            if (generator.GetType().Name != "CompatibleGeneratorBinding")
            {
                Plugin.Log.LogWarning($"the recorded world uses custom world generation that isn't installed; the replay shows vanilla terrain");
                return false;
            }
            Plugin.Log.LogInfo($"replay world uses the custom world generation from {from}");
            return true;
        }

        // The game keeps one cached minimap per world folder, checked against the seed only; every replay uses the
        // same folder. A note beside it says which generator drew it, and a different one throws it away.
        public static void KeepMinimapCacheHonest(World world)
        {
            try
            {
                byte[] data = Save(world);
                string key = world.m_seed + " " + (data.Length == 0 ? "vanilla"
                    : Convert.ToBase64String(System.Security.Cryptography.SHA1.Create().ComputeHash(data)));
                string dir = world.GetSaveDirectory(FileHelpers.FileSource.Local);
                string note = System.IO.Path.Combine(dir, "replaymod-minimap.txt");
                if (System.IO.File.Exists(note) && System.IO.File.ReadAllText(note) == key) return;
                foreach (string name in new[] { "cacheMinimapMask", "cacheMinimapBiome", "cacheMinimapHeight", "cacheMinimapMeta" })
                {
                    string file = System.IO.Path.Combine(dir, name);
                    if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                }
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(note, key);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                Plugin.Log.LogWarning($"couldn't check the replay minimap cache: {e.Message}");
            }
        }

        private static void Clear(World world)
        {
            if (SetGenerator == null) return;
            try { SetGenerator.Invoke(null, new object[] { world, null }); }
            catch (Exception) { }
            world.m_dataError = default; // SaveDataError.None
        }
    }
}
