using System;
using System.Collections.Generic;
using SandboxOptions;
using UnityEngine;

namespace ServerMultipass.Modules
{
    public sealed partial class VendingRental
    {
        private const int MaxAutoBuyRuns = 100000;

        private static readonly System.Random random = new System.Random();

        private ulong AutoBuyPeriod => (ulong)Math.Max(1.0, Math.Round(24000.0 / Settings.AutoBuyRunsPerDay));

        internal void OnTraderData(TileEntityVendingMachine vm, TraderData incoming, ClientInfo sender)
        {
            try
            {
                if (sender == null || !IsLockHolder(vm, sender.entityId))
                {
                    Resync(vm, sender, byte.MaxValue);
                    return;
                }
                var free = IsFree(vm);
                if (!free && vm.GetOwner().Equals(sender.InternalId)) ApplyOwner(vm, incoming, sender);
                else ApplyPurchase(vm, incoming, sender, free);
            }
            catch (Exception e)
            {
                Fault(e, "trade check");
                Resync(vm, sender, byte.MaxValue);
            }
        }

        private void ApplyOwner(TileEntityVendingMachine vm, TraderData incoming, ClientInfo sender)
        {
            var data = vm.TraderData;
            var stripped = false;
            data.PrimaryInventory.Clear();
            foreach (var entry in incoming.PrimaryInventory)
            {
                if (entry == null || entry.Item == null || entry.Item.IsEmpty()) continue;
                if (IsContract(entry))
                {
                    stripped = true;
                    continue;
                }
                data.PrimaryInventory.Add(entry.Clone());
            }
            data.AvailableMoney = Math.Max(0, incoming.AvailableMoney);
            vm.SetChunkModified();
            vm.NotifyListeners();
            Remember(vm, false);
            if (stripped) Resync(vm, sender, byte.MaxValue);
        }

        private void ApplyPurchase(TileEntityVendingMachine vm, TraderData incoming, ClientInfo sender, bool free)
        {
            var data = vm.TraderData;
            var current = data.PrimaryInventory;
            var offered = incoming.PrimaryInventory;
            var result = new List<TraderData.Entry>(current.Count);
            var buyer = GameManager.Instance.World.GetEntity(sender.entityId) as EntityPlayer;
            long expected = 0;
            var changes = 0;
            var contractPaid = 0;
            var onlyContracts = true;
            var j = 0;
            foreach (var entry in current)
            {
                var left = 0;
                if (j < offered.Count && Same(entry, offered[j]))
                {
                    left = offered[j].Item.count;
                    j++;
                }
                if (left < 0 || left > entry.Item.count)
                {
                    Reject(vm, sender, "item count increased");
                    return;
                }
                var taken = entry.Item.count - left;
                if (taken > 0)
                {
                    var price = BuyPrice(entry.Item.itemValue, taken, entry.Markup, buyer);
                    expected += price;
                    changes++;
                    if (IsContract(entry)) contractPaid += price;
                    else onlyContracts = false;
                }
                if (left <= 0) continue;
                var kept = entry.Clone();
                kept.Item.count = left;
                result.Add(kept);
            }
            if (j != offered.Count)
            {
                Reject(vm, sender, "items added");
                return;
            }

            var paid = (long)incoming.AvailableMoney - data.AvailableMoney;
            if (changes == 0)
            {
                if (paid != 0) Reject(vm, sender, "money changed without a purchase");
                return;
            }
            if (paid == 0 && onlyContracts)
            {
                Resync(vm, sender, byte.MaxValue);
                return;
            }
            if (paid < 0 || paid + changes < expected)
            {
                Reject(vm, sender, $"paid {paid}, expected {expected}");
                return;
            }

            data.PrimaryInventory.Clear();
            data.PrimaryInventory.AddRange(result);
            data.AvailableMoney = incoming.AvailableMoney;
            vm.SetChunkModified();
            vm.NotifyListeners();

            var pos = vm.ToWorldPos();
            if (!free) Remember(vm, true);
            else if (contractPaid > 0) AddPending(pos, sender, onlyContracts ? (int)paid : contractPaid);
            Info($"{sender.playerName} bought {changes} entries for {paid} at {pos}");
        }

        private static bool Same(TraderData.Entry current, TraderData.Entry incoming)
        {
            if (incoming == null || incoming.Item == null || current.Markup != incoming.Markup) return false;
            var a = current.Item.itemValue;
            var b = incoming.Item.itemValue;
            return a.type == b.type && a.Quality == b.Quality && a.UseTimes == b.UseTimes && a.Meta == b.Meta;
        }

        private void Reject(TileEntityVendingMachine vm, ClientInfo sender, string reason)
        {
            Warn($"trade from {sender.playerName} at {vm.ToWorldPos()} rejected: {reason}");
            Resync(vm, sender, byte.MaxValue);
        }

