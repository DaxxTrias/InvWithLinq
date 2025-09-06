using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using ItemFilterLibrary;

namespace InvWithLinq;

public class InvWithLinq : BaseSettingsPlugin<InvWithLinqSettings>
{
    private readonly TimeCache<List<CustomItemData>> _inventItems;
    private readonly TimeCache<List<CustomItemData>> _stashItems;
    private List<ItemFilter> _itemFilters;
    private bool _isInTown = true;
    private readonly List<string> ItemDebug = new List<string>();

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

            if (items == null || items.Count == 0)
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

    public override void AreaChange(AreaInstance area)
    {
        if (area.IsHideout || area.IsTown)
        {
            _isInTown = true;
        }
        else
        {
            _isInTown = false;
        }
    }

    public override void Render()
    {
        var hoveredItem = GetHoveredItem();

        if (!IsInventoryVisible())
            return;

        foreach (var item in GetFilteredInvItems())
        {
            var frameColor = GetFilterColor(item);
            var hoverIntersects = hoveredItem != null && hoveredItem.Tooltip != null && item?.Entity != null && hoveredItem.Entity != null &&
                                  hoveredItem.Tooltip.GetClientRectCache.Intersects(item.ClientRectangleCache) &&
                                  hoveredItem.Entity.Address != item.Entity.Address;
            if (hoverIntersects)
            {
                Graphics.DrawFrame(item.ClientRectangleCache, frameColor.Value.ToImguiVec4(45).ToColor(), Settings.FrameThickness);
            }
            else
            {
                Graphics.DrawFrame(item.ClientRectangleCache, frameColor, Settings.FrameThickness);
            }
        }

        if (!IsStashVisible() || !Settings.EnableForStash)
            return;

        foreach (var stashItem in GetFilteredStashItems())
        {
            var frameColor = GetFilterColor(stashItem);
            var hoverIntersects = hoveredItem != null && hoveredItem.Tooltip != null && stashItem?.Entity != null && hoveredItem.Entity != null &&
                                  hoveredItem.Tooltip.GetClientRectCache.Intersects(stashItem.ClientRectangleCache) &&
                                  hoveredItem.Entity.Address != stashItem.Entity.Address;
            if (hoverIntersects)
            {
                Graphics.DrawFrame(stashItem.ClientRectangleCache, frameColor.Value.ToImguiVec4(45).ToColor(), Settings.FrameThickness);
            }
            else
            {
                Graphics.DrawFrame(stashItem.ClientRectangleCache, frameColor, Settings.FrameThickness);
            }
        }

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

        var stashItems = TryGetRef(() => visibleStash?.VisibleInventoryItems);
        if (stashItems != null)
        {
            foreach (var slotItem in stashItems)
            {
                if (slotItem == null || slotItem.Address == 0 || slotItem.Item == null)
                    continue;

                var itemEntity = slotItem.Item;
                var metadata = itemEntity.Path;
                var isCurrencyOrQuest = !string.IsNullOrEmpty(metadata) && (
                    metadata.StartsWith("Metadata/Items/Currency/", StringComparison.OrdinalIgnoreCase) ||
                    metadata.StartsWith("Metadata/Items/QuestItems/", StringComparison.OrdinalIgnoreCase));

                if (!isCurrencyOrQuest)
                {
                    var modsComp = itemEntity.GetComponent<Mods>();
                    if (modsComp?.ExplicitMods == null || modsComp.ExplicitMods.Count == 0 || modsComp.ExplicitMods.Any(m => m?.ModRecord == null))
                        continue;
                }

                var rect = slotItem.GetClientRectCache;
                var safeItem = TryGetRef(() => new CustomItemData(itemEntity, GameController, rect));
                if (safeItem != null)
                {
                    items.Add(safeItem);
                }
            }
        }
        return items;
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

            var safeItem = TryGetRef(() => new CustomItemData(item.Item, GameController, item.GetClientRect()));
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

            var safeItem = TryGetRef(() => new CustomItemData(item.Item, GameController));
            if (safeItem != null)
            {
                inventoryItems.Add(safeItem);
            }
        }

        return inventoryItems;
    }

    private List<string> GetItemAffixes(CustomItemData item)
    {
        var affixes = new List<string>();
        try
        {
            var baseName = item?.Entity?.GetComponent<Base>()?.Name ?? "(Unknown Base)";
            var displayName = item?.Name ?? "(Unnamed)";
            affixes.Add(baseName + " - " + displayName);

            var mods = item?.Entity?.GetComponent<Mods>();
            var explicitMods = mods?.ExplicitMods;
            if (explicitMods == null || explicitMods.Count == 0)
            {
                affixes.Add("  - (no explicit mods)");
                return affixes;
            }

            foreach (var mod in explicitMods)
            {
                if (mod?.ModRecord == null)
                    continue;
                var stats = mod.ModRecord.StatNames;
                if (stats == null || !stats.Any())
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
        catch (Exception ex)
        {
            affixes.Add($"  ! Error parsing mods: {ex.GetType().Name}: {ex.Message}");
        }
        return affixes;
    }

    private Element GetHoveredItem()
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
        if (_itemFilters == null || _itemFilters.Count == 0 || Settings?.InvRules == null)
            return Array.Empty<CustomItemData>();
        var items = _inventItems.Value;
        var rules = Settings.InvRules;
        return items.Where(item =>
        {
            for (int i = 0; i < _itemFilters.Count && i < rules.Count; i++)
            {
                if (!rules[i].Enabled)
                    continue;
                if (_itemFilters[i].Matches(item))
                    return true;
            }
            return false;
        });
    }

    internal void ReloadRules()
    {
        LoadRules();
    }

    private IEnumerable<CustomItemData> GetFilteredStashItems()
    {
        if (_itemFilters == null || _itemFilters.Count == 0 || Settings?.InvRules == null)
            return Array.Empty<CustomItemData>();
        var items = _stashItems.Value;
        var rules = Settings.InvRules;
        return items.Where(item =>
        {
            for (int i = 0; i < _itemFilters.Count && i < rules.Count; i++)
            {
                if (!rules[i].Enabled)
                    continue;
                if (_itemFilters[i].Matches(item))
                    return true;
            }
            return false;
        });
    }

    private void PerformItemFilterTest(Element hoveredItem)
    {
        if (Settings.FilterTest.Value is { Length: > 0 } && hoveredItem != null)
        {
            try
            {
                var filter = ItemFilter.LoadFromString(Settings.FilterTest);
                var matched = filter.Matches(new ItemData(hoveredItem.Entity, GameController));
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

            foreach (var rule in existingRules)
            {
                var fullPath = Path.Combine(configDirectory, rule.Location);
                if (File.Exists(fullPath))
                {
                    newRules.Add(rule);
                    discovered.Remove(rule.Location);
                }
                else
                {
                    DebugWindow.LogError($"{Name}: File \"{rule.Name}\" does not exist.", 15);
                }
            }

            // Append newly discovered rules at the end to preserve user order precedence
            newRules.AddRange(discovered.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase));

            _itemFilters = newRules
                .Select(x => ItemFilter.LoadFromPath(Path.Combine(configDirectory, x.Location)))
                .ToList();

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

    private static T? TryGetRef<T>(Func<T> getter) where T : class
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

    //private int SumItemStats(params int[] itemStats)
    //{
    //    return itemStats.Sum();
    //}
}
