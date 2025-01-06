﻿using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared;
using ItemFilterLibrary;

public class CustomItemData : ItemData
{
    public CustomItemData(Entity queriedItem, GameController gc) : base(queriedItem, gc)
    {
    }

    public CustomItemData(Entity queriedItem, GameController gc, RectangleF getClientRectCache) : base(queriedItem, gc)
    {
        ClientRectangleCache = getClientRectCache;
    }
    public CustomItemData(Entity queriedItem, GameController gc, EKind kind, RectangleF getClientRectCache) : base(queriedItem, gc)
    {
        Kind = kind;
        ClientRectangleCache = getClientRectCache;
    }

    public RectangleF ClientRectangleCache { get; set; }
    public EKind Kind { get; }
}
public enum EKind
{
    QuestReward,
    Shop,
    RitualReward
}