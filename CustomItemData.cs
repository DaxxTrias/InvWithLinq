using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared;
using ItemFilterLibrary;

public class CustomItemData : ItemData
{
    public CustomItemData(Entity queriedItem, GameController gc) : base(queriedItem, gc)
    {
    }

    public CustomItemData(Entity queriedItem, GameController gc, RectangleF getClientRectCache, ItemSourceKind sourceKind = ItemSourceKind.Unknown, long sourceKey = 0) : base(queriedItem, gc)
    {
        ClientRectangleCache = getClientRectCache;
        SourceKind = sourceKind;
        SourceKey = sourceKey;
    }

    public CustomItemData(Entity queriedItem, GameController gc, EKind kind, RectangleF getClientRectCache, ItemSourceKind sourceKind = ItemSourceKind.Unknown, long sourceKey = 0) : base(queriedItem, gc)
    {
        Kind = kind;
        ClientRectangleCache = getClientRectCache;
        SourceKind = sourceKind;
        SourceKey = sourceKey;
    }

    public RectangleF ClientRectangleCache { get; set; }
    public EKind Kind { get; }
    public ItemSourceKind SourceKind { get; set; } = ItemSourceKind.Unknown;
    public long SourceKey { get; set; }
}

public enum EKind
{
    QuestReward,
    Shop,
    RitualReward
}

public enum ItemSourceKind
{
    Unknown,
    PlayerInventory,
    Stash,
    Panel
}