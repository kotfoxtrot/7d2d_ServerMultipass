using System;
using HarmonyLib;

namespace ServerMultipass.Modules
{
    public sealed class AutolockSettings
    {
        public bool LockStorage;
        public bool LockDoors;
        public bool LockVehicles;
    }

    public sealed class Autolock : Module<AutolockSettings>
    {
        private static Autolock instance;

        public override string Name => "Autolock";
        protected override bool HasPatches => true;
        protected override bool HasTexts => false;

        internal static Autolock Active => instance != null && instance.Ready ? instance : null;

        protected override void OnEnable()
        {
            instance = this;
        }

        protected override void OnDisable()
        {
            instance = null;
        }

        internal bool Covers(TileEntityComposite tileEntity)
        {
            return (Settings.LockStorage && tileEntity.GetFeature<TEFeatureStorage>() != null)
                   || (Settings.LockDoors && tileEntity.GetFeature<TEFeatureDoor>() != null);
        }
    }

    [HarmonyPatch(typeof(TileEntityComposite), nameof(TileEntityComposite.OnBlockAdded))]
    [HarmonyPatchCategory(nameof(Autolock))]
    internal static class AutolockBlockPatch
    {
        private static void Postfix(TileEntityComposite __instance, PlatformUserIdentifierAbs _owner)
        {
            var module = Autolock.Active;
            if (module == null || _owner == null || !ConnectionManager.Instance.IsServer) return;
            try
            {
                var lockable = __instance.GetFeature<TEFeatureLockable>();
                if (lockable == null || lockable.IsLocked() || !module.Covers(__instance)) return;
                lockable.SetLocked(true);
            }
            catch (Exception e)
            {
                module.Fault(e, "block lock");
            }
        }
    }

    [HarmonyPatch(typeof(EntityVehicle), nameof(EntityVehicle.SetOwner))]
    [HarmonyPatchCategory(nameof(Autolock))]
    internal static class AutolockVehiclePatch
    {
        private static void Postfix(EntityVehicle __instance)
        {
            var module = Autolock.Active;
            if (module == null || !module.Settings.LockVehicles) return;
            try
            {
                if (__instance.GetOwner() == null || __instance.IsLocked()) return;
                __instance.SetLocked(true);
            }
            catch (Exception e)
            {
                module.Fault(e, "vehicle lock");
            }
        }
    }
}