        private ulong FirstAutoBuy()
        {
            return Now + AutoBuyPeriod;
        }

        private bool AutoBuy(TileEntityVendingMachine vm, VendingRent rental)
        {
            if (!Settings.AutoBuyEnabled) return false;
            var now = Now;
            var period = AutoBuyPeriod;
            if (rental.LastPurchase > now)
            {
                rental.LastPurchase = now;
                dirty = true;
            }
            if (rental.NextAutoBuy == 0 || rental.NextAutoBuy > now + period)
            {
                rental.NextAutoBuy = now + period;
                dirty = true;
                return false;
            }
            if (rental.NextAutoBuy > now) return false;

            var first = rental.NextAutoBuy;
            var due = (now - first) / period + 1;
            rental.NextAutoBuy = first + due * period;
            dirty = true;

            var idleEnd = rental.LastPurchase + (ulong)(Settings.AutoBuyIdleHours * 1000.0);
            var skipped = idleEnd <= first ? 0 : (idleEnd - first + period - 1) / period;
            if (skipped >= due) return false;

            var inventory = vm.TraderData.PrimaryInventory;
            var candidates = new List<int>();
            for (var i = 0; i < inventory.Count; i++)
                if (Eligible(inventory[i]))
                    candidates.Add(i);
            if (candidates.Count == 0) return false;

            var runs = (int)Math.Min(due - skipped, (ulong)MaxAutoBuyRuns);
            var sales = 0;
            for (var i = 0; i < runs && sales < candidates.Count; i++)
                if (random.Next(100) < Settings.AutoBuyChancePercent)
                    sales++;
            if (sales == 0) return false;

            for (var i = 0; i < sales; i++)
            {
                var k = random.Next(i, candidates.Count);
                var picked = candidates[k];
                candidates[k] = candidates[i];
                candidates[i] = picked;
            }
            candidates.RemoveRange(sales, candidates.Count - sales);
            candidates.Sort();
            for (var i = candidates.Count - 1; i >= 0; i--) Sell(vm, candidates[i]);
            return true;
        }

        private void Sell(TileEntityVendingMachine vm, int index)
        {
            var data = vm.TraderData;
            var entry = data.PrimaryInventory[index];
            var price = BuyPrice(entry.Item.itemValue, entry.Item.count, entry.Markup, null);
            data.PrimaryInventory.RemoveAt(index);
            data.AvailableMoney += price;
            Info($"autobuy {entry.Item.itemValue.ItemClass.GetItemName()} x{entry.Item.count} for {price} at {vm.ToWorldPos()}");
        }

        private bool Eligible(TraderData.Entry entry)
        {
            if (entry == null || entry.Item == null || entry.Item.IsEmpty() || IsContract(entry)) return false;
            if (entry.Markup * 20 > Settings.AutoBuyMaxMarkupPercent) return false;
            var itemValue = entry.Item.itemValue;
            var itemClass = itemValue.ItemClass;
            if (itemClass == null || excluded.Contains(itemClass.GetItemName())) return false;
            var sellable = itemClass.IsBlock() ? Block.list[itemValue.type].SellableToTrader : itemClass.SellableToTrader;
            return sellable && BuyPrice(itemValue, entry.Item.count, entry.Markup, null) > 0;
        }

        private static int BuyPrice(ItemValue itemValue, int count, sbyte markup, EntityAlive buyer)
        {
            var itemClass = itemValue?.ItemClass;
            if (itemClass == null) return 0;
            float value;
            int bundle;
            if (itemClass.IsBlock())
            {
                var block = Block.list[itemValue.type];
                value = block.EconomicValue;
                bundle = block.EconomicBundleSize;
            }
            else
            {
                value = EffectManager.GetValue(PassiveEffects.EconomicValue, itemValue, itemClass.EconomicValue, buyer);
                bundle = itemClass.EconomicBundleSize;
            }
            if (value == 0f) return 0;

            var multiplier = 1f + markup * 0.2f;
            float price;
            if (itemValue.HasQuality)
            {
                var quality = ((int)itemValue.Quality - 1f) / 5f;
                price = value * multiplier;
                price *= itemClass.TraderQualityMinMod > 0f || itemClass.TraderQualityMaxMod > 0f
                    ? Mathf.Lerp(itemClass.TraderQualityMinMod, itemClass.TraderQualityMaxMod, quality)
                    : Mathf.Lerp(TraderInfo.QualityMinMod, TraderInfo.QualityMaxMod, quality);
                price *= itemValue.PercentUsesLeft;
            }
            else if (itemClass.HasSubItems)
            {
                price = 0f;
            }
            else
            {
                price = value * multiplier;
            }
            if (bundle <= 0) bundle = 1;
            return Mathf.CeilToInt((int)(price * (count / bundle)) * SandboxOptionManager.GetFloat(global::SandboxOptions.SandboxOptions.TraderBuyPrices));
        }
    }
}
