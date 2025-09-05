## InvWithLinq IFL Recipes

This plugin highlights items in your Inventory and (optionally) Stash based on rules written in IFL (Item Filter Language). Each `.ifl` file returns true/false for a given item; if true, the item is highlighted with the rule's configured color.

- Config folder: place `.ifl` files under your ExileCore config path for `InvWithLinq` (e.g., `config/InvWithLinq/`).
- Rule selection and priority: enable/disable and order files in the plugin settings. The first enabled file that matches determines the color.
- Comments: start lines with `//`.
- Quick testing: use the `Filter Test` textbox in settings; hover an item to see if the expression matches.

### Expression building blocks

- Logical: `&&`, `||`, `!`
- Numeric compare: `==`, `!=`, `>=`, `<=`, `>`, `<`
- Strings: `BaseName == "..."`, `BaseName.Contains("...")`, `BaseName.StartsWith("...")`
- Tags: `HasTag("Bow")`, `HasTag("Ring")`, `HasTag("Map")`, etc.
- Player info: `PlayerInfo.Level` (your character level)
- Item stats: `ItemStats[GameStat.X]` exposes parsed numeric values of explicit/implicit stats
- Combinations: aggregate booleans and require N-of-M using `Count`

Example N-of-M pattern:

```csharp

new [] {
  ItemStats[GameStat.BaseMaximumLife] >= 80,
  ItemStats[GameStat.BaseFireDamageResistancePct] >= 20,
  ItemStats[GameStat.BaseColdDamageResistancePct] >= 20,
}.Count(x => x) >= 2
```

Note on “tiers”: If your build does not expose explicit tier helpers, approximate tiers with numeric thresholds (e.g., treat Life ≥ 100 as T1/T2 equivalent).

---

### 1) Match by item name

Exact base name:

```csharp

BaseName == "Advanced Dualstring Bow"
```

Contains / prefix matching:

```csharp
BaseName.Contains("Dualstring")
// or
BaseName.StartsWith("Advanced ")
```

Group by a set of names:

```csharp
(
  BaseName == "Expert Dualstring Bow" ||
  BaseName == "Advanced Dualstring Bow" ||
  BaseName == "Long Bow"
)
```

Alphabet “buckets” (use as separate files to simulate sort-by-name priority):

```csharp
BaseName.StartsWith("A") || BaseName.StartsWith("B")
```

---

### 2) Level ranges (character level and item level)

By character level (e.g., while mapping ≥ 65):

```csharp
PlayerInfo.Level >= 65
```

Level bracket:

```csharp
PlayerInfo.Level >= 35 && PlayerInfo.Level <= 65
```

If your IFL environment exposes `ItemLevel` (ilvl), you can also do:

```csharp
ItemLevel >= 75 && ItemLevel <= 86
```

---

### 3) Affix combinations (require N of M)

Rings with life + any two resists:

```csharp
HasTag("Ring") && (
  new [] {
    ItemStats[GameStat.BaseMaximumLife] >= 80,
    ItemStats[GameStat.BaseFireDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseColdDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseLightningDamageResistancePct] >= 20,
  }.Count(x => x) >= 3
)
```

Bows with speed, %phys, or added elemental (any 2):

```csharp
HasTag("Bow") && (
  new [] {
    ItemStats[GameStat.AttackSpeedPct] >= 15,
    ItemStats[GameStat.LocalPhysicalDamagePct] >= 120,
    ItemStats[GameStat.LocalMinimumAddedFireDamage] >= 30,
    ItemStats[GameStat.LocalMinimumAddedColdDamage] >= 15,
  }.Count(x => x) >= 2
)
```

---

### 4) Approximating tiers by thresholds

Treat Life ≥ 100 as “T1/T2” and ≥ 80 as “T3+”, then demand at least one T1+ and one T3+:

```csharp
HasTag("Glove") && (
  (ItemStats[GameStat.BaseMaximumLife] >= 100) &&
  new [] {
    ItemStats[GameStat.BaseFireDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseColdDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseLightningDamageResistancePct] >= 20,
    ItemStats[GameStat.BaseChaosDamageResistancePct] >= 10,
  }.Count(x => x) >= 1
)
```

Elemental bow “T1+” approximation:

```csharp
HasTag("Bow") && (
  new [] {
    ItemStats[GameStat.ElementalDamageWithAttackSkillsPct] >= 80,
    ItemStats[GameStat.ProjectileSkillGemLevel] >= 2,
    ItemStats[GameStat.LocalCriticalStrikeChance] >= 200,
  }.Count(x => x) >= 2
)
```

