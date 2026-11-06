using System.Collections.Generic;

namespace ReplayMod
{
    internal static class Rpcs
    {
        // Broadcast RPCs whose handlers only change what is drawn or heard. Anything that spawns,
        // destroys, damages or moves objects is left out: the ZDO stream already carries those results.
        public const string DefaultWhitelist =
            "SetTrigger,RPC_DamageText,RPC_HealthChanged,FlashShield,RPC_CreateFragments,RPC_Shake," +
            "RPC_UpdateEffects,RPC_FreezeFrame,RPC_SetPose,RPC_ResetCloth,RPC_AnimateLever,RPC_AnimateLeverReturn," +
            "SetVisualItem,RPC_SetVisualItem,RPC_UpdateVisual,RPC_SetSlotVisual,RPC_UpdateMaterial," +
            "RPC_SetPicked,RPC_OnEat,RPC_Wakeup,RPC_Sleep,RPC_OnStateChanged,SetSaddle," +
            // Footsteps are detected only on the owner and sent to everyone as "Step"; puppets never make their own.
            "Step,RPC_OnHit," +
            // Catapult arm and legs, ballista aim, shield hits. Their handlers only spawn things on the owner.
            "RPC_Shoot,RPC_OnLegUse,RPC_SetTarget,RPC_HitNow";

        // Every name the game registers, so recorded hashes can be reported by name.
        private static readonly string[] KnownNames =
        {
            "AddSaddle", "Alert", "Backward", "ChatMessage", "Controls", "DestroyZDO", "FlashShield", "Forward", "Hide", "Hit",
            "OnDeath", "OnNearProjectileHit", "OnTargeted", "Pick", "Pickup", "RPC_AddAdrenaline", "RPC_AddAmmo", "RPC_AddFuel",
            "RPC_AddFuelAmount", "RPC_AddItem", "RPC_AddNoise", "RPC_AddOre", "RPC_AddStatusEffect", "RPC_AnimateLever",
            "RPC_AnimateLeverReturn", "RPC_ApplyOperation", "RPC_Attach", "RPC_Attack", "RPC_BossSpawnInitiated",
            "RPC_ClearCachedSupport", "RPC_CreateFragments", "RPC_Damage", "RPC_DamageText", "RPC_DestroyAttachment",
            "RPC_DiscoverClosestLocation", "RPC_DiscoverLocationResponse", "RPC_Discovered", "RPC_Drain", "RPC_DropArrows",
            "RPC_DropItem", "RPC_DropItemByName", "RPC_EatConfirmation", "RPC_EmptyProcessed", "RPC_Extract", "RPC_FreezeFrame",
            "RPC_Grow", "RPC_Heal", "RPC_HealthChanged", "RPC_HitNow", "RPC_HitWhileDodging", "RPC_IncinerateRespons", "RPC_Left",
            "RPC_MakePiece", "RPC_Nibble", "RPC_OnEat", "RPC_OnHit", "RPC_OnLegUse", "RPC_OnStateChanged", "RPC_OpenResponse",
            "RPC_Pick", "RPC_PlayMusic", "RPC_ProjectileHit", "RPC_RegisterKill", "RPC_Remove", "RPC_RemoveDoneItem", "RPC_Repair",
            "RPC_RequestDenied", "RPC_RequestIncinerate", "RPC_RequestOpen", "RPC_RequestOwn", "RPC_RequestStack",
            "RPC_RequestStateChange", "RPC_RequestTakeAll", "RPC_ResetCloth", "RPC_SetAreaHealth", "RPC_SetConnected",
            "RPC_SetConnection", "RPC_SetDreamCinematic", "RPC_SetFuel", "RPC_SetFuelAmount", "RPC_SetLoadedVisual",
            "RPC_SetPicked", "RPC_SetPose", "RPC_SetSlotVisual", "RPC_SetSnow", "RPC_SetStayTTL", "RPC_SetTag", "RPC_SetTamed",
            "RPC_SetTarget", "RPC_SetVisualItem", "RPC_Shake", "RPC_Shoot", "RPC_Sleep", "RPC_SpawnBoss", "RPC_StackResponse",
            "RPC_Stagger", "RPC_StateChanged", "RPC_TakeAllResponse", "RPC_Tap", "RPC_TeleportPlayer", "RPC_TeleportTo",
            "RPC_ToggleOn", "RPC_TryEat", "RPC_UnSummon", "RPC_UpdateEffects", "RPC_UpdateMaterial", "RPC_UpdateVisual",
            "RPC_Wakeup", "ReleaseControl", "RemoveGlobalKey", "RemoveSaddle", "RequestControl", "RequestPickup", "RequestRespons",
            "Rudder", "Say", "SetAggravated", "SetEvent", "SetGlobalKey", "SetName", "SetOwner", "SetPlayed", "SetSaddle",
            "SetTrigger", "SetVisualItem", "ShowMessage", "SleepStart", "SleepStop", "SpawnObject", "Step", "Stop",
            "ToggleEnabled", "TogglePermitted", "Trigger", "UseDoor", "UseStamina",
        };

        private static Dictionary<int, string> _names;

        public static bool IsVanilla(int hash)
        {
            Name(hash); // builds the table
            return _names.ContainsKey(hash);
        }

        public static string Name(int hash)
        {
            if (_names == null)
            {
                _names = new Dictionary<int, string>();
                foreach (string n in KnownNames) _names[n.GetStableHashCode()] = n;
            }
            return _names.TryGetValue(hash, out string s) ? s : "#" + hash;
        }

        public static HashSet<int> ParseWhitelist(string list)
        {
            var set = new HashSet<int>();
            foreach (string n in list.Split(','))
            {
                string name = n.Trim();
                if (name.Length > 0) set.Add(name.GetStableHashCode());
            }
            return set;
        }
    }
}
