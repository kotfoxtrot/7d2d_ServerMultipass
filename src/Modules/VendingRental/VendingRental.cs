using System;
using System.Collections.Generic;
using System.Linq;
using Platform;

namespace ServerMultipass.Modules
{
    public sealed class VendingRentalSettings
    {
        [Range(1)] public int RentPrice;
        [Range(1)] public int RentDays;
        [Range(1)] public int MaxRentalsPerPlayer;
        public bool AutoBuyEnabled;
        [Range(0.01, 96)] public double AutoBuyRunsPerDay;
        [Range(0, 100)] public int AutoBuyChancePercent;
        [Range(0)] public double AutoBuyIdleHours;
        [Range(0)] public int AutoBuyMaxMarkupPercent;
        public string[] AutoBuyExcludeItems;
    }

    public sealed partial class VendingRental : Module<VendingRentalSettings>
    {
        internal const string RemoveContractEvent = "vending_rental_remove_contract";

        private const string TraderMachineBlock = "cntVendingMachineTrader";
        private const string PlayerMachineBlock = "cntVendingMachine";
        private const string ContractItem = "vendingRentalContract";
        private const int TickSeconds = 5;
        private const int SaveSeconds = 60;

        private static VendingRental instance;

        internal readonly HashSet<int> MachineBlockTypes = new HashSet<int>();
        private readonly HashSet<int> traderIds = new HashSet<int>();
        private readonly Dictionary<Vector3i, PendingRent> pending = new Dictionary<Vector3i, PendingRent>();
        private readonly HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int playerTraderId = -1;
        private int contractType = -1;
        private int secondsToTick;
        private int secondsToSave;

        private sealed class PendingRent
        {
            public PlatformUserIdentifierAbs Owner;
            public string Name;
            public int EntityId;
            public int Paid;
        }

        public override string Name => "VendingRental";
        protected override bool HasPatches => true;
        protected internal override bool HasXml => true;

        internal static VendingRental Active => instance != null && instance.Ready ? instance : null;

        private static int Today => GameUtils.WorldTimeToDays(GameManager.Instance.World.worldTime);
        private static ulong Now => GameManager.Instance.World.worldTime;

        protected override void OnEnable()
        {
            traderIds.Clear();
            MachineBlockTypes.Clear();
            pending.Clear();
            RegisterMachine(TraderMachineBlock, false);
            playerTraderId = RegisterMachine(PlayerMachineBlock, true);
            var contract = ItemClass.GetItem(ContractItem);
            if (contract == null || contract.IsEmpty()) throw new InvalidOperationException($"item {ContractItem} not found");
            contractType = contract.type;
            ReadExcluded();
            LoadData();
            secondsToTick = TickSeconds;
            secondsToSave = SaveSeconds;
            instance = this;
            Info($"traders {string.Join(",", traderIds)}, {rentals.Count} rentals, {stored.Count} stored, contract price {ContractPrice()}");
        }

        protected override void OnDisable()
        {
            instance = null;
            pending.Clear();
            SaveData();
        }

        protected override void OnSettingsChanged()
        {
            ReadExcluded();
        }

        protected internal override void OnSecond(Tick tick)
        {
            if (--secondsToTick <= 0)
            {
                secondsToTick = TickSeconds;
                Update();
            }
            if (--secondsToSave > 0) return;
            secondsToSave = SaveSeconds;
            if (dirty) SaveData();
        }

        private void ReadExcluded()
        {
            excluded.Clear();
            foreach (var name in Settings.AutoBuyExcludeItems) excluded.Add(name);
        }

        private int RegisterMachine(string blockName, bool playerOwned)
        {
            var block = Block.GetBlockByName(blockName);
            if (block == null || !block.Properties.Values.ContainsKey("TraderID") || !int.TryParse(block.Properties.Values["TraderID"], out var traderId))
                throw new InvalidOperationException($"block {blockName} with TraderID not found");
            var info = traderId >= 0 && traderId < TraderInfo.traderInfoList.Length ? TraderInfo.traderInfoList[traderId] : null;
            if (info == null || !info.Rentable || info.PlayerOwned != playerOwned)
                throw new InvalidOperationException($"trader_info {traderId} of block {blockName} must exist with rentable=true and player_owned={(playerOwned ? "true" : "false")}");
            traderIds.Add(traderId);
            MachineBlockTypes.Add(block.blockID);
            return traderId;
        }

