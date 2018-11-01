﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Trinity.Framework;
using Trinity.Framework.Actors.ActorTypes;
using Trinity.Framework.Helpers;
using Trinity.Framework.Objects;
using Trinity.Framework.Objects.Enums;
using Trinity.Framework.Reference;
using Trinity.Settings;
using Zeta.Bot.Coroutines;
using Zeta.Bot.Logic;
using Zeta.Game;
using Zeta.Game.Internals;
using Zeta.Game.Internals.Actors;

namespace Trinity.Components.Coroutines.Town
{
    public partial class TrinityTownRun
    {
        private static readonly List<int> s_extractionCandidatesTakenFromStash = new List<int>();

        public static HashSet<RawItemType> DoNotExtractRawItemTypes = new HashSet<RawItemType>
        {
            RawItemType.EnchantressSpecial,
            RawItemType.ScoundrelSpecial,
            RawItemType.FollowerSpecial,
            RawItemType.TemplarSpecial,
        };

        public static HashSet<int> DoNotExtractItemIds = new HashSet<int>()
        {
            Legendary.PigSticker.Id,
            Legendary.CorruptedAshbringer.Id
        };

        public static bool HasCurrencyRequired => Core.Inventory.Currency.HasCurrency(Zeta.Game.TransmuteRecipe.ExtractLegendaryPower);

        public static bool IsLegendaryPowerExtractionPossible
        {
            get
            {
                if (!ZetaDia.IsInGame ||
                    !ZetaDia.IsInTown ||
                    ZetaDia.Storage.CurrentWorldType != Act.OpenWorld ||
                    BrainBehavior.GreaterRiftInProgress)
                    return false;

                if (Core.Settings.KanaisCube.ExtractLegendaryPowers == CubeExtractOption.None)
                    return false;

                if (TownInfo.ZoltunKulle?.GetActor() is DiaUnit kule)
                {
                    if (kule.IsQuestGiver)
                    {
                        s_logger.Info($"[{nameof(IsLegendaryPowerExtractionPossible)}] Cube is not unlocked yet");
                        return false;
                    }
                }

                if (!HasCurrencyRequired)
                {
                    s_logger.Info($"[{nameof(IsLegendaryPowerExtractionPossible)}] Unable to find the required materials!");
                    return false;
                }

                var backpackCandidates = GetLegendaryExtractionCandidates(InventorySlot.BackpackItems);
                var stashCandidates = Core.Settings.KanaisCube.CubeExtractFromStash
                    ? GetLegendaryExtractionCandidates(InventorySlot.SharedStash).DistinctBy(i => i.ActorSnoId).ToList()
                    : new List<TrinityItem>();

                if (!backpackCandidates.Any() && !stashCandidates.Any())
                {
                    s_logger.Info($"[{nameof(IsLegendaryPowerExtractionPossible)}] There are no items that need extraction!");
                    return false;
                }

                return true;
            }
        }

        private static IEnumerable<TrinityItem> GetLegendaryExtractionCandidates(InventorySlot slot)
        {
            var alreadyCubedIds = new HashSet<int>(ZetaDia.Storage.PlayerDataManager.ActivePlayerData.KanaisPowersExtractedActorSnoIds);
            var usedIds = new HashSet<int>();

            foreach (var item in Core.Inventory.Where(i => i.InventorySlot == slot))
            {
                if (!item.IsValid)
                    continue;

                if (item.TrinityItemType == TrinityItemType.HealthPotion)
                    continue;

                if (item.FollowerType != FollowerType.None)
                    continue;

                if (usedIds.Contains(item.ActorSnoId))
                    continue;

                if (DoNotExtractRawItemTypes.Contains(item.RawItemType))
                    continue;

                if (DoNotExtractItemIds.Contains(item.ActorSnoId))
                    continue;

                if (alreadyCubedIds.Contains(item.ActorSnoId))
                    continue;

                if (Core.Settings.KanaisCube.ExtractLegendaryPowers == CubeExtractOption.OnlyTrashed &&
                    Combat.TrinityCombat.Loot.ShouldStash(item))
                    continue;

                if (Core.Settings.KanaisCube.ExtractLegendaryPowers == CubeExtractOption.OnlyNonAncient &&
                    !item.IsAncient)
                    continue;

                if (string.IsNullOrEmpty(Legendary.GetItem(item)?.LegendaryAffix))
                    continue;

                usedIds.Add(item.ActorSnoId);
                yield return item;
            }
        }

