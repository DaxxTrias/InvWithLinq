﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory;
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

    public InvWithLinq()
    {
        Name = "Inv With Linq";
        _inventItems = new TimeCache<List<CustomItemData>>(GetInventoryItems, 200);
        _stashItems = new TimeCache<List<CustomItemData>>(GetStashItems, 200);
    }

    public override bool Initialise()
    {
        Settings.ReloadFilters.OnPressed = LoadRules;
        LoadRules();
        return true;
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
        
        if (!_isInTown && !Settings.RunOutsideTown)
            return;

        foreach (var item in GetFilteredInvItems())
        {
            var frameColor = GetFilterColor(item);
            if (hoveredItem != null && hoveredItem.Tooltip.GetClientRectCache.Intersects(item.ClientRectangleCache) && hoveredItem.Entity.Address != item.Entity.Address)
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
            if (hoveredItem != null && hoveredItem.Tooltip.GetClientRectCache.Intersects(stashItem.ClientRectangleCache) && hoveredItem.Entity.Address != stashItem.Entity.Address)
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

        // Check if stash is visible in the UI
        var stashElement = GameController?.Game?.IngameState?.IngameUi?.StashElement;

        // We only proceed if either stash or guild stash is open/visible
        if (stashElement != null && stashElement.IsVisible)
        {
            // This is the "currently visible" stash:
            var visibleStash = stashElement.VisibleStash;
            // The bounding rectangle for the stash UI panel
            var stashRect = visibleStash?.InventoryUIElement?.GetClientRectCache;

            // Now get the stash items from that visible stash
            var stashItems = visibleStash?.VisibleInventoryItems;
            if (stashItems != null)
            {
                foreach (var slotItem in stashItems)
                {
                    if (slotItem == null || slotItem.Address == 0 || slotItem.Item == null)
                        continue;

                    var rect = slotItem.GetClientRectCache;
                    items.Add(new CustomItemData(slotItem.Item, GameController, rect));
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

        var inventory = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories[0]?.Inventory;
        var items = inventory?.InventorySlotItems;

        if (items == null) 
            return inventoryItems;

        foreach (var item in items)
        {
            if (item.Item == null || item.Address == 0) 
                continue;

            inventoryItems.Add(new CustomItemData(item.Item, GameController, item.GetClientRect()));
        }

        return inventoryItems;
    }

    private Element GetHoveredItem()
    {
        return GameController?.IngameState?.UIHover?.Address != 0 && GameController.IngameState.UIHover.Entity.IsValid
            ? GameController.IngameState.UIHover
            : null;
    }

    private bool IsStashVisible()
    {
        return GameController.IngameState.IngameUi.StashElement.IsVisible;
    }

    private bool IsInventoryVisible()
    {
        return GameController.IngameState.IngameUi.InventoryPanel.IsVisible;
    }
    
    private IEnumerable<CustomItemData> GetFilteredInvItems()
    {
        return _inventItems.Value.Where(x => _itemFilters.Any(y => y.Matches(x)));
    }

    internal void ReloadRules()
    {
        LoadRules();
    }

    private IEnumerable<CustomItemData> GetFilteredStashItems()
    {
        return _stashItems.Value.Where(x => _itemFilters.Any(y => y.Matches(x)));
    }

    private void PerformItemFilterTest(Element hoveredItem)
    {
        if (Settings.FilterTest.Value is { Length: > 0 } && hoveredItem != null)
        {
            var filter = ItemFilter.LoadFromString(Settings.FilterTest);
            var matched = filter.Matches(new ItemData(hoveredItem.Entity, GameController));
            DebugWindow.LogMsg($"{Name}: [Filter Test] Hovered Item: {matched}", 5);
        }
    }

    private void LoadRules()
    {
        string configDirectory = ConfigDirectory;
        List<InvRule> existingRules = Settings.InvRules;
        var existingColors = Settings.FilterColors;

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
            var newRules = new DirectoryInfo(configDirectory).GetFiles("*.ifl")
                .Select(x => new InvRule(x.Name, Path.GetRelativePath(configDirectory, x.FullName), false))
                .ExceptBy(existingRules.Select(x => x.Location), x => x.Location)
                .ToList();

            foreach (var rule in existingRules)
            {
                var fullPath = Path.Combine(configDirectory, rule.Location);
                if (File.Exists(fullPath))
                {
                    newRules.Add(rule);
                }
                else
                {
                    DebugWindow.LogError($"{Name}: File \"{rule.Name}\" does not exist.", 15);
                }
            }

            _itemFilters = newRules
                .Where(x => x.Enabled)
                .Select(x => ItemFilter.LoadFromPath(Path.Combine(configDirectory, x.Location)))
                .ToList();

            Settings.InvRules = newRules;

            // Initialize FilterColors for each filter
            Settings.FilterColors = new List<ColorNode>();
            for (int i = 0; i < newRules.Count; i++)
            {
                if (i < existingColors.Count)
                {
                    Settings.FilterColors.Add(existingColors[i]);
                }
                else
                {
                    Settings.FilterColors.Add(new ColorNode(Color.Red));
                }
            }
        }
        catch (Exception e)
        {
            DebugWindow.LogError($"{Name}: Filter Load Error.\n{e}", 15);
        }
    }

    private bool ItemInFilter(CustomItemData item)
    {
        return _itemFilters?.Any(filter => filter.Matches(item)) ?? false;
    }

    private ColorNode GetFilterColor(CustomItemData item)
    {
        for (int i = 0; i < _itemFilters.Count; i++)
        {
            if (_itemFilters[i].Matches(item))
            {
                return Settings.FilterColors[i];
            }
        }
        return Settings.DefaultFrameColor;
    }
}