        private void Update()
        {
            var world = GameManager.Instance.World;
            var today = Today;
            foreach (var rental in rentals.Values.ToList())
            {
                if (world.GetChunkFromWorldPos(rental.Pos) == null)
                {
                    RenewStored(rental, today);
                    if (rental.EndDay <= today)
                    {
                        Forget(rental);
                        continue;
                    }
                    WarnLowFunds(rental, today);
                    continue;
                }
                var vm = world.GetTileEntity(rental.Pos) as TileEntityVendingMachine;
                if (!IsRental(vm))
                {
                    Forget(rental);
                    continue;
                }
                if (LockManager.Instance.IsLockedServer(vm)) continue;
                if (EnsureState(vm, true)) vm.SetModified();
                if (rentals.TryGetValue(rental.Pos, out var current)) WarnLowFunds(current, today);
            }
            for (var i = stored.Count - 1; i >= 0; i--)
            {
                var rental = stored[i];
                RenewStored(rental, today);
                if (rental.EndDay > today) continue;
                stored.RemoveAt(i);
                dirty = true;
                Info($"stored rental of {rental.Name} ended");
            }
        }

        internal void BeforeTileTick(TileEntityVendingMachine vm)
        {
            if (vm.GetOwner() == null || vm.RentalEndDay - Today > 1 || LockManager.Instance.IsLockedServer(vm)) return;
            if (IsParked(vm) && vm.RentalEndDay == 0 && vm.TraderData.PrimaryInventory.Count == 0 && vm.TraderData.AvailableMoney == 0) return;
            if (EnsureState(vm, false)) vm.SetModified();
        }

        internal bool IsRental(TileEntity tileEntity)
        {
            return tileEntity is TileEntityVendingMachine vm && vm.TraderData != null && traderIds.Contains(vm.TraderData.TraderID);
        }

        internal bool IsPlaced(TileEntity tileEntity)
        {
            return tileEntity is TileEntityVendingMachine vm && vm.TraderData != null && vm.TraderData.TraderID == playerTraderId;
        }

        private bool IsParked(TileEntityVendingMachine vm)
        {
            return IsPlaced(vm) && vm.GetOwner() != null && vm.RentalEndDay <= Today && !rentals.ContainsKey(vm.ToWorldPos());
        }

        private bool IsFree(TileEntityVendingMachine vm)
        {
            return vm.GetOwner() == null || IsParked(vm);
        }

        private bool IsContract(TraderData.Entry entry)
        {
            return entry != null && entry.Item != null && entry.Item.itemValue.type == contractType;
        }

        private static int ContractPrice()
        {
            return BuyPrice(ItemClass.GetItem(ContractItem), 1, 0, null);
        }

        internal bool EnsureState(TileEntityVendingMachine vm, bool autoBuy)
        {
            var data = vm.TraderData;
            var changed = false;
            var owner = vm.GetOwner();
            var placed = IsPlaced(vm);
            if (owner != null && !IsParked(vm))
            {
                var rental = Track(vm, owner, ref changed);
                if (autoBuy && AutoBuy(vm, rental)) changed = true;
                if (RenewFromTill(vm, rental)) changed = true;
                rental.Till = data.AvailableMoney;
                if (vm.RentalEndDay > Today)
                {
                    if (data.PrimaryInventory.RemoveAll(IsContract) > 0) changed = true;
                    if (vm.nextAutoBuy != ulong.MaxValue)
                    {
                        vm.nextAutoBuy = ulong.MaxValue;
                        changed = true;
                    }
                    return changed;
                }
                if (!placed) vm.ClearVendingMachine();
                changed = true;
            }
            if (placed) return Park(vm) || changed;
            if (!HoldsOnlyContract(data))
            {
                data.PrimaryInventory.Clear();
                data.PrimaryInventory.Add(NewContract());
                changed = true;
            }
            if (data.AvailableMoney != 0)
            {
                data.AvailableMoney = 0;
                changed = true;
            }
            if (rentals.TryGetValue(vm.ToWorldPos(), out var stale)) Forget(stale);
            return changed;
        }

        private bool Park(TileEntityVendingMachine vm)
        {
            var pos = vm.ToWorldPos();
            var data = vm.TraderData;
            var changed = false;
            if (rentals.TryGetValue(pos, out var rental))
            {
                RemoveRental(pos);
                Info($"rental of {rental.Name} at {pos} ended");
            }
            if (vm.RentalEndDay > 0)
            {
                vm.rentalEndDay = 0;
                changed = true;
            }
            if (data.PrimaryInventory.Count > 0)
            {
                data.PrimaryInventory.Clear();
                changed = true;
            }
            if (data.AvailableMoney != 0)
            {
                data.AvailableMoney = 0;
                changed = true;
            }
            if (vm.nextAutoBuy != ulong.MaxValue)
            {
                vm.nextAutoBuy = ulong.MaxValue;
                changed = true;
            }
            return changed;
        }

