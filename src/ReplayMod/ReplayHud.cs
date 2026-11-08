using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ReplayMod
{
    // The HUD and minimap read the hidden viewer, so in a replay they show the recording player's
    // food, status effects and explored map instead, and the biome where the camera is.
    internal static class ReplayHud
    {
        private static ReplayData Data => ReplaySession.Data;

        public static void Reset()
        {
            _hudShown = null;
            _path = null;
            _explored = 0;
            _mapStale = true;
            _prevT = 0f;
            _lastJump = -10f;
        }

        public static void Update(Player ghost)
        {
            UpdateBiome(ghost);
            UpdateMap();
            HideDead(ghost);
        }

        // ---- Dead players

        // A player who dies isn't removed until they respawn: every machine hides the body when the
        // OnDeath RPC arrives. That RPC isn't replayed, and a seek has to bring the body back, so the
        // recorded dead flag decides.
        private static readonly AccessTools.FieldRef<Character, GameObject> Visual = AccessTools.FieldRefAccess<Character, GameObject>("m_visual");

        private static void HideDead(Player ghost)
        {
            foreach (Player p in Player.GetAllPlayers())
            {
                if (p == ghost) continue;
                ZDO z = p.GetComponent<ZNetView>()?.GetZDO();
                GameObject visual = Visual(p);
                if (z == null || !visual) continue;
                bool dead = z.GetBool(ZDOVars.s_dead);
                if (visual.activeSelf == dead) visual.SetActive(!dead);
            }
        }

        // ---- Food and status effects

        // A status effect that shows the text recorded with it (its time left).
        internal class RecordedStatus : StatusEffect
        {
            public string Text;
            public override string GetIconText() => Text;
        }

        private static HudSample _hudShown;
        private static readonly List<Player.Food> _foods = new List<Player.Food>();
        private static readonly List<StatusEffect> _effects = new List<StatusEffect>();

        private static void Refresh()
        {
            List<HudSample> list = Data.Huds;
            int lo = 0, hi = list.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (list[mid].T <= ReplaySession.T) lo = mid + 1; else hi = mid; }
            HudSample s = lo > 0 ? list[lo - 1] : null;
            if (s == _hudShown && _hudShown != null) return;
            var before = new HashSet<int>();
            foreach (StatusEffect e in _effects)
            {
                before.Add(e.NameHash());
                Object.Destroy(e);
            }
            _hudShown = s;
            _foods.Clear();
            _effects.Clear();
            if (s == null || !ObjectDB.instance) return;
            foreach (FoodSlot f in s.Foods)
            {
                GameObject prefab = ObjectDB.instance.GetItemPrefab(f.Prefab);
                ItemDrop drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (!drop) continue; // a mod's food that isn't installed now
                ItemDrop.ItemData item = drop.m_itemData.Clone();
                item.m_dropPrefab = prefab;
                _foods.Add(new Player.Food { m_name = f.Prefab, m_item = item, m_time = f.Time });
            }
            foreach (EffectSlot e in s.Effects)
            {
                StatusEffect source = ObjectDB.instance.GetStatusEffect(e.Hash);
                if (!source) continue;
                var shown = ScriptableObject.CreateInstance<RecordedStatus>();
                shown.name = source.name;
                shown.m_name = source.m_name;
                shown.m_icon = source.m_icon;
                shown.m_flashIcon = source.m_flashIcon;
                shown.m_cooldownIcon = source.m_cooldownIcon;
                shown.m_isNew = !before.Contains(e.Hash);
                shown.Text = e.Text;
                _effects.Add(shown);
            }
        }

        [HarmonyPatch(typeof(Hud), "UpdateStatusEffects")]
        private static class RecordedStatusEffects
        {
            private static void Prefix(List<StatusEffect> statusEffects)
            {
                if (!ReplaySession.Active || !ReplaySession.Ready) return;
                Refresh();
                statusEffects.Clear();
                statusEffects.AddRange(_effects);
            }
        }

        // Swapped into the viewer's own food list only for the call.
        [HarmonyPatch(typeof(Hud), "UpdateFood")]
        private static class RecordedFood
        {
            private static void Prefix(Player player, out List<Player.Food> __state)
            {
                __state = null;
                if (!ReplaySession.Active || !ReplaySession.Ready || !player) return;
                Refresh();
                List<Player.Food> own = player.GetFoods();
                __state = new List<Player.Food>(own);
                own.Clear();
                own.AddRange(_foods);
            }

            private static void Finalizer(Player player, List<Player.Food> __state)
            {
                if (__state == null) return;
                List<Player.Food> own = player.GetFoods();
                own.Clear();
                own.AddRange(__state);
            }
        }

        // ---- Biome

        // The game works out the biome in the player's physics step, which the viewer skips.
        private static readonly AccessTools.FieldRef<Player, BiomeSector> BiomeData = AccessTools.FieldRefAccess<Player, BiomeSector>("m_currentBiomeData");
        private static readonly AccessTools.FieldRef<Player, Heightmap.Biome> Biome = AccessTools.FieldRefAccess<Player, Heightmap.Biome>("m_currentBiome");
        private static float _nextBiome;

        private static void UpdateBiome(Player ghost)
        {
            if (Time.unscaledTime < _nextBiome || WorldGenerator.instance == null) return;
            _nextBiome = Time.unscaledTime + 0.5f;
            BiomeSector sector = WorldGenerator.instance.GetBiomeSector(ghost.transform.position);
            BiomeData(ghost) = sector;
            Biome(ghost) = sector.BiomeType.Biome;
        }

        // ---- Map

        // The map as it was when recording started, then explored along the recorded player's path the
        // way the game does it (100 m around them every 2 s). Going back in time builds it again.
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> Fog = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_fogTexture");
        private static readonly AccessTools.FieldRef<Minimap, float> ExploreInterval = AccessTools.FieldRefAccess<Minimap, float>("m_exploreInterval");
        private static readonly MethodInfo ExploreAt = AccessTools.Method(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) });
        private static readonly MethodInfo ResetAndExplore = AccessTools.Method(typeof(Minimap), "ResetAndExplore", new[] { typeof(BitArray), typeof(BitArray) });

        private static List<(float T, Vector3 Pos)> _path;
        private static int _explored;
        private static bool _mapStale;
        private static float _prevT, _lastJump;

        private static void BuildPath(float interval)
        {
            var ids = new HashSet<RecId> { new RecId(Data.Header.PlayerUser, Data.Header.PlayerId) };
            foreach (ReplayEvent s in Data.Selves) ids.Add(s.Id);
            _path = new List<(float, Vector3)>();
            foreach (ReplayEvent e in Data.Events)
            {
                if (e.Type != RecType.Zdo || !ids.Contains(e.Id)) continue;
                if (_path.Count == 0 || e.T - _path[_path.Count - 1].T >= interval) _path.Add((e.T, e.Pos));
            }
        }

        private static void UpdateMap()
        {
            Minimap map = Minimap.instance;
            if (!map || ResetAndExplore == null || ExploreAt == null) return;
            if (_path == null) BuildPath(ExploreInterval(map));
            float t = ReplaySession.T;
            int due = _explored;
            while (due < _path.Count && _path[due].T <= t) due++;
            // A seek or a drag on the timeline. Playing moves time a little each frame; this doesn't.
            float step = t - _prevT;
            _prevT = t;
            if (step < 0f || step > 1f) _lastJump = Time.unscaledTime;
            // Backward, or far forward: build the whole map, once the timeline has been let go of.
            if ((_explored > 0 && _path[_explored - 1].T > t) || due - _explored > 3) _mapStale = true;
            if (_mapStale)
            {
                if (Time.unscaledTime - _lastJump < 0.3f) return;
                Rebuild(map, t);
                _mapStale = false;
                return;
            }
            for (; _explored < due; _explored++) ExploreAt.Invoke(map, new object[] { _path[_explored].Pos, map.m_exploreRadius });
        }

        private static void Rebuild(Minimap map, float t)
        {
            int size = map.m_textureSize, pixels = size * size;
            BitArray explored = Bits(Data.MapExplored, pixels), others = Bits(Data.MapOthers, pixels);
            int r = Mathf.CeilToInt(map.m_exploreRadius / map.m_pixelSize);
            for (_explored = 0; _explored < _path.Count && _path[_explored].T <= t; _explored++)
            {
                Vector3 p = _path[_explored].Pos;
                int px = Mathf.RoundToInt(p.x / map.m_pixelSize + size / 2), py = Mathf.RoundToInt(p.z / map.m_pixelSize + size / 2);
                for (int y = Mathf.Max(py - r, 0); y <= Mathf.Min(py + r, size - 1); y++)
                {
                    for (int x = Mathf.Max(px - r, 0); x <= Mathf.Min(px + r, size - 1); x++)
                    {
                        if ((x - px) * (x - px) + (y - py) * (y - py) <= r * r) explored[y * size + x] = true;
                    }
                }
            }
            ResetAndExplore.Invoke(map, new object[] { explored, others });
            Fog(map).Apply();
        }

        // A recording's map that doesn't fit this map's size (or none, before 0.0.29) starts empty.
        private static BitArray Bits(byte[] packed, int pixels)
        {
            if (packed == null || packed.Length * 8 < pixels) return new BitArray(pixels);
            return new BitArray(packed) { Length = pixels };
        }

        // The game explores around the viewer, which follows the camera.
        [HarmonyPatch(typeof(Minimap), "UpdateExplore")]
        private static class NoViewerExplore
        {
            private static bool Prefix() => !ReplaySession.Active;
        }
    }
}