---

### 5) “Good” vs “Bad” map examples

Good maps (any 1 of beneficial mods):

```csharp
HasTag("Map") && (
  PlayerInfo.Level >= 65 && (
    new [] {
      ItemStats[GameStat.MapExperienceGainPct] >= 15,
      ItemStats[GameStat.MapItemDropQuantityPct] >= 10,
      ItemStats[GameStat.MapItemDropRarityPct] >= 15,
      ItemStats[GameStat.MapMonstersArmourBreakPhysicalDamagePctDealtAsArmourBreak] >= 1,
      ItemStats[GameStat.MapGoldPct] >= 5,
    }.Count(x => x) >= 1
  )
)
```

Bad maps (deny-list; match if any 1 bad mod is present):

```csharp
HasTag("Map") && (
  PlayerInfo.Level >= 65 && (
    new [] {
      ItemStats[GameStat.MapMonstersDamagePct] >= 1,
      ItemStats[GameStat.MapMonstersAttackSpeedPct] >= 1,
      ItemStats[GameStat.MapMonstersAdditionalNumberOfProjecitles] >= 1,
      ItemStats[GameStat.MapMonsterSkillsChainXAdditionalTimes] >= 1,
      ItemStats[GameStat.MapMonstersPoisonOnHit] >= 1,
      ItemStats[GameStat.MapMonstersCriticalStrikeChancePct] >= 1,
      ItemStats[GameStat.MapMonstersPenetrateElementalResistancesPct] >= 1,
      ItemStats[GameStat.MapMonstersAccuracyRatingPct] >= 1,
      ItemStats[GameStat.MapPlayerHasLevelXTemporalChains] >= 1,
      ItemStats[GameStat.MapPlayerHasLevelXElementalWeakness] >= 1,
      ItemStats[GameStat.MapPlayerHasLevelXEnfeeble] >= 1,
      ItemStats[GameStat.MapPlayerHasLevelXVulnerability] >= 1,
      ItemStats[GameStat.MapMonstersPctAllDamageToGainAsChaos] >= 1,
      ItemStats[GameStat.MapGroundLightning] >= 1,
    }.Count(x => x) >= 1
  )
)
```

---

### 6) Jewels by stat clusters

Any jewel with at least one useful bow-related stat:

```csharp
HasTag("Jewel") && (
  PlayerInfo.Level >= 1 && (
    new [] {
      ItemStats[GameStat.ProjectileDamagePct] >= 1,
      ItemStats[GameStat.LightningDamagePct] >= 1,
      ItemStats[GameStat.AttackDamagePctVsRareOrUniqueEnemy] >= 1,
      ItemStats[GameStat.ElementalDamagePct] >= 1,
      ItemStats[GameStat.AttackSpeedPct] >= 1,
      ItemStats[GameStat.BowAttackSpeedPct] >= 1,
      ItemStats[GameStat.DamagePctWithBowSkills] >= 1,
      ItemStats[GameStat.QuiverModEffectPct] >= 1,
      ItemStats[GameStat.ProjectileSpeedPct] >= 1,
    }.Count(x => x) >= 1
  )
)
```

---

### 7) Open affix slots (if exposed in your build)

Some builds of the IFL host expose open slot counts. If available, you can write:

```csharp
// Require at least one open suffix (craftable) and one open prefix
HasTag("Ring") && OpenSuffixCount >= 1 && OpenPrefixCount >= 1
```

If these symbols are not available in your environment, this feature is not supported and the rule will fail to compile. In that case, approximate by requiring only the affixes you care about and review items manually for open slots.

---

### 8) Putting it together: bow pickup rule

A practical “any 2-of” bow with a name filter and optional open suffix (if supported):

```csharp
HasTag("Bow") && BaseName.Contains("Dualstring") && (
  new [] {
    ItemStats[GameStat.LocalPhysicalDamagePct] >= 120,
    ItemStats[GameStat.AttackSpeedPct] >= 12,
    ItemStats[GameStat.ProjectileSkillGemLevel] >= 2,
    ItemStats[GameStat.ElementalDamageWithAttackSkillsPct] >= 80,
  }.Count(x => x) >= 2
)
// Optional, only if available:
&& OpenSuffixCount >= 1
```

---

### Tips