        private TraderData.Entry NewContract()
        {
            return new TraderData.Entry(new ItemStack(ItemClass.GetItem(ContractItem), 1), 0, false);
        }

        private bool HoldsOnlyContract(TraderData data)
        {
            return data.PrimaryInventory.Count == 1
                   && IsContract(data.PrimaryInventory[0])
                   && data.PrimaryInventory[0].Item.count == 1
                   && data.PrimaryInventory[0].Markup == 0;
        }

        private VendingRent Track(TileEntityVendingMachine vm, PlatformUserIdentifierAbs owner, ref bool changed)
        {
            var pos = vm.ToWorldPos();
            var ownerId = owner.CombinedString;
            var data = vm.TraderData;
            if (!rentals.TryGetValue(pos, out var rental) || rental.Owner != ownerId)
            {
                rental = new VendingRent
                {
                    Pos = pos,
                    Owner = ownerId,
                    Name = NameOf(owner),
                    EndDay = vm.RentalEndDay,
                    Till = data.AvailableMoney,
                    LastPurchase = Now,
                    NextAutoBuy = FirstAutoBuy(),
                    Player = IsPlaced(vm)
                };
                rentals[pos] = rental;
                dirty = true;
                ProtectRegion(owner, pos, true);
                return rental;
            }
            if (rental.Charged > 0)
            {
                data.AvailableMoney = Math.Max(0, data.AvailableMoney - rental.Charged);
                rental.Charged = 0;
                changed = true;
            }
            if (rental.EndDay > vm.RentalEndDay)
            {
                vm.rentalEndDay = rental.EndDay;
                changed = true;
            }
            rental.EndDay = vm.RentalEndDay;
            rental.Till = data.AvailableMoney;
            rental.Player = IsPlaced(vm);
            dirty = true;
            return rental;
        }

        private bool RenewFromTill(TileEntityVendingMachine vm, VendingRent rental)
        {
            var data = vm.TraderData;
            var price = ContractPrice();
            var today = Today;
            var periods = 0;
            while (vm.RentalEndDay - today <= 1 && data.AvailableMoney >= price && periods < 1000)
            {
                data.AvailableMoney -= price;
                vm.rentalEndDay += Settings.RentDays;
                periods++;
            }
            if (periods == 0) return false;
            rental.EndDay = vm.RentalEndDay;
            rental.Till = data.AvailableMoney;
            dirty = true;
            Renewed(rental, periods * price);
            return true;
        }

        private void RenewStored(VendingRent rental, int today)
        {
            var price = ContractPrice();
            var periods = 0;
            while (rental.EndDay - today <= 1 && rental.Till >= price && periods < 1000)
            {
                rental.Till -= price;
                rental.Charged += price;
                rental.EndDay += Settings.RentDays;
                periods++;
            }
            if (periods == 0) return;
            dirty = true;
            Renewed(rental, periods * price);
        }

        private void Renewed(VendingRent rental, int paid)
        {
            Info($"rental of {rental.Name} at {rental.Pos} renewed until day {rental.EndDay}, {paid} taken from the till");
        }

        private void WarnLowFunds(VendingRent rental, int today)
        {
            var price = ContractPrice();
            if (rental.EndDay - today > 1 || rental.Till >= price || rental.WarnedDay == rental.EndDay) return;
            var client = ClientOf(rental.Owner);
            if (client == null) return;
            rental.WarnedDay = rental.EndDay;
            dirty = true;
            Reply(client, "RenewSoon", FormatPos(rental.Pos), price - rental.Till, rental.EndDay);
        }

        private void Forget(VendingRent rental)
        {
            RemoveRental(rental.Pos);
            if (!rental.Player) ProtectRegion(PlatformUserIdentifierAbs.FromCombinedString(rental.Owner, false), rental.Pos, false);
        }

        internal List<TraderData.Entry> OnLocked(TileEntityVendingMachine vm, int playerId)
        {
            var owner = vm.GetOwner();
            var placed = IsPlaced(vm);
            if (owner != null && !IsParked(vm)) return null;
            var client = ConnectionManager.Instance.Clients.ForEntityId(playerId);
            if (client == null || client.InternalId == null) return null;
            if (placed)
            {
                Resync(vm, client, byte.MaxValue);
                if (owner == null || !owner.Equals(client.InternalId)) return null;
            }
            var count = CountActive(client.InternalId.CombinedString, Today);
            if (count < Settings.MaxRentalsPerPlayer)
            {
                if (placed) vm.TraderData.PrimaryInventory.Add(NewContract());
                return null;
            }
            Reply(client, "LimitReached", count, Settings.MaxRentalsPerPlayer);
            if (placed) return null;
            var hidden = vm.TraderData.PrimaryInventory.FindAll(IsContract);
            vm.TraderData.PrimaryInventory.RemoveAll(IsContract);
            return hidden;
        }

