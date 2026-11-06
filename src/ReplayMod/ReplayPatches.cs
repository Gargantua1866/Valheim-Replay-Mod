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

        // A wider full-detail terrain radius in replays (see ReplaySession.FindSpawnPoint).
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.GetSyncedSimulationDistance))]
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

        // Cut the loading screen the moment the replay is ready instead of fading it over scaled time.
        [HarmonyPatch(typeof(Hud), "UpdateBlackScreen")]
        private static class NoLoadingFade
        {
            private static void Postfix(Hud __instance)
            {
                if (!ReplaySession.Ready) return;
                __instance.m_loadingScreen.alpha = 0f;
                __instance.m_loadingScreen.gameObject.SetActive(false);
            }
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
