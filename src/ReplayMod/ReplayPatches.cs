using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ReplayMod
{
    // Everything that would let the replay world simulate, save or claim the recorded objects.
    internal static class ReplayPatches
    {
        private static bool IsGhost(Character c) => ReplaySession.Active && c == Player.m_localPlayer && c != null;

        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
        private static class NoOwnershipClaims
        {
            private static bool Prefix() => !ReplaySession.Active;
        }

        // Recorded objects never change owner in a replay. The hidden viewer is the recording character
        // (same player ID), so mods that hand a vehicle to "the local pilot" claimed the recorded
        // vehicle and ran live physics on it. Both entry points: SetOwner can inline.
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
        private static class KeepRecordedOwnerZdo
        {
            private static bool Prefix(ZDO __instance) => !ReplaySession.Active || __instance.GetOwner() != ReplaySession.FakeOwner;
        }

        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.ClaimOwnership))]
        private static class KeepRecordedOwnerView
        {
            private static bool Prefix(ZNetView __instance) =>
                !ReplaySession.Active || __instance.GetZDO() == null || __instance.GetZDO().GetOwner() != ReplaySession.FakeOwner;
        }

        [HarmonyPatch(typeof(ZDOMan), "RemoveOrphanNonPersistentZDOS")]
        private static class KeepFakeOwnedZdos
        {
            private static bool Prefix() => !ReplaySession.Active;
        }

        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        private static class NoSpawning
        {
            private static bool Prefix() => !ReplaySession.Active;
        }

        [HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
        private static class NoRaids
        {
            private static bool Prefix() => !ReplaySession.Active;
        }

        // Locations only spawn in zones we never generate, so skip the minute-long placement pass.
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GenerateLocationsIfNeeded))]
        private static class NoLocationGeneration
        {
            private static readonly MethodInfo Setter = AccessTools.PropertySetter(typeof(ZoneSystem), nameof(ZoneSystem.LocationsGenerated));

            private static bool Prefix(ZoneSystem __instance)
            {
                if (!ReplaySession.Active) return true;
                Setter.Invoke(__instance, new object[] { true });
                return false;
            }
        }

        // Never read a world file into the replay, even a stale one with our name.
        [HarmonyPatch]
        private static class NoWorldLoad
        {
            private static readonly MethodInfo WorldSetup = AccessTools.Method(typeof(ZNet), "WorldSetup");

            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ZNet), "LoadWorld");
                yield return AccessTools.Method(typeof(ZNet), "LoadOldWorld");
            }

            private static bool Prefix(ZNet __instance)
            {
                if (!ReplaySession.Active) return true;
                WorldSetup.Invoke(__instance, null);
                return false;
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Save))]
        private static class NoWorldSave
        {
            private static bool Prefix() => !ReplaySession.Active;
        }

        [HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
        private static class NoProfileSave
        {
            private static bool Prefix() => !ReplaySession.Active;
        }

        [HarmonyPatch(typeof(Game), "FindSpawnPoint")]
        private static class SpawnAtRecording
        {
            private static bool Prefix(ref bool __result, ref Vector3 point, ref bool usedLogoutPoint)
            {
                if (!ReplaySession.Active) return true;
                __result = ReplaySession.FindSpawnPoint(out point);
                usedLogoutPoint = true; // keeps the profile's home point untouched
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class GhostOnSpawn
        {
            private static void Postfix(Player __instance)
            {
                if (ReplaySession.Active && __instance == Player.m_localPlayer) ReplaySession.OnLocalPlayerSpawned(__instance);
            }
        }

        // The hidden viewer must not move, fall, pick things up or take damage.
        [HarmonyPatch(typeof(Player), "FixedUpdate")]
        private static class GhostNoPlayerUpdate
        {
            private static bool Prefix(Player __instance) => !IsGhost(__instance);
        }

        [HarmonyPatch(typeof(Character), nameof(Character.CustomFixedUpdate))]
        private static class GhostNoCharacterUpdate
        {
            private static bool Prefix(Character __instance) => !IsGhost(__instance);
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.CustomFixedUpdate))]
        private static class GhostNoHumanoidUpdate
        {
            private static bool Prefix(Character __instance) => !IsGhost(__instance);
        }

        [HarmonyPatch(typeof(Chat), nameof(Chat.SendText))]
        private static class NoArrivedShout
        {
            private static bool Prefix() => !ReplaySession.Active;
        }

        // Replay speed. Game.UpdatePause rewrites Time.timeScale every frame.
        [HarmonyPatch(typeof(Game), "UpdatePause")]
        private static class ReplaySpeed
        {
            private static void Postfix()
            {
                if (!ReplaySession.Active || !ReplaySession.Ready || Game.IsPaused()) return;
                Time.timeScale = ReplaySession.Paused ? 0f : ReplaySession.PlaySpeed;
            }
        }

        // Game buttons do nothing in a replay, except the menu (Escape) so you can log out. Stopped where
        // the press enters ZInput: ZInput.GetButton* get inlined into their callers, so a patch there
        // misses some of them (G radial, T emotes, C walk).
        [HarmonyPatch(typeof(ZInput), "OnActionPerformed")]
        private static class MuteGameButtons
        {
            private static bool Prefix(InputAction.CallbackContext ctx)
            {
                if (!ReplaySession.Active || !ReplaySession.Ready) return true;
                string name = ctx.action.name;
                return name == "Menu" || name == "JoyMenu";
            }
        }

        // Patches on methods the game calls very often, applied only while a replay is open: even a postfix
        // that returns at once costs a little on every call in normal play.
        internal static class ReplayOnly
        {
            private static Harmony _harmony;

            internal static void Apply()
            {
                if (_harmony != null) return;
                _harmony = new Harmony(Plugin.Guid + ".replay");
                _harmony.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.GetSyncedSimulationDistance)),
                    postfix: new HarmonyMethod(typeof(ReplayTerrainRadius), "Postfix"));
                _harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.IsTeleporting)),
                    postfix: new HarmonyMethod(typeof(LoadLikeAPortal), "Postfix"));
            }

            internal static void Remove()
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
            }
        }

        // A wider full-detail terrain radius in replays (see ReplaySession.FindSpawnPoint). See ReplayOnly.
        private static class ReplayTerrainRadius
        {
            private static void Postfix(ref SimulationDistance __result)
            {
                if (!ReplaySession.Active) return;
                int near = Mathf.Max(__result.NearSimulationDistance, Plugin.ReplayLoadRadius.Value);
                __result = new SimulationDistance(near, __result.FarSimulationDistance);
            }
        }

        // The ghost has no inventory to show. Patched at Show because ZInput.GetButtonDown is small
        // enough to be inlined into InventoryGui, which skips the MuteGameButtons prefix.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        private static class NoInventory
        {
            private static bool Prefix() => !ReplaySession.Ready;
        }

        // A health bar eases toward its value by scaled time: it stood still while paused, and slid after
        // a jump in time. After a seek or while paused, it shows the value at once.
        [HarmonyPatch(typeof(GuiBar), "LateUpdate")]
        private static class BarsFollowSeeks
        {
            private static readonly AccessTools.FieldRef<GuiBar, float> Value = AccessTools.FieldRefAccess<GuiBar, float>("m_value");
            private static readonly AccessTools.FieldRef<GuiBar, float> MaxValue = AccessTools.FieldRefAccess<GuiBar, float>("m_maxValue");
            private static readonly AccessTools.FieldRef<GuiBar, float> Smooth = AccessTools.FieldRefAccess<GuiBar, float>("m_smoothValue");
            private static readonly AccessTools.FieldRef<GuiBar, float> Delay = AccessTools.FieldRefAccess<GuiBar, float>("m_delayTimer");

            private static void Prefix(GuiBar __instance)
            {
                if (!ReplaySession.SnapBars) return;
                Delay(__instance) = 0f;
                Smooth(__instance) = Mathf.Clamp01(Value(__instance) / MaxValue(__instance));
            }
        }

        // Hiding the HUD also moves the pause menu off screen (in the normal game that makes a clean
        // paused view). In a replay the HUD starts hidden, so Escape looked like a freeze.
        [HarmonyPatch(typeof(Hud), "SetVisible")]
        private static class MenuDespiteHiddenHud
        {
            private static void Postfix()
            {
                if (ReplaySession.Active && Menu.instance) Menu.instance.m_root.localPosition = Vector3.zero;
            }
        }

        // The pause menu stops time and would be filmed: no pause menu during an export (F10 cancels it).
        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        private static class NoMenuWhileExporting
        {
            private static bool Prefix() => !Exporter.Busy;
        }

        // The radial and emote wheels, and sitting. Patched at the player's handling of them because the
        // button checks can be inlined past MuteGameButtons (see NoInventory).
        [HarmonyPatch(typeof(Player), "HandleRadialInput")]
        private static class NoWheels
        {
            private static bool Prefix(Player __instance) => !IsGhost(__instance);
        }

        // A ballista compares its last shot with the game's clock, which in a replay isn't the recording's,
        // so one that had ever fired stayed cooling down: unarmed, and never turning. The recording shows
        // the moment of a shot as a change of the value, so that's what's timed.
        [HarmonyPatch(typeof(Turret), nameof(Turret.IsCoolingDown))]
        private static class TurretCooldownInReplayTime
        {
            private static bool Prefix(Turret __instance, ZNetView ___m_nview, ref bool __result)
            {
                if (!ReplaySession.Active) return true;
                __result = ___m_nview.IsValid() && ReplaySession.TurretCooling(__instance, ___m_nview.GetZDO());
                return false;
            }
        }

        // Cut the loading screen the moment the replay is ready instead of fading it over scaled time, and
        // show it at once while loading after a far jump (paused, scaled time wouldn't fade it in).
        [HarmonyPatch(typeof(Hud), "UpdateBlackScreen")]
        private static class NoLoadingFade
        {
            private static void Postfix(Hud __instance)
            {
                if (!ReplaySession.Ready) return;
                bool show = ReplaySession.Loading && !Exporter.Busy;
                __instance.m_loadingScreen.alpha = show ? 1f : 0f;
                __instance.m_loadingScreen.gameObject.SetActive(show);
            }
        }

        // Never remove the hidden viewer: the replay's controls and clock run through it.
        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        private static class KeepTheViewer
        {
            private static void Prefix(List<ZDO> currentNearObjects)
            {
                if (!ReplaySession.Active || !Player.m_localPlayer) return;
                ZDO z = Player.m_localPlayer.GetComponent<ZNetView>()?.GetZDO();
                if (z != null) currentNearObjects.Add(z);
            }
        }

        // While loading after a far jump the viewer counts as teleporting (see ReplaySession.Loading). See ReplayOnly.
        private static class LoadLikeAPortal
        {
            private static void Postfix(Player __instance, ref bool __result)
            {
                if (ReplaySession.Loading && IsGhost(__instance)) __result = true;
            }
        }

        // Zones are built on a 0.1 s timer of scaled time, one at a time: slower in slow motion, never while
        // paused. In replays it runs on real time. While the replay first loads and after a far jump, zones
        // are built for up to 40 ms every frame: the start of a replay took over half a minute.
        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        private static class ZonesInRealTime
        {
            private static readonly AccessTools.FieldRef<ZoneSystem, float> Timer = AccessTools.FieldRefAccess<ZoneSystem, float>("m_updateTimer");
            private static readonly MethodInfo CreateLocalZones = AccessTools.Method(typeof(ZoneSystem), "CreateLocalZones");

            private static bool Rushing => !ReplaySession.Ready || ReplaySession.Loading;

            private static void Prefix(ZoneSystem __instance)
            {
                if (!ReplaySession.Active) return;
                if (Rushing) Timer(__instance) = 1f;
                else Timer(__instance) += Time.unscaledDeltaTime - Time.deltaTime;
            }

            private static void Postfix(ZoneSystem __instance)
            {
                if (!ReplaySession.Active || !Rushing || !__instance.LocationsGenerated || ZNet.instance == null || CreateLocalZones == null) return;
                var args = new object[] { ZNet.instance.GetReferencePosition() };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 40 && (bool)CreateLocalZones.Invoke(__instance, args)) { }
            }
        }

        // The game switches the controller layout on F9, which is the screenshot key in replays.
        [HarmonyPatch(typeof(KeyHints), "Update")]
        private static class NoLayoutSwitch
        {
            private static bool Prefix() => !ReplaySession.Active;
        }

        [HarmonyPatch(typeof(MonoUpdaters), "LateUpdate")]
        private static class HiFiLane
        {
            private static void Postfix()
            {
                ReplaySession.ApplyHiFi();
                ReplaySession.ApplyPose();
                ReplaySession.ApplyParts();
                ReplaySession.ApplyProjectiles();
                ReplaySession.ApplyCatapults();
                ReplaySession.ApplyParticles();
                ReplaySession.ApplyPitch();
            }
        }

        // Capes simulate just before the LateUpdate scripts, so they saw the animator's pose while the body
        // was drawn in the recorded one. The two differ most in an emote, and the cape thrashed. Positions and
        // the recorded pose go on first, ahead of the cloth; re-hooked every frame, others may reorder the list.
        internal static class BeforeCloth
        {
            private static readonly MagicaCloth2.MagicaManager.UpdateMethod Pose = () =>
            {
                ReplaySession.ApplyHiFi();
                ReplaySession.ApplyPose();
            };
            private static MagicaCloth2.MagicaManager.UpdateMethod _hooked;

            internal static void Keep()
            {
                MagicaCloth2.MagicaManager.UpdateMethod now = MagicaCloth2.MagicaManager.beforeLateUpdateDelegate;
                if (now != null && ReferenceEquals(now, _hooked)) return;
                System.Delegate[] list = now?.GetInvocationList();
                if (list == null || list.Length == 0 || !list[0].Equals(Pose))
                {
                    var rest = (MagicaCloth2.MagicaManager.UpdateMethod)System.Delegate.Remove(now, Pose);
                    MagicaCloth2.MagicaManager.beforeLateUpdateDelegate = (MagicaCloth2.MagicaManager.UpdateMethod)System.Delegate.Combine(Pose, rest);
                }
                _hooked = MagicaCloth2.MagicaManager.beforeLateUpdateDelegate;
            }
        }

        // The hotbar draws the viewer's own hidden character. In a replay it draws the recording player's
        // hotbar instead, swapped into that character only for the call.
        [HarmonyPatch(typeof(HotkeyBar), "UpdateIcons")]
        private static class RecordedHotbar
        {
            private static readonly AccessTools.FieldRef<Humanoid, Inventory> Items = AccessTools.FieldRefAccess<Humanoid, Inventory>("m_inventory");

            private static void Prefix(Player player, out Inventory __state)
            {
                __state = null;
                if (!ReplaySession.Active || !ReplaySession.Ready || !player) return;
                __state = Items(player);
                Items(player) = ReplaySession.HotbarAt();
            }

            private static void Finalizer(Player player, Inventory __state)
            {
                if (__state != null) Items(player) = __state;
            }
        }

        // The network clock comes from the recording too (see RecordedNetTime).
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.GetTimeSeconds))]
        private static class RecordedNetTime
        {
            private static void Postfix(ref double __result)
            {
                if (ReplaySession.RecordedNetTime(out double time)) __result = time;
            }
        }

        // The wave clock comes from the recording (see RecordedWaterTime). The game's own smoothing would
        // lag after a seek and doesn't follow pause or slow motion.
        [HarmonyPatch(typeof(WaterVolume), "UpdateWaterTime")]
        private static class RecordedWaves
        {
            private static bool Prefix(ref float ___s_wrappedDayTimeSeconds, ref float ___s_waterTime)
            {
                if (!ReplaySession.RecordedWaterTime(out float time)) return true;
                ___s_wrappedDayTimeSeconds = ___s_waterTime = time;
                return false;
            }
        }

        [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
        private static class ReplayCamera
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(GameCamera __instance) => FreeCam.Apply(__instance);
        }

        // ZNetScene builds and removes objects on a timer fed by scaled time, so at timeScale 0 (paused)
        // nothing new appeared until playing. Feed it real time in replays.
        [HarmonyPatch(typeof(ZNetScene), "Update")]
        private static class BuildWhilePaused
        {
            private static readonly AccessTools.FieldRef<ZNetScene, float> Timer = AccessTools.FieldRefAccess<ZNetScene, float>("m_createDestroyTimer");

            private static void Prefix(ZNetScene __instance)
            {
                if (ReplaySession.Active) Timer(__instance) += Time.unscaledDeltaTime - Time.deltaTime;
            }
        }

        [HarmonyPatch(typeof(ZSFX), nameof(ZSFX.Play))]
        private static class PitchNewSounds
        {
            private static readonly AccessTools.FieldRef<ZSFX, AudioSource> Source = AccessTools.FieldRefAccess<ZSFX, AudioSource>("m_audioSource");
            private static void Postfix(ZSFX __instance) => ReplaySession.OnSoundStarted(Source(__instance));
        }

        // The viewer's stealth value never updates (it stays 0, which reads as sneaking), so the sneak
        // icon was always on. The recording player's value comes through its ZDO.
        [HarmonyPatch(typeof(Hud), "UpdateStealth")]
        private static class StealthOfRecordedPlayer
        {
            private static bool Prefix(ref Player player)
            {
                if (!ReplaySession.Active || !ReplaySession.Ready) return true;
                Player recorded = ReplaySession.RecordedPlayer();
                if (!recorded) return false;
                player = recorded;
                return true;
            }
        }

        // "Combat HUD only": health bars and damage numbers stay visible while the rest of the HUD is hidden.
        private static bool _showDespiteHidden;

        [HarmonyPatch(typeof(Hud), nameof(Hud.IsUserHidden))]
        private static class CombatHudOnly
        {
            private static void Postfix(ref bool __result)
            {
                if (_showDespiteHidden) __result = false;
            }
        }

        [HarmonyPatch(typeof(EnemyHud), "LateUpdate")]
        private static class CombatHudBars
        {
            private static void Prefix() => _showDespiteHidden = ReplaySession.Active && ReplayUI.CombatHud;
            private static void Finalizer() => _showDespiteHidden = false;
        }

        [HarmonyPatch(typeof(DamageText), "RPC_DamageText")]
        private static class CombatHudNumbers
        {
            private static void Prefix() => _showDespiteHidden = ReplaySession.Active && ReplayUI.CombatHud;
            private static void Finalizer() => _showDespiteHidden = false;
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        private static class KeepTheView
        {
            private static void Prefix()
            {
                if (ReplaySession.Active && ReplaySession.Ready) CameraPath.SaveView(ReplaySession.T);
            }
        }

        [HarmonyPatch(typeof(FejdStartup), "Start")]
        private static class BackAtMenu
        {
            private static void Postfix(FejdStartup __instance)
            {
                ReplaySession.End();
                MenuUI.Install(__instance);
            }
        }
    }
}