        private void AddPending(Vector3i pos, ClientInfo client, int paid)
        {
            pending[pos] = new PendingRent { Owner = client.InternalId, Name = client.playerName, EntityId = client.entityId, Paid = paid };
        }

        private void Remember(TileEntityVendingMachine vm, bool purchase)
        {
            if (!rentals.TryGetValue(vm.ToWorldPos(), out var rental)) return;
            rental.Till = vm.TraderData.AvailableMoney;
            if (purchase) rental.LastPurchase = Now;
            dirty = true;
        }

        internal void OnUnlocked(TileEntityVendingMachine vm, int playerId)
        {
            var pos = vm.ToWorldPos();
            if (GameManager.Instance.World.GetTileEntity(pos) != vm)
            {
                pending.Remove(pos);
                return;
            }
            if (pending.TryGetValue(pos, out var rent))
            {
                pending.Remove(pos);
                if (rent.Owner != null && IsFree(vm)) Register(vm, rent);
            }
            if (!IsPlaced(vm)) return;
            if (EnsureState(vm, false)) vm.SetModified();
            Resync(vm, ConnectionManager.Instance.Clients.ForEntityId(playerId), byte.MaxValue);
        }

        private void Register(TileEntityVendingMachine vm, PendingRent rent)
        {
            var pos = vm.ToWorldPos();
            var data = vm.TraderData;
            data.AvailableMoney = Math.Max(0, data.AvailableMoney - rent.Paid);
            var client = ConnectionManager.Instance.Clients.ForEntityId(rent.EntityId);
            if (GameManager.Instance.World.GetEntity(rent.EntityId) is EntityPlayer player)
                GameEventManager.Current.HandleAction(RemoveContractEvent, player, player, false);

            var notPlacer = IsPlaced(vm) && !rent.Owner.Equals(vm.GetOwner());
            var count = CountActive(rent.Owner.CombinedString, Today);
            if (notPlacer || count >= Settings.MaxRentalsPerPlayer)
            {
                EnsureState(vm, false);
                vm.SetModified();
                Refund(client, pos, rent.Paid);
                if (!notPlacer) Reply(client, "LimitReached", count, Settings.MaxRentalsPerPlayer);
                Info($"{rent.Name} may not rent {pos}, {rent.Paid} refunded");
                return;
            }

            data.PrimaryInventory.Clear();
            vm.ownerID = rent.Owner;
            vm.allowedUserIds.Clear();
            vm.passwordHash = "";
            vm.rentalEndDay = Today + Settings.RentDays;
            vm.nextAutoBuy = ulong.MaxValue;
            vm.SetModified();

            rentals[pos] = new VendingRent
            {
                Pos = pos,
                Owner = rent.Owner.CombinedString,
                Name = rent.Name,
                EndDay = vm.RentalEndDay,
                Till = data.AvailableMoney,
                LastPurchase = Now,
                NextAutoBuy = FirstAutoBuy(),
                Player = IsPlaced(vm)
            };
            dirty = true;
            ProtectRegion(rent.Owner, pos, true);
            Reply(client, "Rented", CountActive(rent.Owner.CombinedString, Today), Settings.MaxRentalsPerPlayer);
            Info($"{rent.Name} rented {pos} until day {vm.RentalEndDay}");
        }

        private static void Refund(ClientInfo client, Vector3i pos, int amount)
        {
            var money = ItemClass.GetItem(TraderInfo.CurrencyItem);
            var itemClass = money?.ItemClass;
            if (itemClass == null) return;
            var stackSize = Math.Max(1, itemClass.MaxCount);
            while (amount > 0)
            {
                var count = Math.Min(stackSize, amount);
                amount -= count;
                var stack = new ItemStack(money.Clone(), count);
                if (!Players.GiveItem(client, stack))
                    GameManager.Instance.ItemDropServer(stack, pos.ToVector3Center() + UnityEngine.Vector3.up, UnityEngine.Vector3.zero);
            }
        }

        internal void OnCleared(TileEntityVendingMachine vm, PlatformUserIdentifierAbs previousOwner)
        {
            var pos = vm.ToWorldPos();
            if (rentals.TryGetValue(pos, out var rental))
            {
                RemoveRental(pos);
                Info($"rental of {rental.Name} at {pos} ended");
            }
            if (!IsPlaced(vm)) ProtectRegion(previousOwner, pos, false);
        }