        public static async Task<bool> FetchExtractionCandidatesFromStash()
        {
            // TODO: Extract from Stash might be broken.
            if (!Core.Settings.KanaisCube.CubeExtractFromStash)
                return true;

            var stashCandidates = GetLegendaryExtractionCandidates(InventorySlot.SharedStash).ToList();
            if (!stashCandidates.Any())
                return true;

            if (!await TakeItemsFromStash(stashCandidates))
                return false;

            s_logger.Info($"[{nameof(FetchExtractionCandidatesFromStash)}] Got Legendaries from Stash");

            s_extractionCandidatesTakenFromStash.AddRange(stashCandidates.Select(i => i.AnnId));
            // Signal that we are not done...
            return false;
        }

        public static async Task<bool> PutExtractionCandidatesBackToStash()
        {
            if (!ZetaDia.IsInGame || !ZetaDia.IsInTown)
                return true;

            if (!s_extractionCandidatesTakenFromStash.Any())
                return true;

            if (!await CommonCoroutines.MoveAndInteract(TownInfo.Stash?.GetActor(), () => UIElements.StashWindow.IsVisible))
                return false;

            // Find the first item from the list.
            var item = InventoryManager.Backpack.FirstOrDefault(i => s_extractionCandidatesTakenFromStash.Contains(i.AnnId));

            // When the backpack does no longer contain a item from the list we are done...
            if (item == null)
            {
                s_extractionCandidatesTakenFromStash.Clear();
                return true;
            }

            // Make sure the item is valid.
            if (!item.IsValid || item.IsDisposed)
                return false;

            // TODO: Is that try/catch really required?
            try
            {
                s_logger.Debug($"[{nameof(PutExtractionCandidatesBackToStash)}] Adding {item.Name} ({item.ActorSnoId}) to stash. StackSize={item.ItemStackQuantity} AnnId={item.AnnId} InternalName={item.InternalName} Id={item.ActorSnoId} Quality={item.ItemQualityLevel} AncientRank={item.AncientRank}");
                InventoryManager.QuickStash(item);
            }
            catch (Exception ex)
            {
                s_logger.Error($"[{nameof(PutExtractionCandidatesBackToStash)}] Failed to handle one item. See Exception.", ex);
            }

            // When we reach that point we have to run again...
            return false;
        }

        public static async Task<bool> ExtractLegendaryPowers()
        {
            if (!IsLegendaryPowerExtractionPossible)
                return true;

            var backpackCandidate = GetLegendaryExtractionCandidates(InventorySlot.BackpackItems).FirstOrDefault();
            if (backpackCandidate != null)
            {
                if (!await ExtractPower(backpackCandidate))
                    return false;

                // Signal that we are not done...
                return false;
            }

            if (!await FetchExtractionCandidatesFromStash())
                return false;

            // Make sure we put back anything we removed from stash. Its possible for example that we ran out of materials
            // and the current backpack contents do no longer match the loot rules. Don't want them to be lost.
            if (!await PutExtractionCandidatesBackToStash())
                return false;

            s_logger.Info($"[{nameof(ExtractLegendaryPowers)}] Finished");
            return true;
        }

        public static async Task<bool> ExtractAllBackpack()
        {
            var candidate = GetLegendaryExtractionCandidates(InventorySlot.BackpackItems).FirstOrDefault();
            if (candidate == null)
            {
                Core.Logger.Log($"[{nameof(ExtractAllBackpack)}] Oh no! Out of materials!");
                return true;
            }
            return await ExtractPower(candidate);
        }

        private static async Task<bool> ExtractPower(TrinityItem item)
        {
            if (item == null)
                return true;

            if (!await TransmuteRecipe(Zeta.Game.TransmuteRecipe.ExtractLegendaryPower, item))
                return false;

            s_extractionCandidatesTakenFromStash.Remove(item.AnnId);
            return true;
        }
    }
}