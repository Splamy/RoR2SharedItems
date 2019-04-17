using System.Linq;
using System.Reflection;
using Harmony;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace ShareSuite
{
    public static class Hooks
    {
        static MethodInfo sendPickupMessage =
            typeof(GenericPickupController).GetMethod("SendPickupMessage",
                BindingFlags.NonPublic | BindingFlags.Static);

        public static void DisableInteractablesScaling()
        {
            if (ShareSuite.WrapOverridePlayerScalingEnabled)
                On.RoR2.SceneDirector.PlaceTeleporter += (orig, self) => //Replace 1 player values
                {
                    if (!ShareSuite.WrapModIsEnabled)
                    {
                        orig(self);
                        return;
                    }

                    // Set interactables budget to 200 * config player count (normal calculation)
                    AccessTools.Field(AccessTools.TypeByName("RoR2.SceneDirector"), "interactableCredit")
                        .SetValue(self, 200 * ShareSuite.WrapInteractablesCredit);
                    orig(self);
                };

            if (ShareSuite.WrapOverrideBossLootScalingEnabled)
                IL.RoR2.BossGroup.OnCharacterDeathCallback += il => // Replace boss drops
                {
                    // return; F: Disabled until fixed for 2.0.0 T: no it works lol just not via ingame config
                    if (!ShareSuite.WrapModIsEnabled) return;
                    // Remove line where boss loot amount is specified and replace it with WrapBossLootCredit
                    var c = new ILCursor(il).Goto(99);
                    c.Remove();
                    c.Emit(OpCodes.Ldc_I4, ShareSuite.WrapBossLootCredit); // only works when it's a value
                };
        }

        public static void OnGrantItem()
        {
            On.RoR2.GenericPickupController.GrantItem += (orig, self, body, inventory) =>
            {
                if (!ShareSuite.WrapModIsEnabled)
                {
                    orig(self, body, inventory);
                    return;
                }

                // Give original player the item
                orig(self, body, inventory);

                // Do nothing else if single-player or 
                if (!IsMultiplayer())
                    return;

                if (!NetworkServer.active)
                    return;


                // Item to share
                var item = self.pickupIndex.itemIndex;

                // Iterate over all player characters in game
                if (!ShareSuite.WrapItemBlacklist.Contains((int) item))
                if (IsValidPickup(self.pickupIndex))
                foreach (var player in PlayerCharacterMasterController.instances.Select(p => p.master))
                {
                    if (!(bool) player.GetBody())
                    {
                        if (!player.alive && ShareSuite.WrapDeadPlayersGetItems)
                            player.inventory.GiveItem(item);
                        continue;
                    }

                    // Ensure character is not original player that picked up item
                    if (player.GetBody().Equals(body))
                        continue;
                    
                    // Give character the item
                    player.inventory.GiveItem(item);
                }
            };
        }

        public static void ModifyGoldReward()
        {
            if (ShareSuite.WrapMoneyIsShared)
            {
                On.RoR2.DeathRewards.OnKilled += (orig, self, info) =>
                {
                    if (!ShareSuite.WrapModIsEnabled)
                    {
                        orig(self, info);
                        return;
                    }

                    if (!NetworkServer.active) return;
                    // extraGold is the normal reward * player count - normal reward (so 4 players would get 4x normal gold)
                    var extraGold = self.goldReward * PlayerCharacterMasterController.instances.Count - self.goldReward;
                    foreach (var player in PlayerCharacterMasterController.instances.Select(p => p.master))
                    {
                        // Add money to players w/ scalar
                        player.GiveMoney(
                            (uint) Mathf.Floor(extraGold * ShareSuite.WrapMoneyScalar));
                    }

                    // give the normal amount of money and perform other onkill actions
                    orig(self, info);
                };
            }
        }

        public static void OnShopPurchase()
        {
            On.RoR2.PurchaseInteraction.OnInteractionBegin += (orig, self, activator) =>
            {
                if (!ShareSuite.WrapModIsEnabled)
                {
                    orig(self, activator);
                    return;
                }

                // Return if you can't afford the item
                if (!self.CanBeAffordedByInteractor(activator)) return;

                var characterBody = activator.GetComponent<CharacterBody>();
                var inventory = characterBody.inventory;

                if (ShareSuite.WrapMoneyIsShared)
                {
                    //TODO add comments on what this does
                    switch (self.costType)
                    {
                        case CostType.Money:
                        {
                            orig(self, activator);
                            foreach (var playerCharacterMasterController in PlayerCharacterMasterController.instances)
                            {
                                if (playerCharacterMasterController.master.alive &&
                                    playerCharacterMasterController.master.GetBody() != characterBody)
                                {
                                    playerCharacterMasterController.master.money -= (uint) self.cost;
                                }
                            }

                            return;
                        }

                        case CostType.PercentHealth:
                        {
                            orig(self, activator);
                            var teamMaxHealth = 0;
                            foreach (var playerCharacterMasterController in PlayerCharacterMasterController.instances)
                            {
                                var charMaxHealth = playerCharacterMasterController.master.GetBody().maxHealth;
                                if (charMaxHealth > teamMaxHealth)
                                {
                                    teamMaxHealth = (int) charMaxHealth;
                                }
                            }

                            var purchaseInteraction = self.GetComponent<PurchaseInteraction>();
                            var amount = (uint) (teamMaxHealth * purchaseInteraction.cost / 100.0 * 0.5f *
                                                 ShareSuite.WrapMoneyScalar);
                            var purchaseDiff =
                                amount - (uint) ((double) characterBody.maxHealth * purchaseInteraction.cost / 100.0 *
                                                 0.5f);

                            foreach (var playerCharacterMasterController in PlayerCharacterMasterController.instances)
                            {
                                if (!playerCharacterMasterController.master.alive) continue;
                                if (playerCharacterMasterController.master.GetBody() != characterBody)
                                {
                                    playerCharacterMasterController.master.GiveMoney(amount);
                                }
                                else
                                {
                                    playerCharacterMasterController.master.GiveMoney(purchaseDiff);
                                }
                            }

                            return;
                        }
                    }
                }

                // If this is not a multi-player server or the fix is disabled, do the normal drop action
                if (!IsMultiplayer() || !ShareSuite.WrapPrinterCauldronFixEnabled)
                {
                    orig(self, activator);
                    return;
                }

                var shop = self.GetComponent<ShopTerminalBehavior>();

                // If the cost type is an item, give the user the item directly and send the pickup message
                if (self.costType == CostType.WhiteItem
                    || self.costType == CostType.GreenItem
                    || self.costType == CostType.RedItem)
                {
                    var item = shop.CurrentPickupIndex().itemIndex;
                    inventory.GiveItem(item);
                    sendPickupMessage.Invoke(null,
                        new object[] {inventory.GetComponent<CharacterMaster>(), shop.CurrentPickupIndex()});
                }

                orig(self, activator);
            };
        }

        public static void OnPurchaseDrop()
        {
            On.RoR2.ShopTerminalBehavior.DropPickup += (orig, self) =>
            {
                if (!ShareSuite.WrapModIsEnabled)
                {
                    orig(self);
                    return;
                }

                if (!NetworkServer.active) return;
                var costType = self.GetComponent<PurchaseInteraction>().costType;
                Debug.Log("Cost type: " + costType);
                // If this is a multi-player lobby and the fix is enabled and it's not a lunar item, don't drop an item
                if (!IsMultiplayer()
                    || !IsValidPickup(self.CurrentPickupIndex())
                    || !ShareSuite.WrapPrinterCauldronFixEnabled
                    || self.itemTier == ItemTier.Lunar
                    || costType == CostType.Money)
                {
                    // Else drop the item
                    orig(self);
                }
            };
        }

        private static bool IsValidPickup(PickupIndex pickup)
        {
            var item = pickup.itemIndex;
            return IsWhiteItem(item) && ShareSuite.WrapWhiteItemsShared
                   || IsGreenItem(item) && ShareSuite.WrapGreenItemsShared
                   || IsRedItem(item) && ShareSuite.WrapRedItemsShared
                   || pickup.IsLunar() && ShareSuite.WrapLunarItemsShared
                   || IsBossItem(item) && ShareSuite.WrapBossItemsShared
                   || IsQueensGland(item) && ShareSuite.WrapQueensGlandsShared;
        }

        private static bool IsMultiplayer()
        {
            // Check if there are more then 1 players in the lobby
            return PlayerCharacterMasterController.instances.Count > 1;
        }

        public static bool IsWhiteItem(ItemIndex index)
        {
            return ItemCatalog.tier1ItemList.Contains(index);
        }

        public static bool IsGreenItem(ItemIndex index)
        {
            return ItemCatalog.tier2ItemList.Contains(index);
        }

        public static bool IsRedItem(ItemIndex index)
        {
            return ItemCatalog.tier3ItemList.Contains(index);
        }

        public static bool IsBossItem(ItemIndex index)
        {
            return index == ItemIndex.Knurl;
        }

        public static bool IsQueensGland(ItemIndex index)
        {
            return index == ItemIndex.BeetleGland;
        }
    }
}