        internal void OnPlaced(TileEntityVendingMachine vm, PlatformUserIdentifierAbs placer)
        {
            var pos = vm.ToWorldPos();
            pending.Remove(pos);
            ProtectRegion(placer, pos, true);
            var rental = TakeStored(placer.CombinedString);
            if (rental == null)
            {
                Info($"{NameOf(placer)} placed a machine at {pos}");
                return;
            }
            var data = vm.TraderData;
            data.PrimaryInventory.Clear();
            data.AvailableMoney = rental.Till;
            vm.ownerID = placer;
            vm.allowedUserIds.Clear();
            vm.passwordHash = "";
            vm.rentalEndDay = rental.EndDay;
            vm.nextAutoBuy = ulong.MaxValue;
            rental.Pos = pos;
            rental.Charged = 0;
            rental.WarnedDay = 0;
            rental.LastPurchase = Now;
            rental.NextAutoBuy = FirstAutoBuy();
            rental.Player = true;
            rentals[pos] = rental;
            dirty = true;
            vm.SetModified();
            Info($"stored rental of {rental.Name} moved to {pos} until day {rental.EndDay}");
            SaveData();
        }

        internal void OnRemoved(TileEntityVendingMachine vm)
        {
            var pos = vm.ToWorldPos();
            pending.Remove(pos);
            if (!rentals.TryGetValue(pos, out var rental)) return;
            var owner = vm.GetOwner();
            var endDay = Math.Max(rental.EndDay, vm.RentalEndDay);
            if (IsPlaced(vm) && owner != null && owner.CombinedString == rental.Owner && endDay > Today)
            {
                rentals.Remove(pos);
                rental.EndDay = endDay;
                rental.Till = Math.Max(0, vm.TraderData.AvailableMoney - rental.Charged);
                rental.Charged = 0;
                rental.Player = true;
                stored.Add(rental);
                Info($"machine of {rental.Name} removed from {pos}, rental until day {rental.EndDay} stored with {rental.Till} in the till");
            }
            else
            {
                Forget(rental);
                Info($"rented machine of {rental.Name} removed from {pos}, rental ended");
            }
            dirty = true;
            SaveData();
        }

        internal bool IsLockHolder(TileEntityVendingMachine vm, int entityId)
        {
            return LockManager.Instance.singleLocks.TryGetByValue(new LockManager.LockEntry(vm, 0), out var holder) && holder == entityId;
        }

        internal void Resync(TileEntityVendingMachine vm, ClientInfo client, byte handle)
        {
            if (client == null) return;
            var owner = vm.ownerID;
            if (IsParked(vm) && IsLockHolder(vm, client.entityId)) vm.ownerID = null;
            try
            {
                client.SendPackage(NetPackageManager.GetPackage<NetPackageTileEntity>().Setup(vm, StreamModeWrite.ToClient, handle));
            }
            finally
            {
                vm.ownerID = owner;
            }
        }

        private static string NameOf(PlatformUserIdentifierAbs owner)
        {
            var data = owner == null ? null : GameManager.Instance.persistentPlayers?.GetPlayerData(owner);
            var name = data?.PlayerName?.DisplayName;
            return string.IsNullOrEmpty(name) ? "?" : name;
        }

        private static string FormatPos(Vector3i pos)
        {
            var z = pos.z >= 0 ? pos.z + " N" : -pos.z + " S";
            var x = pos.x >= 0 ? pos.x + " E" : -pos.x + " W";
            return z + ", " + x;
        }

        private static ClientInfo ClientOf(string ownerId)
        {
            var owner = PlatformUserIdentifierAbs.FromCombinedString(ownerId, false);
            return owner == null ? null : ConnectionManager.Instance.Clients.ForUserId(owner);
        }

        private VendingRent TakeStored(string ownerId)
        {
            var today = Today;
            var best = -1;
            for (var i = 0; i < stored.Count; i++)
            {
                var rental = stored[i];
                if (rental.Owner == ownerId && rental.EndDay > today && (best < 0 || rental.EndDay > stored[best].EndDay)) best = i;
            }
            if (best < 0) return null;
            var taken = stored[best];
            stored.RemoveAt(best);
            dirty = true;
            return taken;
        }

        private static void ProtectRegion(PlatformUserIdentifierAbs owner, Vector3i pos, bool add)
        {
            var data = owner == null ? null : GameManager.Instance.persistentPlayers?.GetPlayerData(owner);
            if (data == null) return;
            if (add) data.AddVendingMachinePosition(pos);
            else data.TryRemoveVendingMachinePosition(pos);
        }
    }
}
