using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using ExileCore2.PoEMemory.Components;
using ItemFilterLibrary;

#nullable enable

namespace InvWithLinq
{
    public static class ItemFilterUtils
    {
        public static int SumItemStats(params int[] itemStats)
        {
            return itemStats.Sum();
        }

        // Returns how many prefix slots are available on the item (clamped to >= 0)
        public static int OpenPrefixCount(ItemData item)
        {
            var max = GetMaxAffixes(item);
            var used = GetPrefixCount(item);
            var open = max - used;
            return open > 0 ? open : 0;
        }

        // Returns how many suffix slots are available on the item (clamped to >= 0)
        public static int OpenSuffixCount(ItemData item)
        {
            var max = GetMaxAffixes(item);
            var used = GetSuffixCount(item);
            var open = max - used;
            return open > 0 ? open : 0;
        }

        private static Mods? TryGetMods(ItemData item)
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
            if (mods == null)
                return 0;

            // Common direct properties
            if (TryGetIntProperty(mods, out var v, "PrefixesCount", "PrefixCount", "NumPrefixes"))
                return v;

            // Fallback: count explicit mods by GenerationType/IsPrefix
            return CountAffixesByKind(mods, wantPrefix: true);
        }

        private static int GetSuffixCount(ItemData item)
        {
            var mods = TryGetMods(item);
            if (mods == null)
                return 0;

            if (TryGetIntProperty(mods, out var v, "SuffixesCount", "SuffixCount", "NumSuffixes"))
                return v;

            return CountAffixesByKind(mods, wantPrefix: false);
        }

        private static int GetMaxPrefixes(ItemData item)
        {
            var mods = TryGetMods(item);
            if (mods == null)
                return 0;

            if (TryGetIntProperty(mods, out var v, "PrefixesMax", "MaxPrefixes", "TotalAllowedPrefixes", "MaximumPrefixes"))
                return v;

            // Reasonable default if metadata is not exposed
            return 3;
        }

        private static int GetMaxSuffixes(ItemData item)
        {
            var mods = TryGetMods(item);
            if (mods == null)
                return 0;

            if (TryGetIntProperty(mods, out var v, "SuffixesMax", "MaxSuffixes", "TotalAllowedSuffixes", "MaximumSuffixes"))
                return v;

            return 3;
        }

        private static int CountAffixesByKind(Mods mods, bool wantPrefix)
        {
            try
            {
                var explicitMods = GetPropertyValue(mods, "ExplicitMods") as System.Collections.IEnumerable;
                if (explicitMods == null)
                    return 0;

                int count = 0;
                foreach (var m in explicitMods)
                {
                    if (m == null) continue;

                    // Try boolean flags first
                    if (TryGetBoolProperty(m, out var isPrefix, "IsPrefix") && wantPrefix && isPrefix)
                    {
                        count++;
                        continue;
                    }
                    if (TryGetBoolProperty(m, out var isSuffix, "IsSuffix") && !wantPrefix && isSuffix)
                    {
                        count++;
                        continue;
                    }

                    // Inspect ModRecord.GenerationType when present
                    var modRecord = GetPropertyValue(m, "ModRecord");
                    if (modRecord == null) continue;

                    var genType = GetPropertyValue(modRecord, "GenerationType");
                    if (genType != null)
                    {
                        var text = genType.ToString()?.ToLowerInvariant() ?? string.Empty;
                        if (wantPrefix && text.Contains("prefix")) count++;
                        else if (!wantPrefix && text.Contains("suffix")) count++;
                        continue;
                    }

                    // Some builds expose numeric generation ids; try common mapping: 1=Prefix, 2=Suffix
                    if (TryGetIntProperty(modRecord, out var genId, "GenerationTypeId", "GenerationId", "GenType"))
                    {
                        if (wantPrefix && genId == 1) count++;
                        else if (!wantPrefix && genId == 2) count++;
                    }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        // Compute the maximum number of prefixes/suffixes per item based on tags and rarity
        private static int GetMaxAffixes(ItemData item)
        {
            var mods = TryGetMods(item);
            if (mods == null)
                return 0;

            return ComputeMaxByTagsAndRarity(item);
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

        private static int GetRarityCode(Mods? mods)
        {
            if (mods == null) return -1;
            if (TryGetIntProperty(mods, out var r, "ItemRarity", "Rarity")) return r;
            var ro = GetPropertyValue(mods, "ItemRarity") ?? GetPropertyValue(mods, "Rarity");
            var t = ro?.ToString()?.ToLowerInvariant();
            return t switch { "normal" => 0, "magic" => 1, "rare" => 2, "unique" => 3, _ => -1 };
        }

        private static System.Collections.Generic.HashSet<string> GetItemTags(ItemData item)
        {
            var tags = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item?.Entity == null || !item.Entity.IsValid)
                return tags;
            var path = item.Entity.Path ?? string.Empty;
            if (string.IsNullOrEmpty(path)) return tags;
            try
            {
                var baseComp = item.Entity.GetComponent<Base>();
                if (baseComp != null)
                {
                    var baseType = GetPropertyValue(baseComp, "ItemBase") ?? GetPropertyValue(baseComp, "BaseItemType") ?? (object)baseComp;
                    var t1 = GetPropertyValue(baseType, "Tags") as System.Collections.IEnumerable;
                    var t2 = GetPropertyValue(baseType, "MoreTagsFromPath") as System.Collections.IEnumerable;
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

        private static void AddStrings(System.Collections.Generic.HashSet<string> into, System.Collections.IEnumerable? list)
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
                var prop = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (prop == null) continue;
                try
                {
                    var raw = prop.GetValue(source);
                    if (raw is int i)
                    {
                        value = i;
                        return true;
                    }
                    if (raw is long l)
                    {
                        value = unchecked((int)l);
                        return true;
                    }
                    if (raw is short s)
                    {
                        value = s;
                        return true;
                    }
                    if (raw is byte b)
                    {
                        value = b;
                        return true;
                    }
                    if (raw is Enum e)
                    {
                        value = Convert.ToInt32(e);
                        return true;
                    }
                }
                catch
                {
                    // try next
                }
            }
            return false;
        }

        private static bool TryGetBoolProperty(object source, out bool value, params string[] names)
        {
            value = false;
            if (source == null) return false;
            foreach (var name in names)
            {
                var prop = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (prop == null) continue;
                try
                {
                    var raw = prop.GetValue(source);
                    if (raw is bool b)
                    {
                        value = b;
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        private static object? GetPropertyValue(object source, string name)
        {
            try
            {
                var prop = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                return prop?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }
    }
}
