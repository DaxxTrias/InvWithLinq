using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using ItemFilterLibrary;
using RectangleF = ExileCore2.Shared.RectangleF;

#nullable enable

namespace InvWithLinq;

public class InvWithLinq : BaseSettingsPlugin<InvWithLinqSettings>
{
    private readonly TimeCache<List<CustomItemData>> _inventItems;
    private readonly TimeCache<List<CustomItemData>> _stashItems;
    private List<ItemFilter>? _itemFilters;
    private List<CompiledRule>? _compiledRules;
    private readonly List<string> ItemDebug = [];

    private sealed class CompiledRule
    {
        public required ItemFilter Filter { get; init; }
        public required InvRule RuleMeta { get; init; }
        public int? MinOpenPrefixes { get; init; }
        public int? MinOpenSuffixes { get; init; }
        public int? MaxOpenPrefixes { get; init; }
        public int? MaxOpenSuffixes { get; init; }
    }

    public InvWithLinq()
    {
        Name = "Inv With Linq";
        _inventItems = new TimeCache<List<CustomItemData>>(GetInventoryItems, 200);
        _stashItems = new TimeCache<List<CustomItemData>>(GetStashItems, 200);
    }

    private void InvalidateItemCaches()
    {
        // Force recreation of all cached items to clear ItemData internal caches
        _inventItems.ForceUpdate();
        _stashItems.ForceUpdate();
    }

    public override bool Initialise()
    {
        Settings.ReloadFilters.OnPressed = LoadRules;
        Settings.DumpInventoryItems.OnPressed = DumpItems;
        Settings.OpenDumpFolder.OnPressed = OpenDumpFolder;
        LoadRules();
        return true;
    }

    private bool ExtraOpenAffixConstraintsPass(ItemData item, CompiledRule rule)
    {
        if (rule.MinOpenPrefixes is null && rule.MinOpenSuffixes is null && rule.MaxOpenPrefixes is null && rule.MaxOpenSuffixes is null)
            return true;
        var ok = true;
        if (rule.MinOpenPrefixes is int pReq)
        {
            ok &= ItemFilterUtils.OpenPrefixCount(item) >= pReq;
            if (!ok) return false;
        }
        if (rule.MaxOpenPrefixes is int pMax)
        {
            ok &= ItemFilterUtils.OpenPrefixCount(item) <= pMax;
            if (!ok) return false;
        }
        if (rule.MinOpenSuffixes is int sReq)
        {
            ok &= ItemFilterUtils.OpenSuffixCount(item) >= sReq;
            if (!ok) return false;
        }
        if (rule.MaxOpenSuffixes is int sMax)
        {
            ok &= ItemFilterUtils.OpenSuffixCount(item) <= sMax;
        }
        return ok;
    }

