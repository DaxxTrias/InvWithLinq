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
        if (rule.MinOpenPrefixes is null && rule.MinOpenSuffixes is null)
            return true;
        var ok = true;
        if (rule.MinOpenPrefixes is int pReq)
        {
            ok &= ItemFilterUtils.OpenPrefixCount(item) >= pReq;
            if (!ok) return false;
        }
        if (rule.MinOpenSuffixes is int sReq)
        {
            ok &= ItemFilterUtils.OpenSuffixCount(item) >= sReq;
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
                var _ = TryExtractOpenCounts(expr, out var cleaned, out var minPref, out var minSuff);
                var filter = ItemFilter.LoadFromString(cleaned);
                var itemCtx = new ItemData(hoveredItem.Entity, GameController);
                var openOk = (minPref is null || ItemFilterUtils.OpenPrefixCount(itemCtx) >= minPref)
                             && (minSuff is null || ItemFilterUtils.OpenSuffixCount(itemCtx) >= minSuff);
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
                    TryExtractOpenCounts(text, out var cleaned, out var minPref, out var minSuff);
                    var filter = ItemFilter.LoadFromString(cleaned);
                    compiled.Add(new CompiledRule
                    {
                        Filter = filter,
                        RuleMeta = rule,
                        MinOpenPrefixes = minPref,
                        MinOpenSuffixes = minSuff,
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
                TryExtractOpenCounts(text, out var cleaned, out var minPref, out var minSuff);
                var filter = ItemFilter.LoadFromString(cleaned);
                newRules.Add(r);
                compiled.Add(new CompiledRule
                {
                    Filter = filter,
                    RuleMeta = r,
                    MinOpenPrefixes = minPref,
                    MinOpenSuffixes = minSuff,
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

    private static bool TryExtractOpenCounts(string expr, out string cleanedExpr, out int? minPrefixes, out int? minSuffixes)
    {
        int? localMinPrefixes = null;
        int? localMinSuffixes = null;
        var cleaned = expr;

        cleaned = OpenPrefixRegex.Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value;
            var num = int.Parse(m.Groups[2].Value);
            localMinPrefixes = MergeConstraint(localMinPrefixes, op, num);
            return "true"; // neutralize in expression
        });
        cleaned = OpenSuffixRegex.Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value;
            var num = int.Parse(m.Groups[2].Value);
            localMinSuffixes = MergeConstraint(localMinSuffixes, op, num);
            return "true";
        });

        cleanedExpr = cleaned;
        minPrefixes = localMinPrefixes;
        minSuffixes = localMinSuffixes;
        return minPrefixes != null || minSuffixes != null;
    }

    private static int? MergeConstraint(int? existing, string op, int value)
    {
        // We normalize to a minimum required open slots based on operator.
        int threshold = op switch
        {
            ">" => value + 1,
            ">=" => value,
            "==" => value,
            "<" => int.MinValue, // not a useful min; will be ignored in check logic
            "<=" => int.MinValue,
            _ => value,
        };
        if (op == "==")
        {
            return value; // equality overrides
        }
        if (existing is null) return threshold;
        return Math.Max(existing.Value, threshold);
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
