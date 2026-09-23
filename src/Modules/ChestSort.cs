using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ServerMultipass.Modules
{
    public sealed class ChestSortSettings
    {
        public string ChestName;
        [Range(0, 256)] public int MergeDistance;
    }

    public sealed class ChestSort : Module<ChestSortSettings>
    {
        private static ChestSort instance;

        public override string Name => "ChestSort";
        protected override bool HasPatches => true;

        internal static ChestSort Active => instance != null && instance.Ready ? instance : null;

        protected override void OnEnable()
        {
            instance = this;
        }

        protected override void OnDisable()
        {
            instance = null;
        }

        internal void OnClosed(TEFeatureStorage source, int entityId)
        {
            if (!source.bPlayerStorage || !IsSortBox(source)) return;
            var moved = Sort(source);
            if (moved > 0) Reply(Players.Client(entityId), "Sorted", moved);
        }

        private bool IsSortBox(TEFeatureStorage storage)
        {
            var text = storage.Parent?.GetSelfOrFeature<TEFeatureSignable>()?.GetAuthoredText()?.Text;
            return text != null && text.Trim().Equals((Settings.ChestName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private int Sort(TEFeatureStorage source)
        {
            var world = GameManager.Instance.World;
            var claims = GameManager.Instance.persistentPlayers?.m_lpBlockMap;
            if (world == null || claims == null) return 0;
            var claimSize = GameStats.GetInt(EnumGameStats.LandClaimSize);
            var half = (claimSize - 1) / 2;
            var region = Region(source.ToWorldPos(), claims, claimSize, half);
            if (region.Count == 0) return 0;
            var targets = Targets(world, source, region, half);
            if (targets.Count == 0) return 0;
            var targetTypes = targets.Select(Types).ToList();
            var sorted = new HashSet<int>();
            var items = source.items;
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].IsEmpty() || IsLocked(source, i)) continue;
                var type = items[i].itemValue.type;
                var stack = items[i].Clone();
                var before = stack.count;
                for (var t = 0; t < targets.Count && stack.count > 0; t++)
                {
                    if (!targetTypes[t].Contains(type)) continue;
                    targets[t].TryStackItem(0, stack);
                    if (stack.count > 0 && AddToEmptySlot(targets[t], stack)) stack.count = 0;
                }
                if (stack.count == before) continue;
                source.UpdateSlot(i, stack.count > 0 ? stack : ItemStack.Empty);
                sorted.Add(type);
            }
            if (sorted.Count > 0) source.SetModified();
            return sorted.Count;
        }

        private List<Vector3i> Region(Vector3i source, Dictionary<Vector3i, PersistentPlayerData> claims, int claimSize, int half)
        {
            PersistentPlayerData owner = null;
            var region = new List<Vector3i>();
            foreach (var claim in claims)
            {
                if (Math.Abs(source.x - claim.Key.x) > half || Math.Abs(source.z - claim.Key.z) > half) continue;
                owner ??= claim.Value;
                if (claim.Value == owner) region.Add(claim.Key);
            }
            if (owner == null) return region;
            var threshold = claimSize + Settings.MergeDistance;
            var own = claims.Where(c => c.Value == owner).Select(c => c.Key).ToList();
            var queue = new Queue<Vector3i>(region);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var keystone in own)
                {
                    if (region.Contains(keystone)) continue;
                    if (Math.Abs(current.x - keystone.x) > threshold || Math.Abs(current.z - keystone.z) > threshold) continue;
                    region.Add(keystone);
                    queue.Enqueue(keystone);
                }
            }
            return region;
        }

        private List<TEFeatureStorage> Targets(World world, TEFeatureStorage source, List<Vector3i> region, int half)
        {
            var minX = region.Min(k => k.x) - half;
            var maxX = region.Max(k => k.x) + half;
            var minZ = region.Min(k => k.z) - half;
            var maxZ = region.Max(k => k.z) + half;
            var result = new List<TEFeatureStorage>();
            for (var cx = World.toChunkXZ(minX); cx <= World.toChunkXZ(maxX); cx++)
            for (var cz = World.toChunkXZ(minZ); cz <= World.toChunkXZ(maxZ); cz++)
            {
                if (!(world.GetChunkSync(cx, cz) is Chunk chunk)) continue;
                foreach (var tileEntity in chunk.GetTileEntities().list)
                {
                    var storage = tileEntity.GetSelfOrFeature<TEFeatureStorage>();
                    if (storage == null || storage == source || !storage.bPlayerStorage) continue;
                    if (!InRegion(region, half, tileEntity.ToWorldPos())) continue;
                    if (LockManager.Instance.IsLockedServer(storage) || IsSortBox(storage)) continue;
                    result.Add(storage);
                }
            }
            return result;
        }

        private static HashSet<int> Types(TEFeatureStorage storage)
        {
            var types = new HashSet<int>();
            foreach (var item in storage.items)
                if (!item.IsEmpty())
                    types.Add(item.itemValue.type);
            return types;
        }

        private static bool AddToEmptySlot(TEFeatureStorage storage, ItemStack stack)
        {
            var items = storage.items;
            for (var i = 0; i < items.Length; i++)
            {
                if (!items[i].IsEmpty() || IsLocked(storage, i)) continue;
                storage.UpdateSlot(i, stack);
                storage.SetModified();
                return true;
            }
            return false;
        }

        private static bool IsLocked(TEFeatureStorage storage, int slot)
        {
            var locks = storage.SlotLocks;
            return locks != null && slot < locks.Length && locks[slot];
        }

        private static bool InRegion(List<Vector3i> region, int half, Vector3i position)
        {
            foreach (var keystone in region)
                if (Math.Abs(position.x - keystone.x) <= half && Math.Abs(position.z - keystone.z) <= half)
                    return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(TEFeatureStorage), nameof(TEFeatureStorage.OnUnlockedServer))]
    [HarmonyPatchCategory(nameof(ChestSort))]
    internal static class ChestSortClosePatch
    {
        private static void Postfix(TEFeatureStorage __instance, int _unlockingPlayerId)
        {
            var module = ChestSort.Active;
            if (module == null) return;
            try
            {
                module.OnClosed(__instance, _unlockingPlayerId);
            }
            catch (Exception e)
            {
                module.Fault(e, "sorting");
            }
        }
    }
}