- Keep files focused. Create multiple `.ifl` files (e.g., `good_maps.ifl`, `bad_maps.ifl`, `ranger_bows.ifl`) and order them by priority in the plugin settings.
- Prefer numeric thresholds to emulate tiers where explicit tier info is unavailable.
- Use `Count` patterns to express flexible N-of-M requirements instead of long chains of `||`.
- When testing, paste expressions into the `Filter Test` box and hover an item; the plugin will log whether it matched.

---

### 9) Sockets and Item Quality

Socket count (e.g., pick items with at least 2 sockets):

```csharp
SocketInfo.SocketNumber >= 2
```

Generic “value” pickup via quality:

```csharp
ItemQuality >= 1
```

Combine with bases:

```csharp
ItemQuality >= 1
|| BaseName == "Gold Ring"
|| BaseName == "Prismatic Ring"
|| BaseName == "Amethyst Ring"
|| BaseName == "Gold Amulet"
|| BaseName == "Stellar Amulet"
|| BaseName == "Utility Belt"
|| BaseName == "Sapphire Ring"
```

---

### 10) ClassName and Tags

You can gate by item class or tags. Use what is available in your build:

```csharp
ClassName == "Jewel"
// or, more portable across builds:
HasTag("Jewel")
```

---

### 11) Bow: additional arrows/projectiles

Projectiles/Arrows (prefer arrows where relevant to bows):

```csharp
HasTag("Bow") && (
  ItemStats[GameStat.NumberOfAdditionalArrows] >= 1
  || ItemStats[GameStat.NumberOfAdditionalProjectiles] >= 1
)
```

Typical bow cluster (mix and match):

```csharp
HasTag("Bow") && (
  new [] {
    ItemStats[GameStat.BowSkillGemLevel] >= 3,
    ItemStats[GameStat.ProjectileSkillGemLevel] >= 3,
    ItemStats[GameStat.LocalPhysicalDamagePct] >= 135,
    ItemStats[GameStat.AttackSpeedPct] >= 14,
    ItemStats[GameStat.BaseProjectileSpeedPct] >= 14,
    ItemStats[GameStat.DamagePctWithBowSkills] >= 15,
  }.Count(x => x) >= 2
)
```

---

### 12) Caster/Minion clusters

Caster-focused:

```csharp
new [] {
  ItemStats[GameStat.SpellSkillGemLevel] >= 1,
  ItemStats[GameStat.BaseCastSpeedPct] >= 17,
  ItemStats[GameStat.SpellDamagePct] >= 25,
  ItemStats[GameStat.LightningSpellSkillGemLevel] >= 2,
  ItemStats[GameStat.FireSpellSkillGemLevel] >= 2,
}.Count(x => x) >= 2
```

Minion-focused:

```csharp
new [] {
  ItemStats[GameStat.MinionSkillGemLevel] >= 2,
  ItemStats[GameStat.BaseSpiritFromEquipment] >= 5,
}.Count(x => x) >= 1
```

---

### 13) Summing resistances with SumItemStats

This plugin exposes a helper to sum numeric stats without arrays. Call it with arguments, not an array literal:

```csharp
SumItemStats(
  ItemStats[GameStat.BaseFireDamageResistancePct],
  ItemStats[GameStat.BaseColdDamageResistancePct],
  ItemStats[GameStat.BaseLightningDamageResistancePct],
  ItemStats[GameStat.BaseChaosDamageResistancePct]
) >= 45
```

Notes:
- Do not use `.Value` on `ItemStats[...]`; pass them directly.
- If your environment does not expose `SumItemStats`, approximate with N-of-M thresholds:

```csharp
new [] {
  ItemStats[GameStat.BaseFireDamageResistancePct] >= 15,
  ItemStats[GameStat.BaseColdDamageResistancePct] >= 15,
  ItemStats[GameStat.BaseLightningDamageResistancePct] >= 15,
}.Count(x => x) >= 2
```

---

### 14) Vendor/NPC contexts (if supported)

If your host supports applying IFL to vendor/NPC inventories, the same patterns work. Common “money”/value filters:

```csharp
ItemQuality >= 1
|| BaseName == "Gold Ring"
|| BaseName == "Prismatic Ring"
|| BaseName == "Amethyst Ring"
|| BaseName == "Gold Amulet"
|| BaseName == "Stellar Amulet"
|| BaseName == "Utility Belt"
|| BaseName == "Sapphire Ring"
```

Sockets are also useful in NPC contexts:

```csharp
SocketInfo.SocketNumber >= 1
```

Caveat: This `InvWithLinq` plugin highlights Inventory and Stash. Vendor/NPC usage depends on the host plugin; the rule syntax itself remains the same.
