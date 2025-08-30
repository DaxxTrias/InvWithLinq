using System.Linq;
using ItemFilterLibrary;

namespace InvWithLinq
{
    public static class ItemFilterUtils
    {
        public static int SumItemStats(params int[] itemStats)
        {
            return itemStats.Sum();
        }
    }
}
