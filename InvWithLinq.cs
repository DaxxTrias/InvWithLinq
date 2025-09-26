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
                invBounds = BuildUnionRect(invItems);
            foreach (var item in invItems)
            {
                var frameColor = GetFilterColor(item);
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
				if (filtered.Count > 0)
				{
					foreach (var item in filtered)
					{
						var frameColor = GetFilterColor(item);
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
            stashBounds = BuildUnionRect(stashItems);
        foreach (var stashItem in stashItems)
        {
            var frameColor = GetFilterColor(stashItem);
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

    private void CollectItemsFromPanel(Element panel, List<CustomItemData> items)
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
						if (nii.Item != null && nii.Item.Address != 0)
						{
							var rect = nii.GetClientRectCache;
							var safeItem = TryGetRef(() => new CustomItemData(nii.Item!, GameController, rect));
							if (safeItem != null)
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
						if (maybeEntity != null && maybeEntity.Address != 0)
						{
							var rect = el.GetClientRectCache;
							var safeItem = TryGetRef(() => new CustomItemData(maybeEntity, GameController, rect));
							if (safeItem != null)
								items.Add(safeItem);
						}
					}
					catch { }
				}

				if (depth >= maxDepth)
					continue;
					var children = el.Children;
				if (children != null)
				{
					for (int i = 0; i < children.Count; i++)
					{
							var ch = children[i];
							if (ch != null && ch.Address != 0 && ch.IsVisible)
							stack.Push((ch, depth + 1));
					}
				}
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

            if (!PanelContainsText(panel, "Relic Locker"))
                return (list, null);

            CollectItemsFromPanel(panel, list);
            return (list, panel);
        }
        catch { }
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

            var metadata = item.Item.Path;
            var isCurrencyOrQuest = !string.IsNullOrEmpty(metadata) && (
                metadata.StartsWith("Metadata/Items/Currency/", StringComparison.OrdinalIgnoreCase) ||
                metadata.StartsWith("Metadata/Items/QuestItems/", StringComparison.OrdinalIgnoreCase));

            var modsComp = item.Item.GetComponent<Mods>();

            // For non-currency/quest items, require valid explicit mods as before
            if (!isCurrencyOrQuest)
            {
                if (modsComp?.ExplicitMods == null || modsComp.ExplicitMods.Count == 0 || modsComp.ExplicitMods.Any(m => m?.ModRecord == null))
                    continue;
            }

            var safeItem = TryGetRef(() => new CustomItemData(item.Item, GameController!, item.GetClientRect()));
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
    
    private IEnumerable<CustomItemData> GetFilteredInvItems()
    {
        if ((_compiledRules == null || _compiledRules.Count == 0) && (_itemFilters == null || _itemFilters.Count == 0) || Settings?.InvRules == null)
            return Array.Empty<CustomItemData>();
        var items = _inventItems.Value;
        var rules = Settings.InvRules;
        return items.Where(item =>
        {
            try
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    if (!rules[i].Enabled)
                        continue;

                    // Prefer compiled rules with extra constraints when available
                    if (_compiledRules != null && i < _compiledRules.Count && _compiledRules[i] != null)
                    {
                        var cr = _compiledRules[i];
                        if (!ExtraOpenAffixConstraintsPass(item, cr))
                            continue;
                        if (cr.Filter.Matches(item))
                            return true;
                    }
                    else if (_itemFilters != null && i < _itemFilters.Count)
                    {
                        if (_itemFilters[i].Matches(item))
                            return true;
                    }
                }
                return false;
            }
            finally
            {
            }
        });
    }

    internal void ReloadRules()
    {
        LoadRules();
    }

    private IEnumerable<CustomItemData> GetFilteredStashItems()
    {
        if ((_compiledRules == null || _compiledRules.Count == 0) && (_itemFilters == null || _itemFilters.Count == 0) || Settings?.InvRules == null)
            return Array.Empty<CustomItemData>();
        var items = _stashItems.Value;
        var rules = Settings.InvRules;
        return items.Where(item =>
        {
            try
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    if (!rules[i].Enabled)
                        continue;

                    if (_compiledRules != null && i < _compiledRules.Count && _compiledRules[i] != null)
                    {
                        var cr = _compiledRules[i];
                        if (!ExtraOpenAffixConstraintsPass(item, cr))
                            continue;
                        if (cr.Filter.Matches(item))
                            return true;
                    }
                    else if (_itemFilters != null && i < _itemFilters.Count)
                    {
                        if (_itemFilters[i].Matches(item))
                            return true;
                    }
                }
                return false;
            }
            finally
            {
            }
        });
    }

    private IEnumerable<CustomItemData> ApplyFilters(IEnumerable<CustomItemData> source)
    {
        if ((_compiledRules == null || _compiledRules.Count == 0) && (_itemFilters == null || _itemFilters.Count == 0) || Settings?.InvRules == null)
            return Array.Empty<CustomItemData>();
        var rules = Settings.InvRules;
        return source.Where(item =>
        {
            try
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    if (!rules[i].Enabled)
                        continue;

                    if (_compiledRules != null && i < _compiledRules.Count && _compiledRules[i] != null)
                    {
                        var cr = _compiledRules[i];
                        if (!ExtraOpenAffixConstraintsPass(item, cr))
                            continue;
                        if (cr.Filter.Matches(item))
                            return true;
                    }
                    else if (_itemFilters != null && i < _itemFilters.Count)
                    {
                        if (_itemFilters[i].Matches(item))
                            return true;
                    }
                }
                return false;
            }
            finally
            {
            }
        });
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
        }
        catch (Exception e)
        {
            DebugWindow.LogError($"{Name}: Filter Load Error.\n{e}", 15);
        }
    }

    private ColorNode GetFilterColor(CustomItemData item)
    {
        if (_itemFilters == null || Settings?.InvRules == null)
            return Settings?.DefaultFrameColor ?? new ColorNode(Color.White);

        for (int i = 0; i < _itemFilters.Count && i < Settings.InvRules.Count; i++)
        {
            if (Settings.InvRules[i].Enabled && _itemFilters[i].Matches(item))
            {
                return Settings.InvRules[i].Color;
            }
        }
        return Settings.DefaultFrameColor;
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
