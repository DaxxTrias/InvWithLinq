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
    private List<ItemFilter> _itemFilters;
    private List<CompiledRule> _compiledRules;
    private readonly List<string> ItemDebug = [];

    private sealed class CompiledRule
    {
        public required ItemFilter Filter { get; init; }
        public InvRule RuleMeta { get; init; }
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
            ok &= OpenPrefixCount(item) >= pReq;
            if (!ok) return false;
        }
        if (rule.MaxOpenPrefixes is int pMax)
        {
            ok &= OpenPrefixCount(item) <= pMax;
            if (!ok) return false;
        }
        if (rule.MinOpenSuffixes is int sReq)
        {
            ok &= OpenSuffixCount(item) >= sReq;
            if (!ok) return false;
        }
        if (rule.MaxOpenSuffixes is int sMax)
        {
            ok &= OpenSuffixCount(item) <= sMax;
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

    public override void Render()
    {
        var hoveredItem = GetHoveredItem();

        if (!IsInventoryVisible())
            return;

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

        if (!IsStashVisible() || !Settings.EnableForStash)
            return;

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
        var stashItems = TryGetRef(() => inventory?.VisibleInventoryItems);
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
#pragma warning disable CA1860
                    foreach (var stat in stats)
#pragma warning restore CA1860
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
#pragma warning disable CA1860
                    foreach (var stat in stats)
#pragma warning restore CA1860
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
#pragma warning disable CA1860
                    foreach (var stat in stats)
#pragma warning restore CA1860
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

    private void PerformItemFilterTest(Element hoveredItem)
    {
        if (Settings.FilterTest.Value is { Length: > 0 } && hoveredItem != null)
        {
            try
            {
                // Apply the same preprocessing as real rules so Open* tokens are supported
                var expr = Settings.FilterTest.Value;
                var _ = TryExtractOpenCounts(expr, out var cleaned, out var minPref, out var minSuff, out var maxPref, out var maxSuff);
                var filter = ItemFilter.LoadFromString(cleaned);
                var itemCtx = new ItemData(hoveredItem.Entity, GameController);
                var openOk = (minPref is null || OpenPrefixCount(itemCtx) >= minPref)
                             && (minSuff is null || OpenSuffixCount(itemCtx) >= minSuff)
                             && (maxPref is null || OpenPrefixCount(itemCtx) <= maxPref)
                             && (maxSuff is null || OpenSuffixCount(itemCtx) <= maxSuff);
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
                    TryExtractOpenCounts(text, out var cleaned, out var minPref, out var minSuff, out var maxPref, out var maxSuff);
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
                TryExtractOpenCounts(text, out var cleaned, out var minPref, out var minSuff, out var maxPref, out var maxSuff);
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

    private static readonly Regex OpenPrefixRegex = new Regex(@"OpenPrefixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenSuffixRegex = new Regex(@"OpenSuffixCount\s*\(\)\s*(==|>=|<=|>|<)\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool TryExtractOpenCounts(string expr, out string cleanedExpr, out int? minPrefixes, out int? minSuffixes, out int? maxPrefixes, out int? maxSuffixes)
    {
        int? localMinPrefixes = null;
        int? localMinSuffixes = null;
        int? localMaxPrefixes = null;
        int? localMaxSuffixes = null;
        var cleaned = NormalizeExpression(StripComments(expr ?? string.Empty));

        cleaned = OpenPrefixRegex.Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value;
            var num = int.Parse(m.Groups[2].Value);
            MergeConstraint(ref localMinPrefixes, ref localMaxPrefixes, op, num);
            return "true"; // neutralize in expression
        });
        cleaned = OpenSuffixRegex.Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value;
            var num = int.Parse(m.Groups[2].Value);
            MergeConstraint(ref localMinSuffixes, ref localMaxSuffixes, op, num);
            return "true";
        });

        cleanedExpr = cleaned;
        minPrefixes = localMinPrefixes;
        minSuffixes = localMinSuffixes;
        maxPrefixes = localMaxPrefixes;
        maxSuffixes = localMaxSuffixes;
        return minPrefixes != null || minSuffixes != null || maxPrefixes != null || maxSuffixes != null;
    }

    private static void MergeConstraint(ref int? minExisting, ref int? maxExisting, string op, int value)
    {
        switch (op)
        {
            case ">":
                minExisting = minExisting is null ? value + 1 : Math.Max(minExisting.Value, value + 1);
                break;
            case ">=":
                minExisting = minExisting is null ? value : Math.Max(minExisting.Value, value);
                break;
            case "<":
                maxExisting = maxExisting is null ? value - 1 : Math.Min(maxExisting.Value, value - 1);
                break;
            case "<=":
                maxExisting = maxExisting is null ? value : Math.Min(maxExisting.Value, value);
                break;
            case "==":
                minExisting = value;
                maxExisting = value;
                break;
            default:
                minExisting = minExisting is null ? value : Math.Max(minExisting.Value, value);
                break;
        }
    }

    // ===== Open affix support (aligned with NPCInvWithLinq) =====
    private static int OpenPrefixCount(ItemData item)
    {
        var max = GetMaxAffixes(item);
        var used = GetPrefixCount(item);
        var open = max - used;
        return open > 0 ? open : 0;
    }

    private static int OpenSuffixCount(ItemData item)
    {
        var max = GetMaxAffixes(item);
        var used = GetSuffixCount(item);
        var open = max - used;
        return open > 0 ? open : 0;
    }

    private static Mods TryGetMods(ItemData item)
    {
        try
        {
            return item?.Entity?.GetComponent<Mods>();
        }
        catch
        {
            return null;
        }
    }

    private static int GetPrefixCount(ItemData item)
    {
        var mods = TryGetMods(item);
        if (mods == null) return 0;
        if (TryGetIntProperty(mods, out var v, "PrefixesCount", "PrefixCount", "NumPrefixes")) return v;
        return CountAffixesByKind(mods, wantPrefix: true);
    }

    private static int GetSuffixCount(ItemData item)
    {
        var mods = TryGetMods(item);
        if (mods == null) return 0;
        if (TryGetIntProperty(mods, out var v, "SuffixesCount", "SuffixCount", "NumSuffixes")) return v;
        return CountAffixesByKind(mods, wantPrefix: false);
    }

    private static int GetMaxAffixes(ItemData item)
    {
        var mods = TryGetMods(item);
        if (mods == null) return 0;
        return ComputeMaxByTagsAndRarity(item);
    }

    private static int CountAffixesByKind(Mods mods, bool wantPrefix)
    {
        try
        {
            var explicitMods = TryGetPropertyValue(mods, "ExplicitMods") as System.Collections.IEnumerable;
            if (explicitMods == null) return 0;
            int count = 0;
            foreach (var m in explicitMods)
            {
                if (m == null) continue;
                var modRecord = TryGetPropertyValue(m, "ModRecord");
                if (modRecord != null)
                {
                    var affixTypeObj = TryGetPropertyValue(modRecord, "AffixType");
                    if (affixTypeObj != null)
                    {
                        var text = affixTypeObj.ToString()?.ToLowerInvariant() ?? string.Empty;
                        if (wantPrefix && text.Contains("prefix")) { count++; continue; }
                        if (!wantPrefix && text.Contains("suffix")) { count++; continue; }
                    }
                }
                if (TryGetBoolProperty(m, out var isPrefix, "IsPrefix") && wantPrefix && isPrefix) { count++; continue; }
                if (TryGetBoolProperty(m, out var isSuffix, "IsSuffix") && !wantPrefix && isSuffix) { count++; continue; }
                if (modRecord != null)
                {
                    var genType = TryGetPropertyValue(modRecord, "GenerationType");
                    if (genType != null)
                    {
                        var t = genType.ToString()?.ToLowerInvariant() ?? string.Empty;
                        if (wantPrefix && t.Contains("prefix")) { count++; continue; }
                        if (!wantPrefix && t.Contains("suffix")) { count++; continue; }
                    }
                    if (TryGetIntProperty(modRecord, out var genId, "GenerationTypeId", "GenerationId", "GenType"))
                    {
                        if (wantPrefix && genId == 1) count++;
                        else if (!wantPrefix && genId == 2) count++;
                    }
                }
            }
            return count;
        }
        catch { return 0; }
    }

    private static int ComputeMaxByTagsAndRarity(ItemData item)
    {
        var tags = GetItemTags(item);
        int baseMax;
        if (tags.Contains("flask")) baseMax = 1;
        else if (tags.Contains("jewel") || tags.Contains("abyssjewel") || tags.Contains("clusterjewel")) baseMax = 2;
        else baseMax = 3;
        var mods = TryGetMods(item);
        var rarity = GetRarityCode(mods);
        switch (rarity)
        {
            case 0: return 0; // Normal
            case 1: return Math.Min(baseMax, 1); // Magic
            case 2: return baseMax; // Rare
            case 3: return 0; // Unique
            default: return baseMax;
        }
    }

    private static int GetRarityCode(Mods mods)
    {
        if (mods == null) return -1;
        if (TryGetIntProperty(mods, out var r, "ItemRarity", "Rarity")) return r;
        var ro = TryGetPropertyValue(mods, "ItemRarity") ?? TryGetPropertyValue(mods, "Rarity");
        var t = ro?.ToString()?.ToLowerInvariant();
        return t switch { "normal" => 0, "magic" => 1, "rare" => 2, "unique" => 3, _ => -1 };
    }

    private static HashSet<string> GetItemTags(ItemData item)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item?.Entity == null || !item.Entity.IsValid)
            return tags;
        var path = item.Entity.Path ?? string.Empty;
        if (string.IsNullOrEmpty(path)) return tags;
        try
        {
            var baseComp = item.Entity.GetComponent<Base>();
            if (baseComp != null)
            {
                var baseType = TryGetPropertyValue(baseComp, "ItemBase") ?? TryGetPropertyValue(baseComp, "BaseItemType") ?? (object)baseComp;
                var t1 = TryGetPropertyValue(baseType, "Tags") as System.Collections.IEnumerable;
                var t2 = TryGetPropertyValue(baseType, "MoreTagsFromPath") as System.Collections.IEnumerable;
                AddStrings(tags, t1);
                AddStrings(tags, t2);
            }
        }
        catch { }
        var lower = path.ToLowerInvariant();
        if (lower.Contains("flask")) tags.Add("flask");
        if (lower.Contains("jewel")) tags.Add("jewel");
        if (lower.Contains("abyss")) tags.Add("abyssjewel");
        if (lower.Contains("cluster")) tags.Add("clusterjewel");
        return tags;
    }

    private static void AddStrings(HashSet<string> into, System.Collections.IEnumerable list)
    {
        if (list == null) return;
        foreach (var o in list)
        {
            if (o is string s && !string.IsNullOrWhiteSpace(s)) into.Add(s);
        }
    }

    private static bool TryGetIntProperty(object source, out int value, params string[] names)
    {
        value = 0;
        if (source == null) return false;
        foreach (var name in names)
        {
            var prop = source.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null) continue;
            try
            {
                var raw = prop.GetValue(source);
                if (raw is int i) { value = i; return true; }
                if (raw is long l) { value = unchecked((int)l); return true; }
                if (raw is short s) { value = s; return true; }
                if (raw is byte b) { value = b; return true; }
                if (raw is Enum e) { value = Convert.ToInt32(e); return true; }
            }
            catch { }
        }
        return false;
    }

    private static bool TryGetBoolProperty(object source, out bool value, params string[] names)
    {
        value = false;
        if (source == null) return false;
        foreach (var name in names)
        {
            var prop = source.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null) continue;
            try
            {
                var raw = prop.GetValue(source);
                if (raw is bool b) { value = b; return true; }
            }
            catch { }
        }
        return false;
    }

    private static string StripComments(string expr)
    {
        if (string.IsNullOrEmpty(expr)) return string.Empty;
        var sb = new System.Text.StringBuilder(expr.Length);
        bool inString = false;
        bool inBlock = false;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            char next = i + 1 < expr.Length ? expr[i + 1] : '\0';
            if (!inString && !inBlock && c == '/' && next == '/')
            {
                while (i < expr.Length && expr[i] != '\n') i++;
                continue;
            }
            if (!inString && !inBlock && c == '/' && next == '*')
            {
                inBlock = true; i++;
                continue;
            }
            if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; i++; }
                continue;
            }
            if (c == '"')
            {
                bool escaped = i > 0 && expr[i - 1] == '\\';
                if (!escaped) inString = !inString;
                sb.Append(c);
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string NormalizeExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return string.Empty;
        var nl = expr.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = nl.Split('\n');
        var sb = new System.Text.StringBuilder(nl.Length + 32);
        bool firstWritten = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (firstWritten)
            {
                bool startsWithOp = line.StartsWith("&&") || line.StartsWith("||") || line.StartsWith(")") || line.StartsWith("]") || line.StartsWith(",");
                char last = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                bool prevOpener = last == '(' || last == '{' || last == '[' || last == ',' || last == '&' || last == '|';
                if (!startsWithOp && !prevOpener) sb.Append(" || "); else sb.Append(' ');
            }
            sb.Append(line);
            firstWritten = true;
        }
        return sb.ToString();
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