    private void DumpItems()
    {
        var stopwatch = Stopwatch.StartNew();
        int processed = 0;
        int failed = 0;
        try
        {
            ItemDebug.Clear();
            // Use raw enumeration for dumps to avoid skipping items due to new/unknown mods or hidden UI.
            var items = GetInventoryItemsRaw();

            if (items.Count == 0)
            {
                DebugWindow.LogMsg($"{Name}: No inventory items found to dump.", 5);
            }

            foreach (var item in items)
            {
                try
                {
                    var affixes = GetItemAffixes(item);
                    ItemDebug.AddRange(affixes);
                    processed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    var baseName = item?.Entity?.GetComponent<Base>()?.Name ?? "(Unknown Base)";
                    var displayName = item?.Name ?? "(Unnamed)";
                    var path = item?.Entity?.Path ?? "(UnknownPath)";
                    ItemDebug.Add($"{baseName} - {displayName} (Path: {path})");
                    ItemDebug.Add($"  ! Error collecting affixes: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Directory.CreateDirectory(Path.Combine(DirectoryFullName, "Dumps"));
            var pathOut = Path.Combine(DirectoryFullName, "Dumps",
                $"{GameController.Area.CurrentArea.Name}.txt");

            try
            {
                File.WriteAllLines(pathOut, ItemDebug);
            }
            catch (Exception ioEx)
            {
                failed++;
                DebugWindow.LogError($"{Name}: Failed to write dump file to '{pathOut}'. {ioEx.GetType().Name}: {ioEx.Message}", 10);
                // Attempt a fallback path with timestamp in filename to avoid lock/collisions
                var fallback = Path.Combine(DirectoryFullName, "Dumps",
                    $"{GameController.Area.CurrentArea.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                try
                {
                    File.WriteAllLines(fallback, ItemDebug);
                    DebugWindow.LogMsg($"{Name}: Wrote dump to fallback path: {fallback}", 8);
                }
                catch (Exception fallbackEx)
                {
                    DebugWindow.LogError($"{Name}: Fallback write also failed. {fallbackEx.GetType().Name}: {fallbackEx.Message}", 10);
                }
            }

            stopwatch.Stop();
            LogMessage($"Inventory dump complete. Items processed: {processed}, failures: {failed}, time: {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DebugWindow.LogError($"{Name}: DumpItems failed. {ex.GetType().Name}: {ex.Message}", 10);
        }
    }

    private void OpenDumpFolder()
    {
        try
        {
            var dumpsDir = Path.Combine(DirectoryFullName, "Dumps");
            Directory.CreateDirectory(dumpsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dumpsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"{Name}: Failed to open dump folder. {ex.Message}", 10);
        }
    }

    public override void Render()
    {
        var hoveredItem = GetHoveredItem();

        if (IsInventoryVisible())
        {
            var invItems = GetFilteredInvItems().ToList();
            var invBounds = GetInventoryBounds();
            if (invBounds.Width <= 1 || invBounds.Height <= 1)
                invBounds = BuildUnionRect(invItems.Select(match => match.Item));
            foreach (var (item, rule) in invItems)
            {
                var frameColor = rule.Color;
                var itemRect = item.ClientRectangleCache;
                var drawRect = IntersectRect(itemRect, invBounds);
                if (drawRect.Width <= 1 || drawRect.Height <= 1)
                    continue;
					var hoverIntersects = hoveredItem != null && hoveredItem.Tooltip != null && item?.Entity != null && hoveredItem.Entity != null &&
											  hoveredItem.Tooltip.GetClientRectCache.Intersects(drawRect) &&
											  hoveredItem.Entity.Address != item.Entity.Address;
                if (hoverIntersects)
                {
                    Graphics.DrawFrame(drawRect, frameColor.Value.ToImguiVec4(45).ToColor(), Settings.FrameThickness);
                }
                else
                {
                    Graphics.DrawFrame(drawRect, frameColor, Settings.FrameThickness);
                }
            }
        }

		// OfflineMerchantPanel drawing moved to NPCInvWithLinq

		try
		{
			var (leftItems, leftPanel) = GetOpenLeftRelicLockerItems();
			if (leftPanel != null && leftItems.Count > 0)
			{
				var filtered = ApplyFilters(leftItems).ToList();
				
				if (Settings.EnableDebugLogging && filtered.Count > 0)
				{
					foreach (var (item, _) in filtered)
					{
						var itemName = item.Name ?? item.BaseName ?? "(unknown)";
						var hasHonour = item.ItemStats?[ExileCore2.Shared.Enums.GameStat.SanctumHonourResistancePct] ?? 0;
						var hasKey = item.ItemStats?[ExileCore2.Shared.Enums.GameStat.MapSanctumKeyDropChancePct] ?? 0;
						
						// Dump ALL ItemStats to see what's actually in the dictionary
						var allStats = item.ItemStats != null 
							? string.Join(", ", item.ItemStats.Where(kvp => kvp.Value != 0).Select(kvp => $"{kvp.Key}={kvp.Value}"))
							: "(no stats)";
						
						LogMessage($"[RelicLocker.Render] Highlighting: {itemName}", 2);
						LogMessage($"  Queried: HonourRes={hasHonour}, KeyDrop={hasKey}", 2);
						LogMessage($"  ALL ItemStats: {allStats}", 2);
					}
				}

				if (filtered.Count > 0)
				{
					foreach (var (item, rule) in filtered)
					{
						var frameColor = rule.Color;
						var drawRect = item.ClientRectangleCache;
						if (drawRect.Width <= 1 || drawRect.Height <= 1)
							continue;
						var hoverIntersects = hoveredItem != null && hoveredItem.Tooltip != null && item?.Entity != null && hoveredItem.Entity != null &&
											  hoveredItem.Tooltip.GetClientRectCache.Intersects(drawRect) &&
											  hoveredItem.Entity.Address != item.Entity.Address;
						Graphics.DrawFrame(drawRect, hoverIntersects ? frameColor.Value.ToImguiVec4(45).ToColor() : frameColor, Settings.FrameThickness);
					}
				}
			}
		}
		catch { }

        if (!IsStashVisible() || !Settings.EnableForStash)
            goto AfterStash;

        var stashItems = GetFilteredStashItems().ToList();
        var stashBounds = GetStashBounds();
        if (stashBounds.Width <= 1 || stashBounds.Height <= 1)
            stashBounds = BuildUnionRect(stashItems.Select(match => match.Item));
        foreach (var (stashItem, rule) in stashItems)
        {
            var frameColor = rule.Color;
            var itemRect = stashItem.ClientRectangleCache;
            var drawRect = IntersectRect(itemRect, stashBounds);
            if (drawRect.Width <= 1 || drawRect.Height <= 1)
                continue;
            var hoverIntersects = hoveredItem != null && hoveredItem.Tooltip != null && stashItem?.Entity != null && hoveredItem.Entity != null &&
                                  hoveredItem.Tooltip.GetClientRectCache.Intersects(drawRect) &&
                                  hoveredItem.Entity.Address != stashItem.Entity.Address;
            if (hoverIntersects)
            {
                Graphics.DrawFrame(drawRect, frameColor.Value.ToImguiVec4(45).ToColor(), Settings.FrameThickness);
            }
            else
            {
                Graphics.DrawFrame(drawRect, frameColor, Settings.FrameThickness);
            }
        }

    AfterStash:
        PerformItemFilterTest(hoveredItem);
    }
    
    private List<CustomItemData> GetStashItems()
    {
        var items = new List<CustomItemData>();

        if (!IsStashVisible())
            return items;

        var ingameUi = GameController?.Game?.IngameState?.IngameUi;
        if (ingameUi == null)
            return items;

        // Resolve currently visible stash (player or guild) defensively.
        // Accessing VisibleStash too early in Guild Stash can probe invalid child indices; guard it.
        var visibleStash = default(Inventory);
        try
        {
            if (ingameUi.StashElement?.IsVisible == true)
            {
                visibleStash = ingameUi.StashElement.VisibleStash;
            }
            else if (ingameUi.GuildStashElement?.IsVisible == true)
            {
                visibleStash = ingameUi.GuildStashElement.VisibleStash;
            }
        }
        catch
        {
            // If the UI structure isn't ready (e.g., switching tabs), skip this frame.
            return items;
        }

        // Aggregate items from the primary visible stash
        if (visibleStash != null)
        {
            AddItemsFromInventory(visibleStash, items);

            // If this tab is a nested container (folder), also traverse its visible nested inventories
            if (TryGetBoolFromProperty(visibleStash, "IsNestedInventory"))
            {
                var nestedInventories = TryGetInventoriesFromProperties(visibleStash, "NestedVisibleInventory");
                if (nestedInventories == null)
                {
                    var nestedContainer = TryGetPropertyValue(visibleStash, "NestedStashContainer");
                    if (nestedContainer != null)
                    {
                        nestedInventories = TryGetInventoriesFromProperties(nestedContainer, "NestedVisibleInventory", "VisibleInventories", "Inventories");
                    }
                }
                if (nestedInventories != null)
                {
                    foreach (var nested in nestedInventories)
                    {
                        if (nested != null)
                            AddItemsFromInventory(nested, items);
                    }
                }
            }
        }

        return items;
    }

    private void AddItemsFromInventory(Inventory inventory, List<CustomItemData> items)
    {
        var stashItems = TryGetRef(() => inventory?.VisibleInventoryItems as System.Collections.Generic.IList<ExileCore2.PoEMemory.Elements.InventoryElements.NormalInventoryItem>);
        if (stashItems == null)
            return;

        foreach (var slotItem in stashItems)
        {
            if (slotItem == null || slotItem.Address == 0 || slotItem.Item == null)
                continue;

            var itemEntity = slotItem.Item;

            // Validate entity before creating ItemData
            if (!IsEntityValidForItemData(itemEntity))
                continue;

            var rect = slotItem.GetClientRectCache;
            var safeItem = TryGetRef(() => new CustomItemData(itemEntity, GameController, rect));
            if (safeItem != null)
            {
                items.Add(safeItem);
            }
        }
    }

    private static bool TryGetBoolFromProperty(object source, string propertyName)
    {
        try
        {
            if (source == null) return false;
            var prop = source.GetType().GetProperty(propertyName);
            if (prop == null) return false;
            var value = prop.GetValue(source);
            return value is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private static object? TryGetPropertyValue(object source, string propertyName)
    {
        try
        {
            if (source == null) return null;
            var prop = source.GetType().GetProperty(propertyName);
            return prop?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static List<Inventory>? TryGetInventoriesFromProperties(object source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            try
            {
                var val = TryGetPropertyValue(source, name);
                if (val is System.Collections.IEnumerable enumerable)
                {
                    var list = new List<Inventory>();
                    foreach (var obj in enumerable)
                    {
                        if (obj is Inventory inv && inv != null)
                        {
                            list.Add(inv);
                        }
                    }
                    if (list.Count > 0)
                        return list;
                }
            }
            catch
            {
                // ignore and try next
            }
        }
        return null;
    }

    private static List<ExileCore2.PoEMemory.Elements.InventoryElements.NormalInventoryItem>? TryGetNormalInventoryItemsFromProperties(object source, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            try
            {
                var val = TryGetPropertyValue(source, name);
                if (val is System.Collections.IEnumerable enumerable)
                {
                    var list = new List<ExileCore2.PoEMemory.Elements.InventoryElements.NormalInventoryItem>();
                    foreach (var obj in enumerable)
                    {
                        if (obj is ExileCore2.PoEMemory.Elements.InventoryElements.NormalInventoryItem nii && nii != null)
                        {
                            list.Add(nii);
                        }
                    }
                    if (list.Count > 0)
                        return list;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static bool PanelContainsText(Element? root, string needle)
    {
        if (root == null || string.IsNullOrEmpty(needle))
            return false;
        try
        {
            var stack = new Stack<Element>();
            stack.Push(root);
            int visited = 0;
            while (stack.Count > 0 && visited < 2048)
            {
                visited++;
                var cur = stack.Pop();
                var t = cur?.Text;
                if (!string.IsNullOrEmpty(t) && t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                var children = cur?.Children;
                if (children != null)
                {
                    for (int i = 0; i < children.Count; i++)
                    {
                        var ch = children[i];
                        if (ch != null && ch.Address != 0)
                            stack.Push(ch);
                    }
                }
            }
        }
        catch { }
        return false;
    }


    private void CollectItemsFromPanel(Element panel, List<CustomItemData> items, bool enableDebugLogging = false)
    {
        if (panel == null || panel.Address == 0)
            return;

        try
        {
            if (TryGetPropertyValue(panel, "Inventory") is Inventory invSingle && invSingle != null)
            {
                AddItemsFromInventory(invSingle, items);
            }
        }
        catch { }

        var candidates = new List<object?>
        {
            panel,
            TryGetPropertyValue(panel, "InventoryPanel"),
            TryGetPropertyValue(panel, "VisibleStash"),
        };

        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;

            var inventories = TryGetInventoriesFromProperties(candidate, "Inventories", "VisibleInventories", "NestedVisibleInventory", "VisibleStash");
            if (inventories != null)
            {
                foreach (var inv in inventories)
                {
                    if (inv != null)
                        AddItemsFromInventory(inv, items);
                }
            }

			var normalItems = TryGetNormalInventoryItemsFromProperties(candidate, "VisibleInventoryItems", "YourOfferItems", "OtherOfferItems", "Items", "InventorySlotItems");
            if (normalItems != null)
            {
                foreach (var slotItem in normalItems)
                {
                    try
                    {
                        if (slotItem == null || slotItem.Address == 0 || slotItem.Item == null)
                            continue;

                        // Validate entity before creating ItemData
                        if (!IsEntityValidForItemData(slotItem.Item))
                            continue;

                        var rect = slotItem.GetClientRectCache;
                        var safeItem = TryGetRef(() => new CustomItemData(slotItem.Item!, GameController, rect));
                        if (safeItem != null)
                            items.Add(safeItem);
                    }
                    catch { }
                }
            }

			// Fallback: deep traversal to find NormalInventoryItem descendants
			if (candidate is Element element)
			{
                // max depth is 4 to avoid going too deep into the UI tree
				CollectNormalInventoryItemsFromDescendants(element, items, 4, 4096);
			}
        }
    }

	private void CollectNormalInventoryItemsFromDescendants(Element root, List<CustomItemData> items, int maxDepth = 6, int maxNodes = 4096)
	{
		if (root == null || root.Address == 0)
			return;
		try
		{
			var stack = new Stack<(Element el, int depth)>();
			stack.Push((root, 0));
			int visited = 0;
			while (stack.Count > 0 && visited < maxNodes)
			{
				var (el, depth) = stack.Pop();
				visited++;
				if (el == null || el.Address == 0)
					continue;

				// Try direct cast to NormalInventoryItem
				var nii = el as ExileCore2.PoEMemory.Elements.InventoryElements.NormalInventoryItem;
				if (nii != null)
				{
					try
					{
						if (nii.Item != null && nii.Item.Address != 0 && nii.Item.IsValid)
						{
							// Validate entity before creating ItemData
							if (!IsEntityValidForItemData(nii.Item))
								continue;

							var rect = nii.GetClientRectCache;
							var safeItem = TryGetRef(() => new CustomItemData(nii.Item!, GameController, rect));
							if (safeItem != null && safeItem.Entity?.IsValid == true)
								items.Add(safeItem);
						}
					}
					catch { }
				}
				else
				{
					// Reflective fallback: any element exposing an Entity via known property names
					try
					{
						Entity? maybeEntity = null;
						foreach (var pname in new[] { "Item", "Entity", "ItemEntity" })
						{
							maybeEntity = TryGetPropertyValue(el, pname) as Entity;
							if (maybeEntity != null && maybeEntity.Address != 0)
								break;
						}
						if (maybeEntity != null && maybeEntity.Address != 0 && maybeEntity.IsValid)
						{
							// Validate entity before creating ItemData
							if (!IsEntityValidForItemData(maybeEntity))
								continue;

							var rect = el.GetClientRectCache;
							var safeItem = TryGetRef(() => new CustomItemData(maybeEntity, GameController, rect));
							if (safeItem != null && safeItem.Entity?.IsValid == true)
								items.Add(safeItem);
						}
					}
					catch { }
				}

				if (depth >= maxDepth)
					continue;

				// Traverse children - be more lenient with IsVisible check for relic locker
				try
				{
					var children = el.Children;
					if (children != null)
					{
						for (int i = 0; i < children.Count; i++)
						{
							try
							{
								var ch = children[i];
								// For deeply nested items, check IsVisible but don't fail if the check throws
								if (ch != null && ch.Address != 0)
								{
									bool shouldTraverse = true;
									try
									{
										shouldTraverse = ch.IsVisible;
									}
									catch
									{
										// If IsVisible throws, assume true and traverse anyway
										shouldTraverse = true;
									}

									if (shouldTraverse)
										stack.Push((ch, depth + 1));
								}
							}
							catch { }
						}
					}
				}
				catch { }
			}
		}
		catch { }
	}


    private (List<CustomItemData> items, Element? panel) GetOpenLeftRelicLockerItems()
    {
        var list = new List<CustomItemData>();
        try
        {
            var panel = GameController?.IngameState?.IngameUi?.OpenLeftPanel
                        ?? TryGetPropertyValue(GameController?.IngameState?.IngameUi!, "OpenLeftPanel") as Element;
            
            if (panel == null || panel.Address == 0 || !panel.IsValid || !panel.IsVisible)
                return (list, null);

            // Check if panel has children (relic locker should have 3 children)
            int childCount = 0;
            try
            {
                childCount = panel.Children?.Count ?? 0;
            }
            catch
            {
                return (list, null);
            }

            // If panel has no children yet, it's still loading - skip this frame
            if (childCount == 0)
                return (list, null);

            if (!PanelContainsText(panel, "Relic Locker"))
                return (list, null);

            CollectItemsFromPanel(panel, list);
            return (list, panel);
        }
        catch
        {
            // Silently handle exceptions during relic locker detection
        }
        return (list, null);
    }

    private List<CustomItemData> GetInventoryItems()
    {
        var inventoryItems = new List<CustomItemData>();

        if (!IsInventoryVisible()) 
            return inventoryItems;

        var inventory = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories?[0]?.Inventory;
        var items = inventory?.InventorySlotItems;

        if (items == null) 
            return inventoryItems;

        foreach (var item in items)
        {
            if (item?.Item == null || item.Address == 0)
                continue;

            var itemEntity = item.Item;
            var metadata = itemEntity.Path;
            var isCurrencyOrQuest = !string.IsNullOrEmpty(metadata) && (
                metadata.StartsWith("Metadata/Items/Currency/", StringComparison.OrdinalIgnoreCase) ||
                metadata.StartsWith("Metadata/Items/QuestItems/", StringComparison.OrdinalIgnoreCase));

            // For currency/quest items, we use a more lenient validation
            // For regular items, we require valid explicit mods
            if (!isCurrencyOrQuest)
            {
                var modsComp = itemEntity.GetComponent<Mods>();
                if (modsComp?.ExplicitMods == null || modsComp.ExplicitMods.Count == 0)
                    continue;

                // Validate that the mods component won't cause ItemFilterLibrary to throw
                if (!IsModsComponentValid(modsComp))
                    continue;
            }
            else
            {
                // Even for currency/quest items, validate if Mods component exists
                if (!IsEntityValidForItemData(itemEntity))
                    continue;
            }

            var safeItem = TryGetRef(() => new CustomItemData(itemEntity, GameController!, item.GetClientRect()));
            if (safeItem != null)
            {
                inventoryItems.Add(safeItem);
            }
        }

        return inventoryItems;
    }

    private List<CustomItemData> GetInventoryItemsRaw()
    {
        var inventoryItems = new List<CustomItemData>();

        var inventory = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories?[0]?.Inventory;
        var items = inventory?.InventorySlotItems;

        if (items == null)
            return inventoryItems;

        foreach (var item in items)
        {
            if (item?.Item == null || item.Address == 0)
                continue;

            var safeItem = TryGetRef(() => new CustomItemData(item.Item, GameController!));
            if (safeItem != null)
            {
                inventoryItems.Add(safeItem);
            }
        }

        return inventoryItems;
    }

    private static List<string> GetItemAffixes(CustomItemData item)
    {
        var affixes = new List<string>();
        try
        {
            var baseName = item?.Entity?.GetComponent<Base>()?.Name ?? "(Unknown Base)";
            var displayName = item?.Name ?? "(Unnamed)";
            affixes.Add(baseName + " - " + displayName);

            var mods = item?.Entity?.GetComponent<Mods>();
            var implicitMods = mods?.ImplicitMods;
            var explicitMods = mods?.ExplicitMods;

            // If item has no implicit or explicit mods, keep dump concise
            if ((implicitMods == null || implicitMods.Count == 0) && (explicitMods == null || explicitMods.Count == 0))
            {
                affixes.Add("  - No Affixes");
                return affixes;
            }

            // Implicit section
            affixes.Add("[Implicits]");
            if (implicitMods != null && implicitMods.Count > 0)
            {
                foreach (var imod in implicitMods)
                {
                    if (imod?.ModRecord == null)
                        continue;
                    var stats = imod.ModRecord.StatNames;
                    if (stats == null)
                        continue;

                    foreach (var stat in stats)
                    {
                        if (stat == null)
                            continue;
                        var text = stat.MatchingStat;
                        affixes.Add($"  - {text}");
                    }
                }
            }
            else
            {
                affixes.Add("  (none)");
            }

            // Explicit section split into prefixes/suffixes
            affixes.Add("[Explicits]");
            if (explicitMods == null || explicitMods.Count == 0)
            {
                affixes.Add("  (none)");
                return affixes;
            }

            var prefixes = new List<object>();
            var suffixes = new List<object>();
            foreach (var emod in explicitMods)
            {
                if (emod?.ModRecord == null)
                    continue;
                var affixTypeObj = TryGetPropertyValue(emod.ModRecord, "AffixType");
                var kind = affixTypeObj?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(kind) && kind.Equals("Prefix", StringComparison.OrdinalIgnoreCase))
                {
                    prefixes.Add(emod);
                }
                else if (!string.IsNullOrEmpty(kind) && kind.Equals("Suffix", StringComparison.OrdinalIgnoreCase))
                {
                    suffixes.Add(emod);
                }
                else
                {
                    // If not specified, try GenerationType text as a fallback
                    var genType = TryGetPropertyValue(emod.ModRecord, "GenerationType")?.ToString();
                    if (!string.IsNullOrEmpty(genType) && genType.IndexOf("prefix", StringComparison.OrdinalIgnoreCase) >= 0)
                        prefixes.Add(emod);
                    else if (!string.IsNullOrEmpty(genType) && genType.IndexOf("suffix", StringComparison.OrdinalIgnoreCase) >= 0)
                        suffixes.Add(emod);
                    else
                        prefixes.Add(emod); // default bucket
                }
            }

            // Print prefixes then suffixes
            affixes.Add("  [Prefixes]");
            if (prefixes.Count == 0)
            {
                affixes.Add("    (none)");
            }
            else
            {
                foreach (dynamic pmod in prefixes)
                {
                    var stats = pmod.ModRecord?.StatNames;
                    if (stats == null)
                        continue;

                    foreach (var stat in stats)
                    {
                        if (stat == null)
                            continue;
                        var text = stat.MatchingStat;
                        affixes.Add($"    - {text}");
                    }
                }
            }

            affixes.Add("  [Suffixes]");
            if (suffixes.Count == 0)
            {
                affixes.Add("    (none)");
            }
            else
            {
                foreach (dynamic smod in suffixes)
                {
                    var stats = smod.ModRecord?.StatNames;
                    if (stats == null)
                        continue;

                    foreach (var stat in stats)
                    {
                        if (stat == null)
                            continue;
                        var text = stat.MatchingStat;
                        affixes.Add($"    - {text}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            affixes.Add($"  ! Error parsing mods: {ex.GetType().Name}: {ex.Message}");
        }
        return affixes;
    }

    private Element? GetHoveredItem()
    {
        var hover = GameController?.IngameState?.UIHover;
        if (hover == null || hover.Address == 0)
            return null;
        var entity = hover.Entity;
        return entity != null && entity.IsValid ? hover : null;
    }

    private bool IsStashVisible()
    {
        var ui = GameController?.IngameState?.IngameUi;
        return ui?.StashElement?.IsVisible == true || ui?.GuildStashElement?.IsVisible == true;
    }

    private bool IsInventoryVisible()
    {
        return GameController?.IngameState?.IngameUi?.InventoryPanel?.IsVisible == true;
    }
    
    private IEnumerable<(CustomItemData Item, InvRule Rule)> GetFilteredInvItems()
    {
        return GetMatchingItems(_inventItems.Value);
    }

    internal void ReloadRules()
    {
        LoadRules();
    }

    private IEnumerable<(CustomItemData Item, InvRule Rule)> GetFilteredStashItems()
    {
        return GetMatchingItems(_stashItems.Value);
    }

    private IEnumerable<(CustomItemData Item, InvRule Rule)> ApplyFilters(IEnumerable<CustomItemData> source)
    {
        return GetMatchingItems(source);
    }

    private IEnumerable<(CustomItemData Item, InvRule Rule)> GetMatchingItems(IEnumerable<CustomItemData> source)
    {
        if ((_compiledRules == null || _compiledRules.Count == 0) && (_itemFilters == null || _itemFilters.Count == 0) || Settings?.InvRules == null)
            yield break;

        foreach (var item in source)
        {
            if (TryGetMatchingRule(item, out var rule) && rule != null)
                yield return (item, rule);
        }
    }

    private bool TryGetMatchingRule(CustomItemData? item, out InvRule? matchingRule)
    {
        matchingRule = null;

        if (item == null || (_compiledRules == null || _compiledRules.Count == 0) && (_itemFilters == null || _itemFilters.Count == 0) || Settings?.InvRules == null)
            return false;

        try
        {
            if (item.Entity?.IsValid != true)
                return false;
        }
        catch
        {
            return false;
        }

        var rules = Settings.InvRules;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!rule.Enabled)
                continue;

            try
            {
                if (_compiledRules != null && i < _compiledRules.Count && _compiledRules[i] != null)
                {
                    var compiledRule = _compiledRules[i];
                    if (!ExtraOpenAffixConstraintsPass(item, compiledRule))
                        continue;

                    if (compiledRule.Filter.Matches(item))
                    {
                        matchingRule = rule;
                        return true;
                    }
                }
                else if (_itemFilters != null && i < _itemFilters.Count && _itemFilters[i].Matches(item))
                {
                    matchingRule = rule;
                    return true;
                }
            }
            catch
            {
                // Skip rules/items that throw during filter evaluation rather than crashing render.
                continue;
            }
        }

        return false;
    }

    private void PerformItemFilterTest(Element? hoveredItem)
    {
        if (Settings.FilterTest.Value is { Length: > 0 } && hoveredItem != null)
        {
            try
            {
                // Apply the same preprocessing as real rules so Open* tokens are supported
                var expr = Settings.FilterTest.Value;
                var _ = FilterPreProcessing.TryExtractOpenCounts(expr, out var cleaned, out var minPref, out var minSuff, out var maxPref, out var maxSuff);
                var filter = ItemFilter.LoadFromString(cleaned);
                var itemCtx = new ItemData(hoveredItem.Entity, GameController);
                var openOk = (minPref is null || ItemFilterUtils.OpenPrefixCount(itemCtx) >= minPref)
                             && (minSuff is null || ItemFilterUtils.OpenSuffixCount(itemCtx) >= minSuff)
                             && (maxPref is null || ItemFilterUtils.OpenPrefixCount(itemCtx) <= maxPref)
                             && (maxSuff is null || ItemFilterUtils.OpenSuffixCount(itemCtx) <= maxSuff);
                var matched = openOk && filter.Matches(itemCtx);
                DebugWindow.LogMsg($"{Name}: [Filter Test] Hovered Item: {matched}", 5);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"{Name}: [Filter Test] Error: {ex.GetType().Name}: {ex.Message}", 8);
            }
        }
    }

    private void LoadRules()
    {
        string configDirectory = ConfigDirectory;
        List<InvRule> existingRules = Settings.InvRules ?? new List<InvRule>();

        if (!string.IsNullOrEmpty(Settings.CustomConfigDirectory))
        {
            var customConfigFileDirectory = Path.Combine(Path.GetDirectoryName(ConfigDirectory)!, Settings.CustomConfigDirectory);
            
            if (Directory.Exists(customConfigFileDirectory))
            {
                configDirectory = customConfigFileDirectory;
            }
            else
            {
                DebugWindow.LogError( $"{Name}: Custom Config Folder does not exist.", 15);
            }
        }

        try
        {
            var discovered = new DirectoryInfo(configDirectory).GetFiles("*.ifl")
                .Select(x => new InvRule(x.Name, Path.GetRelativePath(configDirectory, x.FullName), false))
                .ToDictionary(r => r.Location, r => r, StringComparer.OrdinalIgnoreCase);

            var newRules = new List<InvRule>();
            var compiled = new List<CompiledRule>();

            foreach (var rule in existingRules)
            {
                var fullPath = Path.Combine(configDirectory, rule.Location);
                if (File.Exists(fullPath))
                {
                    newRules.Add(rule);
                    discovered.Remove(rule.Location);

                    // Preprocess and compile with extra constraints support
                    var text = File.ReadAllText(fullPath);
                    FilterPreProcessing.TryExtractOpenCounts(text, out var cleaned, out var minPref, out var minSuff, out var maxPref, out var maxSuff);
                    
                    if (Settings.EnableDebugLogging)
                        LogMessage($"[LoadRules] Rule {newRules.Count - 1} ({rule.Name}): MinPref={minPref}, MinSuff={minSuff}, MaxPref={maxPref}, MaxSuff={maxSuff}", 3);
                    if (Settings.EnableDebugLogging)
                        LogMessage($"[LoadRules] Cleaned filter text: {cleaned.Substring(0, Math.Min(200, cleaned.Length))}...", 4);

                    var filter = ItemFilter.LoadFromString(cleaned);
                    compiled.Add(new CompiledRule
                    {
                        Filter = filter,
                        RuleMeta = rule,
                        MinOpenPrefixes = minPref,
                        MinOpenSuffixes = minSuff,
                        MaxOpenPrefixes = maxPref,
                        MaxOpenSuffixes = maxSuff,
                    });
                }
                else
                {
                    DebugWindow.LogError($"{Name}: File \"{rule.Name}\" does not exist.", 15);
                }
            }

            // Append newly discovered rules at the end to preserve user order precedence
            foreach (var r in discovered.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.Combine(configDirectory, r.Location);
                var text = File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
                FilterPreProcessing.TryExtractOpenCounts(text, out var cleaned, out var minPref, out var minSuff, out var maxPref, out var maxSuff);
                var filter = ItemFilter.LoadFromString(cleaned);
                newRules.Add(r);
                compiled.Add(new CompiledRule
                {
                    Filter = filter,
                    RuleMeta = r,
                    MinOpenPrefixes = minPref,
                    MinOpenSuffixes = minSuff,
                    MaxOpenPrefixes = maxPref,
                    MaxOpenSuffixes = maxSuff,
                });
            }

            _itemFilters = compiled.Select(c => c.Filter).ToList();
            _compiledRules = compiled;
            Settings.InvRules = newRules;
            
            // Invalidate item caches to force recreation with fresh ItemData instances
            // This clears internal ItemStats caching that can cause stale filter results
            InvalidateItemCaches();
            
            if (Settings.EnableDebugLogging)
                LogMessage($"[LoadRules] Loaded {_compiledRules.Count} rules total, item caches invalidated", 2);
        }
        catch (Exception e)
        {
            DebugWindow.LogError($"{Name}: Filter Load Error.\n{e}", 15);
        }
    }

    private RectangleF GetInventoryBounds()
    {
        try
        {
            var invPanel = GameController?.IngameState?.IngameUi?.InventoryPanel;
            if (invPanel != null && invPanel.IsVisible)
            {
                return invPanel.GetClientRectCache;
            }
        }
        catch { }
        return default;
    }

    private RectangleF GetStashBounds()
    {
        try
        {
            var ui = GameController?.IngameState?.IngameUi;
            if (ui?.StashElement?.IsVisible == true)
            {
                // Prefer the scroll/content region if available to avoid drawing over headers/footers
                var content = TryGetPropertyValue(ui.StashElement, "InventoryPanel") as Element
                              ?? TryGetPropertyValue(ui.StashElement, "VisibleStash") as Element
                              ?? ui.StashElement;
                return content.GetClientRectCache;
            }
            if (ui?.GuildStashElement?.IsVisible == true)
            {
                var content = TryGetPropertyValue(ui.GuildStashElement, "InventoryPanel") as Element
                              ?? TryGetPropertyValue(ui.GuildStashElement, "VisibleStash") as Element
                              ?? ui.GuildStashElement;
                return content.GetClientRectCache;
            }
        }
        catch { }
        return default;
    }

    private static RectangleF IntersectRect(RectangleF a, RectangleF b)
    {
        if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0)
            return default;
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        if (right <= left || bottom <= top)
            return default;
        return new RectangleF
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top,
        };
    }

    private static RectangleF BuildUnionRect(IEnumerable<CustomItemData> items)
    {
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        bool any = false;
        foreach (var it in items)
        {
            try
            {
                var r = it.ClientRectangleCache;
                if (r.Width <= 0 || r.Height <= 0) continue;
                if (r.X < left) left = r.X;
                if (r.Y < top) top = r.Y;
                if (r.Right > right) right = r.Right;
                if (r.Bottom > bottom) bottom = r.Bottom;
                any = true;
            }
            catch { }
        }
        if (!any || right <= left || bottom <= top) return default;
        return new RectangleF { X = left, Y = top, Width = right - left, Height = bottom - top };
    }

    /// <summary>
    /// Validates an entity to ensure it won't cause exceptions in ItemFilterLibrary.
    /// Checks the Mods component if it exists for null ItemMod entries.
    /// </summary>
    private static bool IsEntityValidForItemData(Entity? entity)
    {
        if (entity == null || entity.Address == 0)
            return false;

        try
        {
            // Check if entity has a Mods component
            if (!entity.TryGetComponent<Mods>(out var modsComp))
            {
                // No Mods component is fine (e.g., currency items)
                return true;
            }

            // If Mods component exists, validate it thoroughly
            return IsModsComponentValid(modsComp);
        }
        catch
        {
            // If any validation throws, consider invalid
            return false;
        }
    }

    /// <summary>
    /// Validates a Mods component to ensure it won't cause NullReferenceException in ItemFilterLibrary.
    /// Checks for null ItemMod entries in all mod collections.
    /// </summary>
    private static bool IsModsComponentValid(Mods? modsComp)
    {
        if (modsComp == null)
            return false;

        try
        {
            // Check all mod collections for null entries or null ModRecords
            // This prevents ItemFilterLibrary.ModsData constructor from throwing NullReferenceException
            // The critical issue is in ModsData.Prefixes/Suffixes where ExplicitMods.Where(m => m.ModRecord.AffixType...)
            // will throw if any ItemMod in the collection is null
            var modCollections = new[]
            {
                modsComp.ItemMods,
                modsComp.ExplicitMods,
                modsComp.ImplicitMods,
                modsComp.EnchantedMods,
                modsComp.CorruptionImplicitMods,
                modsComp.SynthesisMods
            };

            foreach (var collection in modCollections)
            {
                if (collection == null)
                    continue;

                // Check if collection contains any null ItemMod or ItemMod with null ModRecord
                foreach (var mod in collection)
                {
                    if (mod == null || mod.ModRecord == null)
                        return false;
                }
            }

            // Note: ExplicitMods can be empty (valid for currency, white items, etc.)
            // We only check that if it exists, it doesn't contain null entries
            return true;
        }
        catch
        {
            // If any access throws (e.g., memory read issues), consider invalid
            return false;
        }
    }

    private static T? TryGetRef<T>(Func<T?> getter) where T : class
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }
}
