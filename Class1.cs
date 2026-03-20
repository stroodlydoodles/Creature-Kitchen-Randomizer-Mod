using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CreatureKitchenAP;

[BepInPlugin("CreatureKitchenRando", "Creature Kitchen Randomizer", "0.2.0")]
public class Plugin : BasePlugin
{
    public const string MOD_NAME = "Creature Kitchen Randomizer";
    public const string VERSION = "0.2.0";

    internal static new BepInEx.Logging.ManualLogSource Log = null!;
    internal static CardRandomizer Randomizer = null!;
    internal static KeyRandomizer KeyRando = null!;
    internal static IngredientRandomizer IngredientRando = null!;
    internal static bool IsNewGame = false;
    internal static bool IsResumeAsNewGame = false;

    internal static ConfigEntry<bool> EnableCardShuffle = null!;
    internal static ConfigEntry<bool> EnableMistakeSystem = null!;
    internal static ConfigEntry<bool> EnableKeyShuffle = null!;
    internal static ConfigEntry<bool> EnablePantryShuffle = null!;
    internal static ConfigEntry<bool> EnableIngredientShuffle = null!;
    internal static ConfigEntry<int> SeedConfig = null!;

    internal static int ActiveSeed = 0;
    internal static string SeedPath = null!;

    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"{MOD_NAME} v{VERSION} loaded!");

        EnableCardShuffle = Config.Bind("Shuffling", "EnableCardShuffle", true, "Shuffle recipe cards across locations.");
        EnableMistakeSystem = Config.Bind("Shuffling", "EnableMistakeSystem", true, "Force Mistake when cooking uncollected recipe.");
        EnableKeyShuffle = Config.Bind("Shuffling", "EnableKeyShuffle", true, "Shuffle which key each creature rewards. This doesn't include Pantry, Fridge or either Crest half.");
        EnablePantryShuffle = Config.Bind("Shuffling", "EnablePantryShuffle", false, "Include Pantry, Fridge keys, and both Crest Halves in shuffle pool (Hard Mode). Starting table key becomes a random key. Requires EnableKeyShuffle.");
        EnableIngredientShuffle = Config.Bind("Shuffling", "EnableIngredientShuffle", false, "Shuffle finite ingredient locations across the map. Excludes all Mushrooms, Bread, and Water.");
        SeedConfig = Config.Bind("Shuffling", "Seed", 0, "Seed for randomization (0 = random seed each new game). Same seed always produces the same shuffle.");

        Log.LogInfo($"Config: CardShuffle={EnableCardShuffle.Value}, MistakeSystem={EnableMistakeSystem.Value}, KeyShuffle={EnableKeyShuffle.Value}, PantryShuffle={EnablePantryShuffle.Value}, IngredientShuffle={EnableIngredientShuffle.Value}, Seed={SeedConfig.Value}");

        string mappingPath = Path.Combine(Paths.PluginPath, "CK_AP_CardMapping.json");
        string collectedPath = Path.Combine(Paths.PluginPath, "CK_AP_CollectedRecipes.json");
        string keyMappingPath = Path.Combine(Paths.PluginPath, "CK_AP_KeyMapping.json");
        string ingredientMappingPath = Path.Combine(Paths.PluginPath, "CK_AP_IngredientMapping.json");
        SeedPath = Path.Combine(Paths.PluginPath, "CK_AP_Seed.txt");
        Randomizer = new CardRandomizer(mappingPath, collectedPath);
        KeyRando = new KeyRandomizer(keyMappingPath);
        IngredientRando = new IngredientRandomizer(ingredientMappingPath);

        var harmony = new Harmony("CreatureKitchenRando");
        harmony.PatchAll();
        Log.LogInfo("Patches applied!");
    }
}

// =======================================================================
// Card Randomizer
// =======================================================================
public class CardRandomizer
{
    private Dictionary<string, string> _vanillaToShuffled = new();
    private HashSet<string> _collectedRecipes = new();
    private HashSet<string> _defaultRecipes = new();

    private string _savePath;
    private string _collectedPath;
    private bool _initialized = false;
    private bool _defaultsLoaded = false;
    private System.Random _rng = new();

    public CardRandomizer(string savePath, string collectedPath)
    {
        _savePath = savePath;
        _collectedPath = collectedPath;
    }

    public void SetRng(System.Random rng) { _rng = rng; }

    public void PrepareForNewGame()
    {
        _vanillaToShuffled.Clear();
        _collectedRecipes.Clear();
        _initialized = false;
        _defaultsLoaded = false;

        if (File.Exists(_savePath)) File.Delete(_savePath);
        if (File.Exists(_collectedPath)) File.Delete(_collectedPath);
        if (File.Exists(Plugin.SeedPath)) File.Delete(Plugin.SeedPath);
        string spoilerPath = Path.Combine(BepInEx.Paths.PluginPath, "CK_AP_SpoilerLog.txt");
        if (File.Exists(spoilerPath)) File.Delete(spoilerPath);

        Plugin.Log.LogInfo("Card randomizer reset for new game.");
    }

    public void Initialize()
    {
        if (_initialized) return;

        if (File.Exists(_savePath) && LoadMapping())
        {
            _initialized = true;
            return;
        }

        Plugin.Log.LogInfo("CARD INIT: Generating logic-validated card shuffle...");

        // Retry loop: if a key shuffle leads to an impossible card shuffle,
        // regenerate both. The shared RNG keeps advancing so each attempt
        // is different but deterministic from the seed.
        Dictionary<string, string> mapping = null;
        int keyRetries = 0;
        const int MAX_KEY_RETRIES = 30;

        while (mapping == null && keyRetries < MAX_KEY_RETRIES)
        {
            if (keyRetries > 0)
            {
                Plugin.Log.LogInfo($"CARD INIT: Retrying with new key+ingredient shuffle (attempt {keyRetries + 1}/{MAX_KEY_RETRIES})...");
                Plugin.KeyRando.ResetForRetry();
                Plugin.IngredientRando.ResetForRetry();
            }

            Plugin.KeyRando.Initialize();
            Plugin.IngredientRando.Initialize();
            mapping = RecipeLogic.GenerateCardShuffle(Plugin.KeyRando, _rng);
            keyRetries++;
        }

        if (mapping != null)
        {
            _vanillaToShuffled = mapping;
            Plugin.Log.LogInfo($"CARD SHUFFLE: Generated valid mapping with {mapping.Count} entries.{(keyRetries > 1 ? $" (after {keyRetries} key shuffle attempt(s))" : "")}");
            foreach (var kvp in mapping)
            {
                if (kvp.Key != kvp.Value)
                    Plugin.Log.LogInfo($"  CARD: {kvp.Key} → {kvp.Value}");
            }

            // Generate spoiler log
            var stages = Plugin.KeyRando.GetBFSStages();
            RecipeLogic.GenerateSpoilerLog(Plugin.KeyRando, mapping, stages);
        }
        else
            Plugin.Log.LogError($"CARD SHUFFLE: Failed after {keyRetries} key shuffle attempts! Cards will not be shuffled.");

        _initialized = true;
        SaveMapping();
    }

    private void EnsureDefaultsLoaded()
    {
        if (_defaultsLoaded) return;
        var allCardData = Resources.FindObjectsOfTypeAll<RecipeCardData>();
        if (allCardData == null || allCardData.Length == 0) return;

        _defaultRecipes.Clear();
        foreach (var card in allCardData)
        {
            if (card.m_bObtainedByDefault)
            {
                _defaultRecipes.Add(card.GetRecipeTitle());
                var recipeData = card.GetRecipeDataFromCard();
                if (recipeData != null)
                {
                    var foodData = recipeData.GetFoodData();
                    if (foodData != null) _defaultRecipes.Add(foodData.name);
                }
            }
        }
        _defaultsLoaded = true;
    }

    public RecipeCardData GetCardForLocation(RecipeCard cardInstance)
    {
        Initialize();

        var vanillaCard = cardInstance.m_RecipeCardUnlocked;
        if (vanillaCard == null) return null;

        string vanillaTitle = vanillaCard.GetRecipeTitle();

        if (_vanillaToShuffled.TryGetValue(vanillaTitle, out string shuffledTitle))
        {
            if (shuffledTitle == vanillaTitle) return null;

            var allCards = Resources.FindObjectsOfTypeAll<RecipeCardData>();
            var newCard = allCards?.FirstOrDefault(c => c.GetRecipeTitle() == shuffledTitle);
            if (newCard != null) return newCard;

            Plugin.Log.LogWarning($"CARD: Could not find RecipeCardData for '{shuffledTitle}'");
        }

        return null;
    }

    public void CollectRecipe(string recipeTitle)
    {
        if (_collectedRecipes.Add(recipeTitle))
        {
            Plugin.Log.LogInfo($"COLLECTED: {recipeTitle} ({_collectedRecipes.Count} total)");
            SaveCollected();
        }
    }

    public bool IsRecipeCollected(string recipeTitle)
    {
        EnsureDefaultsLoaded();
        if (_defaultRecipes.Contains(recipeTitle)) return true;
        return _collectedRecipes.Contains(recipeTitle);
    }

    public int CollectedCount => _collectedRecipes.Count;

    public void SaveMapping()
    {
        try
        {
            var lines = new List<string> { "{" };
            var entries = _vanillaToShuffled.ToList();
            for (int i = 0; i < entries.Count; i++)
            {
                string comma = i < entries.Count - 1 ? "," : "";
                lines.Add($"  \"{entries[i].Key}\": \"{entries[i].Value}\"{comma}");
            }
            lines.Add("}");
            File.WriteAllLines(_savePath, lines);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Failed to save card mapping: {ex.Message}"); }
    }

    public bool LoadMapping()
    {
        try
        {
            if (!File.Exists(_savePath)) return false;
            string json = File.ReadAllText(_savePath);
            _vanillaToShuffled.Clear();
            foreach (var line in json.Split('\n'))
            {
                var trimmed = line.Trim().TrimEnd(',');
                if (trimmed.StartsWith("\""))
                {
                    int ci = trimmed.IndexOf(':');
                    if (ci > 0)
                    {
                        string key = trimmed.Substring(0, ci).Trim().Trim('"');
                        string value = trimmed.Substring(ci + 1).Trim().Trim('"');
                        _vanillaToShuffled[key] = value;
                    }
                }
            }
            Plugin.Log.LogInfo($"CARD INIT: Loaded existing mapping with {_vanillaToShuffled.Count} entries.");
            return _vanillaToShuffled.Count > 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Failed to load card mapping: {ex.Message}");
            return false;
        }
    }

    private void SaveCollected()
    {
        try
        {
            var lines = new List<string> { "[" };
            var entries = _collectedRecipes.ToList();
            for (int i = 0; i < entries.Count; i++)
            {
                string comma = i < entries.Count - 1 ? "," : "";
                lines.Add($"  \"{entries[i]}\"{comma}");
            }
            lines.Add("]");
            File.WriteAllLines(_collectedPath, lines);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Failed to save collected: {ex.Message}"); }
    }

    public void LoadCollected()
    {
        try
        {
            if (!File.Exists(_collectedPath)) return;
            string json = File.ReadAllText(_collectedPath);
            _collectedRecipes.Clear();
            foreach (var line in json.Split('\n'))
            {
                var trimmed = line.Trim().TrimEnd(',');
                if (trimmed.StartsWith("\""))
                {
                    string value = trimmed.Trim('"');
                    if (!string.IsNullOrEmpty(value)) _collectedRecipes.Add(value);
                }
            }
            Plugin.Log.LogInfo($"Loaded {_collectedRecipes.Count} collected recipes.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"Failed to load collected: {ex.Message}"); }
    }

    public bool HasMapping => _vanillaToShuffled.Count > 0;
}

// =======================================================================
// Key Randomizer with Logic Validation
// =======================================================================
public class KeyRandomizer
{
    private Dictionary<string, string> _creatureToKeyId = new();
    private Dictionary<int, int> _lockIdRemap = new();

    private string _savePath;
    private bool _initialized = false;
    private System.Random _rng = new();

    private int _startingTableKey = -1;
    private bool _startingKeySwapped = false;
    private int _shufflePhase = 0;

    public int ShufflePhase => _shufflePhase;

    public static HashSet<int> AlreadyRemappedKeys = new();

    // Door Lock IDs
    public const int BEDROOM = 1;
    public const int BATHROOM = 2;
    public const int ODDITIES_ROOM = 3;
    public const int PANTRY = 4;
    public const int FRIDGE = 5;
    public const int FREEZER = 6;
    public const int FRONT_DOOR = 7;
    public const int CHEESE_CABINET = 8;
    public const int SHED = 9;
    public const int MUG_CABINET = 10;
    public const int BOWL_CABINET = 11;
    public const int CEREAL = 12;

    // Special item IDs (not real DoorLockIDs)
    public const int CREST_LEFT = 100;
    public const int CREST_RIGHT = 101;

    // Progressive crest system: first crest configured is always LEFT, second is RIGHT.
    // Prevents duplicate-side bugs where the game's DelayHiding hides a crest whose side
    // was already placed in the door.
    public static int ProgressiveCrestCount = 0;
    public static CrestHalf.CrestSide NextCrestSide()
    {
        var side = ProgressiveCrestCount == 0 ? CrestHalf.CrestSide.LEFT : CrestHalf.CrestSide.RIGHT;
        ProgressiveCrestCount++;
        Plugin.Log.LogInfo($"PROGRESSIVE CREST: Assigned {side} (count now {ProgressiveCrestCount})");
        return side;
    }

    // Creature IDs
    public const int RACCOON = 1;
    public const int MOTH = 3;
    public const int RAT = 5;
    public const int GREY = 6;
    public const int SASSFOOT = 7;
    public const int TREE_OCTOPUS = 8;
    public const int GOOBER = 9;
    public const int CROW = 12;
    public const int FROG = 13;
    public const int JAKE = 14;

    // Slot keys for the mapping dictionary
    public const string SLOT_TABLE = "starting_table";
    public const string SLOT_CROW = "crow";
    public const string SLOT_ODDITIES_PUZZLE = "oddities_puzzle";
    public const string SLOT_MOTH = "moth";

    public static readonly Dictionary<int, int> KnownOriginalKeys = new()
    {
        { RACCOON,       CHEESE_CABINET },
        { RAT,           BEDROOM },
        { GREY,          BATHROOM },
        { SASSFOOT,      BOWL_CABINET },
        { TREE_OCTOPUS,  ODDITIES_ROOM },
        { GOOBER,        SHED },
        { FROG,          FREEZER },
        { JAKE,          MUG_CABINET },
    };

    private static readonly Dictionary<int, HashSet<int>> CreatureRequirements = new()
    {
        { FROG,          new HashSet<int> { FRIDGE } },
        { RACCOON,       new HashSet<int> { FRIDGE } },
        { RAT,           new HashSet<int> { FRIDGE, CHEESE_CABINET } },
        { GOOBER,        new HashSet<int> { FRIDGE, CHEESE_CABINET, PANTRY } },
        { TREE_OCTOPUS,  new HashSet<int> { FRIDGE, BATHROOM } },
        { GREY,          new HashSet<int> { FRIDGE, SHED } },
        { JAKE,          new HashSet<int> { FRIDGE, BEDROOM, FREEZER } },
        { SASSFOOT,      new HashSet<int> { FRIDGE, BEDROOM, MUG_CABINET } },
        { MOTH,          new HashSet<int> { ODDITIES_ROOM, SHED } },
    };

    // Additional creature requirements when Pantry is in the shuffle pool.
    // Jake: needs 3 Sugar (1 Pie + 1 Cookie + 1 Ice Cream), only 2 in Kitchen.
    // Grey: ALL Beef recipes need either Pantry (Salt×2) or Cheese Cabinet. No Beef cookable without one.
    // Hopper: 2 of 3 Yard recipes need Pantry (Mushroom Soup) or Cheese (Omelet). Only 1 without.
    private static readonly Dictionary<int, HashSet<int>> PantryExtraRequirements = new()
    {
        { JAKE, new HashSet<int> { PANTRY } },
        { GREY, new HashSet<int> { CHEESE_CABINET } },
        { FROG, new HashSet<int> { CHEESE_CABINET } },
    };

    private HashSet<int> GetEffectiveRequirements(int creature)
    {
        var reqs = CreatureRequirements.ContainsKey(creature)
            ? new HashSet<int>(CreatureRequirements[creature])
            : new HashSet<int>();

        if (Plugin.EnablePantryShuffle.Value && Plugin.EnableKeyShuffle.Value
            && PantryExtraRequirements.TryGetValue(creature, out var extra))
        {
            reqs.UnionWith(extra);
        }

        // When ingredient shuffle is active, adjust requirements based on
        // where ingredients actually are instead of vanilla locations.
        if (Plugin.EnableIngredientShuffle.Value
            && Plugin.IngredientRando != null
            && Plugin.IngredientRando.HasMapping)
        {
            // Tree Octopus: BATHROOM requirement is for Fish summoning.
            // Replace with whatever door Fish (type 7) is actually behind.
            if (creature == TREE_OCTOPUS)
            {
                reqs.Remove(BATHROOM);
                int fishDoor = GetShuffledIngredientDoor(7); // Fish = type 7
                if (fishDoor > 0) reqs.Add(fishDoor);
                // fishDoor == 0 means Kitchen (no door needed), so don't add anything
            }
        }

        return reqs;
    }

    /// <summary>
    /// Find which door a shuffled ingredient type is behind.
    /// Returns 0 if the ingredient is in Kitchen/Outside (no door needed).
    /// Returns the first accessible door if multiple copies exist.
    /// </summary>
    private static int GetShuffledIngredientDoor(int ingredientType)
    {
        if (Plugin.IngredientRando == null || !Plugin.IngredientRando.HasMapping) return -1;
        var shuffled = Plugin.IngredientRando.GetShuffled();
        if (shuffled == null) return -1;

        for (int i = 0; i < IngredientRandomizer.TOTAL_SLOTS; i++)
        {
            if (shuffled[i] == ingredientType)
                return IngredientRandomizer.Slots[i].Door;
        }

        // Check pinned items
        if (ingredientType == 19) return 9;  // Rose → Shed
        if (ingredientType == 23) return 12; // Cereal → Cereal Cabinet

        return -1;
    }

    private static readonly int[] ShufflableKeys = {
        BEDROOM, BATHROOM, ODDITIES_ROOM, FREEZER,
        CHEESE_CABINET, SHED, MUG_CABINET, BOWL_CABINET
    };

    private static readonly int[] ShufflableCreatures = {
        RACCOON, RAT, GREY, SASSFOOT,
        TREE_OCTOPUS, GOOBER, FROG, JAKE
    };

    // All creatures participating in BFS (includes Moth)
    private static readonly int[] AllBFSCreatures = {
        RACCOON, RAT, GREY, SASSFOOT,
        TREE_OCTOPUS, GOOBER, FROG, JAKE, MOTH
    };

    /// <summary>Maps creature ID to its slot key in the mapping dictionary.</summary>
    public static string SlotKey(int creature) => creature == MOTH ? SLOT_MOTH : creature.ToString();

    // Full shuffle: 12 items across 12 slots
    private static readonly int[] FullShuffleItems = {
        BEDROOM, BATHROOM, ODDITIES_ROOM, FREEZER,
        CHEESE_CABINET, SHED, MUG_CABINET, BOWL_CABINET,
        PANTRY, FRIDGE, CREST_LEFT, CREST_RIGHT
    };

    /// <summary>Returns true if the item opens a door (is a key, not a crest half).</summary>
    public static bool IsKey(int item) => item >= 1 && item <= 12;

    /// <summary>Returns the item name for logging.</summary>
    public static string ItemName(int item)
    {
        if (item == CREST_LEFT) return "Crest (Left)";
        if (item == CREST_RIGHT) return "Crest (Right)";
        return DoorNames.TryGetValue(item, out var n) ? $"{n} Key" : $"Item{item}";
    }

    public KeyRandomizer(string savePath) { _savePath = savePath; }

    public void SetRng(System.Random rng) { _rng = rng; }

    public int GetStartingTableKey() => _startingTableKey;

    public int GetRemappedLock(int originalLock)
    {
        return _lockIdRemap.TryGetValue(originalLock, out int remapped) ? remapped : -1;
    }

    public List<(HashSet<int> doorsBefore, List<int> newCreatures)> GetBFSStages()
    {
        return ComputeBFSStages();
    }

    /// <summary>Get the item assigned to a slot (creature ID string, or special slot key).</summary>
    public int GetSlotItem(string slotKey)
    {
        if (_creatureToKeyId.TryGetValue(slotKey, out string s) && int.TryParse(s, out int v))
            return v;
        return -1;
    }

    public int GetSlotItem(int creatureId) => GetSlotItem(SlotKey(creatureId));

    /// <summary>
    /// Lightweight reset for retry — clears mapping so Initialize() will
    /// generate a new shuffle, but preserves the RNG stream for determinism.
    /// </summary>
    public void ResetForRetry()
    {
        _creatureToKeyId.Clear();
        _lockIdRemap.Clear();
        _startingTableKey = -1;
        _startingKeySwapped = false;
        _shufflePhase = 0;
        _initialized = false;
        if (File.Exists(_savePath)) File.Delete(_savePath);
    }

    public void PrepareForNewGame()
    {
        _creatureToKeyId.Clear();
        _lockIdRemap.Clear();
        _initialized = false;
        _startingTableKey = -1;
        _startingKeySwapped = false;
        _shufflePhase = 0;
        AlreadyRemappedKeys.Clear();
        ProgressiveCrestCount = 0;
        if (File.Exists(_savePath)) File.Delete(_savePath);
        Plugin.Log.LogInfo("Key randomizer reset for new game.");
    }

    public static int IdentifyCreatureFromGifter(CreatureRewardGifter gifter)
    {
        var navigator = gifter.GetComponent<CreatureNavigator>();
        if (navigator == null) navigator = gifter.GetComponentInParent<CreatureNavigator>();
        if (navigator != null) return (int)navigator.GetCreatureType();

        var camTarget = gifter.GetComponent<CameraTargetDetection>();
        if (camTarget == null) camTarget = gifter.GetComponentInParent<CameraTargetDetection>();
        if (camTarget != null) return (int)camTarget.GetCameraTargetID();

        return -1;
    }

    private bool ValidateShuffle(Dictionary<string, int> slotToItem, bool fullShuffle)
    {
        var availableDoors = new HashSet<int> { FRONT_DOOR };
        var obtainedCrests = new HashSet<int>(); // Track CREST_LEFT / CREST_RIGHT

        if (fullShuffle)
        {
            int tableItem = slotToItem[SLOT_TABLE];
            int crowItem = slotToItem[SLOT_CROW];
            if (IsKey(tableItem)) availableDoors.Add(tableItem);
            else if (tableItem == CREST_LEFT || tableItem == CREST_RIGHT) obtainedCrests.Add(tableItem);
            if (IsKey(crowItem)) availableDoors.Add(crowItem);
            else if (crowItem == CREST_LEFT || crowItem == CREST_RIGHT) obtainedCrests.Add(crowItem);
        }
        else
        {
            availableDoors.Add(FRIDGE);
            int tableItem = slotToItem.ContainsKey(SLOT_TABLE) ? slotToItem[SLOT_TABLE] : PANTRY;
            if (IsKey(tableItem)) availableDoors.Add(tableItem);
            else availableDoors.Add(PANTRY);
        }

        var satisfied = new HashSet<int>();
        bool odditiesPuzzleAdded = false;
        bool changed = true;

        while (changed)
        {
            changed = false;

            // Check if Oddities Room just became accessible -> add puzzle item
            if (fullShuffle && !odditiesPuzzleAdded && availableDoors.Contains(ODDITIES_ROOM))
            {
                odditiesPuzzleAdded = true;
                int puzzleItem = slotToItem[SLOT_ODDITIES_PUZZLE];
                if (IsKey(puzzleItem) && !availableDoors.Contains(puzzleItem))
                {
                    availableDoors.Add(puzzleItem);
                    changed = true;
                    Plugin.Log.LogInfo($"LOGIC CHECK: Oddities puzzle -> {ItemName(puzzleItem)}");
                }
                else if (puzzleItem == CREST_LEFT || puzzleItem == CREST_RIGHT)
                {
                    if (obtainedCrests.Add(puzzleItem)) changed = true;
                }
            }

            foreach (int creature in AllBFSCreatures)
            {
                if (satisfied.Contains(creature)) continue;
                if (GetEffectiveRequirements(creature).IsSubsetOf(availableDoors))
                {
                    satisfied.Add(creature);
                    string slotKey = SlotKey(creature);
                    if (slotToItem.TryGetValue(slotKey, out int item))
                    {
                        if (IsKey(item)) availableDoors.Add(item);
                        else if (item == CREST_LEFT || item == CREST_RIGHT)
                        {
                            if (obtainedCrests.Add(item)) changed = true;
                        }
                        Plugin.Log.LogInfo($"LOGIC CHECK: Creature {creature} reachable -> gives {ItemName(item)}");
                    }
                    else
                    {
                        // Creature not in shuffle (e.g. Moth in basic mode) — no key contribution
                        Plugin.Log.LogInfo($"LOGIC CHECK: Creature {creature} reachable (not in shuffle pool)");
                    }
                    changed = true;
                }
            }
        }

        if (satisfied.Count < AllBFSCreatures.Length)
        {
            var unreachable = AllBFSCreatures.Where(c => !satisfied.Contains(c));
            Plugin.Log.LogInfo($"LOGIC CHECK: INVALID -- unreachable: {string.Join(", ", unreachable)}");
            return false;
        }

        Plugin.Log.LogInfo("LOGIC CHECK: All creatures reachable! Valid.");
        return true;
    }

    /// <summary>
    /// Validate crest constraint: both crests can't both be obtainable
    /// before at least 5 creatures have been fed.
    /// </summary>
    private bool ValidateCrestConstraint(Dictionary<string, int> slotToItem)
    {
        var availableDoors = new HashSet<int> { FRONT_DOOR };
        var obtainedCrests = new HashSet<int>();
        int creaturesFed = 0;

        int tableItem = slotToItem[SLOT_TABLE];
        int crowItem = slotToItem[SLOT_CROW];
        if (IsKey(tableItem)) availableDoors.Add(tableItem);
        else if (tableItem == CREST_LEFT || tableItem == CREST_RIGHT) obtainedCrests.Add(tableItem);
        if (IsKey(crowItem)) availableDoors.Add(crowItem);
        else if (crowItem == CREST_LEFT || crowItem == CREST_RIGHT) obtainedCrests.Add(crowItem);

        // Crow counts as 1 creature fed
        creaturesFed = 1;

        // Check immediately: table + crow give both crests at 1 creature
        if (obtainedCrests.Count >= 2 && creaturesFed < 5) goto Reject;

        var satisfied = new HashSet<int>();
        bool odditiesPuzzleAdded = false;
        bool changed = true;

        while (changed)
        {
            changed = false;
            if (!odditiesPuzzleAdded && availableDoors.Contains(ODDITIES_ROOM))
            {
                odditiesPuzzleAdded = true;
                int puzzleItem = slotToItem[SLOT_ODDITIES_PUZZLE];
                if (IsKey(puzzleItem)) availableDoors.Add(puzzleItem);
                else if (puzzleItem == CREST_LEFT || puzzleItem == CREST_RIGHT) obtainedCrests.Add(puzzleItem);
                if (obtainedCrests.Count >= 2 && creaturesFed < 5) goto Reject;
            }
            foreach (int creature in AllBFSCreatures)
            {
                if (satisfied.Contains(creature)) continue;
                if (GetEffectiveRequirements(creature).IsSubsetOf(availableDoors))
                {
                    satisfied.Add(creature);
                    creaturesFed++;
                    if (slotToItem.TryGetValue(SlotKey(creature), out int item))
                    {
                        if (IsKey(item)) availableDoors.Add(item);
                        else if (item == CREST_LEFT || item == CREST_RIGHT) obtainedCrests.Add(item);
                    }
                    if (obtainedCrests.Count >= 2 && creaturesFed < 5) goto Reject;
                    changed = true;
                }
            }
        }

        return true;

    Reject:
        Plugin.Log.LogInfo($"CREST CHECK: Both crests obtainable with only {creaturesFed} creatures fed. Rejecting.");
        return false;
    }

    // ---------------------------------------------------------------
    // BFS progression stages — returns doors available BEFORE each
    // batch of creatures needs to be satisfied.
    //
    // FIX: Previously returned doors AFTER adding new keys, which
    // let the validator think ingredients behind those new doors
    // were already available. Now we snapshot doors BEFORE each
    // round, which is what the player actually has when trying to
    // cook for these creatures.
    // ---------------------------------------------------------------
    public List<(HashSet<int> doorsBefore, List<int> newCreatures)> ComputeBFSStages()
    {
        var stages = new List<(HashSet<int> doorsBefore, List<int> newCreatures)>();
        bool fullShuffle = Plugin.EnablePantryShuffle.Value && Plugin.EnableKeyShuffle.Value;
        var availableDoors = new HashSet<int> { FRONT_DOOR };
        var obtainedCrests = new HashSet<int>();

        if (fullShuffle)
        {
            int tableItem = GetSlotItem(SLOT_TABLE);
            int crowItem = GetSlotItem(SLOT_CROW);
            if (tableItem > 0 && IsKey(tableItem)) availableDoors.Add(tableItem);
            else if (tableItem == CREST_LEFT || tableItem == CREST_RIGHT) obtainedCrests.Add(tableItem);
            if (crowItem > 0 && IsKey(crowItem)) availableDoors.Add(crowItem);
            else if (crowItem == CREST_LEFT || crowItem == CREST_RIGHT) obtainedCrests.Add(crowItem);
        }
        else
        {
            availableDoors.Add(FRIDGE);
            if (_startingTableKey > 0)
                availableDoors.Add(_startingTableKey);
            else
                availableDoors.Add(PANTRY);
        }

        var satisfied = new HashSet<int>();
        bool odditiesPuzzleAdded = false;
        bool changed = true;

        while (changed)
        {
            changed = false;

            // Check Oddities puzzle mid-BFS
            if (fullShuffle && !odditiesPuzzleAdded && availableDoors.Contains(ODDITIES_ROOM))
            {
                odditiesPuzzleAdded = true;
                int puzzleItem = GetSlotItem(SLOT_ODDITIES_PUZZLE);
                if (puzzleItem > 0 && IsKey(puzzleItem) && !availableDoors.Contains(puzzleItem))
                {
                    availableDoors.Add(puzzleItem);
                    changed = true;
                }
                else if (puzzleItem == CREST_LEFT || puzzleItem == CREST_RIGHT)
                {
                    if (obtainedCrests.Add(puzzleItem)) changed = true;
                }
            }

            var doorsBefore = new HashSet<int>(availableDoors);
            var newThisRound = new List<int>();

            foreach (int creature in AllBFSCreatures)
            {
                if (satisfied.Contains(creature)) continue;
                if (GetEffectiveRequirements(creature).IsSubsetOf(availableDoors))
                {
                    satisfied.Add(creature);
                    newThisRound.Add(creature);
                    changed = true;
                }
            }

            if (newThisRound.Count > 0)
            {
                stages.Add((doorsBefore, new List<int>(newThisRound)));
                foreach (int c in newThisRound)
                {
                    int item = GetSlotItem(c);
                    if (item > 0 && IsKey(item))
                        availableDoors.Add(item);
                    else if (item == CREST_LEFT || item == CREST_RIGHT)
                        obtainedCrests.Add(item);
                }
            }
        }

        return stages;
    }

    public void Initialize()
    {
        if (_initialized) return;

        if (File.Exists(_savePath) && LoadMapping())
        {
            BuildLockRemap();
            Plugin.Log.LogInfo($"KEY INIT: Loaded existing mapping with {_creatureToKeyId.Count} entries.");
            try
            {
                if (File.Exists(Plugin.SeedPath))
                {
                    string seedStr = File.ReadAllText(Plugin.SeedPath).Trim();
                    if (int.TryParse(seedStr, out int loadedSeed))
                    {
                        Plugin.ActiveSeed = loadedSeed;
                        Plugin.Log.LogInfo($"========================================");
                        Plugin.Log.LogInfo($"  SEED (loaded): {loadedSeed}");
                        Plugin.Log.LogInfo($"========================================");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError($"Failed to load seed: {ex.Message}"); }
            _initialized = true;
            return;
        }

        bool fullShuffle = Plugin.EnablePantryShuffle.Value;
        Plugin.Log.LogInfo($"KEY INIT: Generating logic-validated shuffle (fullShuffle={fullShuffle})...");

        if (fullShuffle)
            InitializeFullShuffle();
        else
            InitializeBasicShuffle();

        BuildLockRemap();
        _initialized = true;
        SaveMapping();
    }

    /// <summary>Basic key shuffle: 8 creatures get 8 keys. Fridge always on Crow.</summary>
    private void InitializeBasicShuffle()
    {
        _creatureToKeyId[CROW.ToString()] = FRIDGE.ToString();
        Plugin.Log.LogInfo($"KEY SHUFFLE: Creature {CROW} (Crow) -> Fridge Key [LOCKED]");

        var keyPool = new List<int>(ShufflableKeys);
        int attempts = 0;

        while (attempts < 1000)
        {
            attempts++;
            var shuffled = keyPool.OrderBy(_ => _rng.Next()).ToList();
            var candidate = new Dictionary<string, int>();
            for (int i = 0; i < ShufflableCreatures.Length; i++)
                candidate[ShufflableCreatures[i].ToString()] = shuffled[i];
            candidate[SLOT_CROW] = FRIDGE;

            if (ValidateShuffle(candidate, false))
            {
                foreach (var kvp in candidate)
                {
                    _creatureToKeyId[kvp.Key] = kvp.Value.ToString();
                    if (int.TryParse(kvp.Key, out int cId) && KnownOriginalKeys.ContainsKey(cId))
                        Plugin.Log.LogInfo($"KEY SHUFFLE: Creature {cId} -> {ItemName(kvp.Value)} (was {ItemName(KnownOriginalKeys[cId])})");
                }
                Plugin.Log.LogInfo($"KEY SHUFFLE: Valid in {attempts} attempt(s).");
                return;
            }
        }

        Plugin.Log.LogError("KEY SHUFFLE: Fallback to vanilla.");
        foreach (var kvp in KnownOriginalKeys)
            _creatureToKeyId[kvp.Key.ToString()] = kvp.Value.ToString();
        _creatureToKeyId[CROW.ToString()] = FRIDGE.ToString();
    }

    /// <summary>Full 12-item shuffle: keys + Pantry + Fridge + 2 crest halves across all slots.</summary>
    private void InitializeFullShuffle()
    {
        // Phase 1 (200):  Pantry on Table/Crow, Fridge deeper (Moth gateway seeds)
        // Phase 2 (1000): Fridge on Table/Crow, Pantry deeper (standard interesting seeds)
        // Phase 3 (500):  Fridge OR Pantry on Table/Crow, no restrictions (fallback)
        int totalAttempts = 0;

        // Phase 1: Pantry start, Fridge buried
        for (int a = 0; a < 200; a++)
        {
            totalAttempts++;
            var candidate = BuildCandidate(pinnedKey: PANTRY, blockedKey: FRIDGE);
            if (candidate != null && ValidateShuffle(candidate, true) && ValidateCrestConstraint(candidate))
            {
                AcceptCandidate(candidate, 1, totalAttempts);
                return;
            }
        }
        Plugin.Log.LogInfo($"KEY SHUFFLE: Phase 1 (Pantry start) exhausted after {totalAttempts} attempts.");

        // Phase 2: Fridge start, Pantry buried
        for (int a = 0; a < 1000; a++)
        {
            totalAttempts++;
            var candidate = BuildCandidate(pinnedKey: FRIDGE, blockedKey: PANTRY);
            if (candidate != null && ValidateShuffle(candidate, true) && ValidateCrestConstraint(candidate))
            {
                AcceptCandidate(candidate, 2, totalAttempts);
                return;
            }
        }
        Plugin.Log.LogInfo($"KEY SHUFFLE: Phase 2 (Fridge start, no Pantry) exhausted after {totalAttempts} attempts.");

        // Phase 3: Fridge or Pantry start, no restrictions
        for (int a = 0; a < 500; a++)
        {
            totalAttempts++;
            int pinned = _rng.Next(2) == 0 ? FRIDGE : PANTRY;
            var candidate = BuildCandidate(pinnedKey: pinned, blockedKey: -1);
            if (candidate != null && ValidateShuffle(candidate, true) && ValidateCrestConstraint(candidate))
            {
                AcceptCandidate(candidate, 3, totalAttempts);
                return;
            }
        }

        Plugin.Log.LogError("KEY SHUFFLE (full): Failed all attempts! Falling back to basic shuffle.");
        InitializeBasicShuffle();
    }

    /// <summary>
    /// Build a candidate layout. pinnedKey goes on Table or Crow (50/50).
    /// blockedKey (if >= 0) is prevented from the other starting slot.
    /// </summary>
    private Dictionary<string, int> BuildCandidate(int pinnedKey, int blockedKey)
    {
        var items = new List<int>(FullShuffleItems);
        items.Remove(pinnedKey);

        bool pinnedOnTable = _rng.Next(2) == 0;
        var candidate = new Dictionary<string, int>();
        var pool = items.OrderBy(_ => _rng.Next()).ToList();

        // The "other" slot is position 0 in pool. Block if needed.
        if (blockedKey >= 0)
        {
            int blockedIdx = pool.IndexOf(blockedKey);
            if (blockedIdx == 0 && pool.Count > 2)
            {
                // Swap blocked key to a creature/puzzle/moth slot (positions 2+)
                int swapIdx = 2 + _rng.Next(pool.Count - 2);
                (pool[0], pool[swapIdx]) = (pool[swapIdx], pool[0]);
            }
        }

        if (pinnedOnTable)
        {
            candidate[SLOT_TABLE] = pinnedKey;
            candidate[SLOT_CROW] = pool[0];
        }
        else
        {
            candidate[SLOT_CROW] = pinnedKey;
            candidate[SLOT_TABLE] = pool[0];
        }

        candidate[SLOT_ODDITIES_PUZZLE] = pool[1];
        for (int i = 0; i < ShufflableCreatures.Length; i++)
            candidate[ShufflableCreatures[i].ToString()] = pool[2 + i];
        candidate[SLOT_MOTH] = pool[10];

        return candidate;
    }

    private void AcceptCandidate(Dictionary<string, int> candidate, int phase, int totalAttempts)
    {
        _shufflePhase = phase;
        foreach (var kvp in candidate)
            _creatureToKeyId[kvp.Key] = kvp.Value.ToString();
        _creatureToKeyId["phase"] = phase.ToString();
        _startingTableKey = candidate[SLOT_TABLE];

        Plugin.Log.LogInfo($"KEY SHUFFLE (full, phase {phase}): Table -> {ItemName(candidate[SLOT_TABLE])}");
        Plugin.Log.LogInfo($"KEY SHUFFLE (full, phase {phase}): Crow -> {ItemName(candidate[SLOT_CROW])}");
        Plugin.Log.LogInfo($"KEY SHUFFLE (full, phase {phase}): Oddities Puzzle -> {ItemName(candidate[SLOT_ODDITIES_PUZZLE])}");
        foreach (int c in ShufflableCreatures)
            Plugin.Log.LogInfo($"KEY SHUFFLE (full, phase {phase}): {CreatureNames.GetValueOrDefault(c, $"Creature{c}")} -> {ItemName(candidate[c.ToString()])}");
        Plugin.Log.LogInfo($"KEY SHUFFLE (full, phase {phase}): Moth -> {ItemName(candidate[SLOT_MOTH])}");
        Plugin.Log.LogInfo($"KEY SHUFFLE: Valid in {totalAttempts} attempt(s) (phase {phase}).");
    }

    private void BuildLockRemap()
    {
        _lockIdRemap.Clear();
        bool fullShuffle = Plugin.EnablePantryShuffle.Value && Plugin.EnableKeyShuffle.Value;

        if (fullShuffle)
        {
            // Each creature's original key gets remapped to what they now give
            foreach (var kvp in KnownOriginalKeys)
            {
                int newItem = GetSlotItem(kvp.Key);
                if (newItem > 0) _lockIdRemap[kvp.Value] = newItem;
            }
            // Crow: original was Fridge
            int crowItem = GetSlotItem(SLOT_CROW);
            if (crowItem > 0) _lockIdRemap[FRIDGE] = crowItem;
            // Table: original was Pantry
            int tableItem = GetSlotItem(SLOT_TABLE);
            if (tableItem > 0) { _lockIdRemap[PANTRY] = tableItem; _startingTableKey = tableItem; }
        }
        else
        {
            foreach (var kvp in KnownOriginalKeys)
            {
                if (_creatureToKeyId.TryGetValue(kvp.Key.ToString(), out string newStr)
                    && int.TryParse(newStr, out int newLock))
                    _lockIdRemap[kvp.Value] = newLock;
            }
            _lockIdRemap[FRIDGE] = FRIDGE;
        }

        foreach (var kvp in _lockIdRemap)
            Plugin.Log.LogInfo($"KEY REMAP: DoorLockID {kvp.Key} -> {kvp.Value}");
    }

    public int? GetShuffledLockId(int creatureId)
    {
        Initialize();
        if (_creatureToKeyId.TryGetValue(creatureId.ToString(), out string s) && int.TryParse(s, out int v))
            return v;
        return null;
    }

    public int? GetRemappedLockId(int originalLockId)
    {
        Initialize();
        if (_lockIdRemap.TryGetValue(originalLockId, out int v)) return v;
        return null;
    }

    public void SaveMapping()
    {
        try
        {
            var lines = new List<string> { "{" };
            var entries = _creatureToKeyId.ToList();
            for (int i = 0; i < entries.Count; i++)
            {
                string comma = i < entries.Count - 1 ? "," : "";
                lines.Add($"  \"{entries[i].Key}\": \"{entries[i].Value}\"{comma}");
            }
            lines.Add("}");
            File.WriteAllLines(_savePath, lines);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Failed to save key mapping: {ex.Message}"); }
    }

    public bool LoadMapping()
    {
        try
        {
            if (!File.Exists(_savePath)) return false;
            string json = File.ReadAllText(_savePath);
            _creatureToKeyId.Clear();
            foreach (var line in json.Split('\n'))
            {
                var trimmed = line.Trim().TrimEnd(',');
                if (trimmed.StartsWith("\""))
                {
                    int ci = trimmed.IndexOf(':');
                    if (ci > 0)
                    {
                        string key = trimmed.Substring(0, ci).Trim().Trim('"');
                        string value = trimmed.Substring(ci + 1).Trim().Trim('"');
                        _creatureToKeyId[key] = value;
                    }
                }
            }

            // Restore starting table key if saved
            if (_creatureToKeyId.TryGetValue("starting_table", out string tableStr)
                && int.TryParse(tableStr, out int tableKey))
            {
                _startingTableKey = tableKey;
                _startingKeySwapped = false;
                Plugin.Log.LogInfo($"KEY INIT: Starting table key restored → DoorLockID {tableKey}");
            }

            // Restore shuffle phase
            if (_creatureToKeyId.TryGetValue("phase", out string phaseStr)
                && int.TryParse(phaseStr, out int phase))
                _shufflePhase = phase;

            return _creatureToKeyId.Count > 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Failed to load key mapping: {ex.Message}");
            return false;
        }
    }

    public bool HasMapping => _creatureToKeyId.Count > 0;

    /// <summary>
    /// Called by StartingKeyPatch on Key.Start(). Swaps the Pantry table key
    /// to the assigned item. Returns the new DoorLockID, or -1 if no swap needed.
    /// Only valid for key items (crest halves handled separately).
    /// </summary>
    public int TrySwapStartingKey()
    {
        if (_startingKeySwapped || _startingTableKey < 0) return -1;
        if (!IsKey(_startingTableKey)) return -1; // Crest halves handled by different patch
        _startingKeySwapped = true;
        return _startingTableKey;
    }

    /// <summary>Is the starting table item a crest half?</summary>
    public bool IsTableCrest()
    {
        return _startingTableKey == CREST_LEFT || _startingTableKey == CREST_RIGHT;
    }

    // Static lookup dictionaries (used by spoiler log and logging)
    public static readonly Dictionary<int, string> DoorNames = new()
    {
        {1,"Bedroom"}, {2,"Bathroom"}, {3,"Oddities Room"}, {4,"Pantry"},
        {5,"Fridge"}, {6,"Freezer"}, {7,"Front Door"}, {8,"Cheese Cabinet"},
        {9,"Shed"}, {10,"Mug Cabinet"}, {11,"Bowl Cabinet"}, {12,"Cereal Cabinet"},
    };

    public static readonly Dictionary<int, string> CreatureNames = new()
    {
        {1,"Trash Cat"}, {3,"Moth"}, {5,"Them"}, {6,"Grey"},
        {7,"SassFoot"}, {8,"Tree Octopus"}, {9,"Goober"}, {12,"Crow"},
        {13,"Hopper"}, {14,"Jake"},
    };
}

// =====================================================================
// Recipe Logic — hardcoded game data + card shuffle generation
// =====================================================================
public static class RecipeLogic
{
    // Type flags
    public const int T_BREAKFAST = 1;
    public const int T_YARD = 2;
    public const int T_SANDWICH = 4;
    public const int T_ITALY = 8;
    public const int T_BEEF = 16;
    public const int T_SKYSEA = 32;
    public const int T_VEG = 64;
    public const int T_CHEESE = 128;
    public const int T_CEREAL = 256;
    public const int T_PIE = 512;
    public const int T_COOKIE = 1024;
    public const int T_ICECREAM = 2048;

    const int D_BED = 1;
    const int D_BATH = 2;
    const int D_ODD = 3;
    const int D_PANTRY = 4;
    const int D_FRIDGE = 5;
    const int D_FREEZE = 6;
    const int D_CHEESE = 8;
    const int D_SHED = 9;
    const int D_MUG = 10;
    const int D_BOWL = 11;
    const int D_CEREAL = 12;

    public struct Recipe
    {
        public string Name;
        public int Types;
        public int[] IngDoors;   // doors needed for ingredients
        public int[] LocDoors;   // doors needed for vanilla card location
    }

    // All 39 non-default recipes
    // IngDoors = doors needed to access ALL ingredients
    // LocDoors = doors needed to access vanilla card location
    public static readonly Recipe[] Recipes = new Recipe[]
    {
        // === Kitchen / Outside (free location) ===
        new Recipe { Name = "Recipe_EggSandwhichName",                Types = T_BREAKFAST|T_SANDWICH|T_VEG,  IngDoors = new[]{D_FRIDGE},                      LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_GrilledCheeseName",               Types = T_SANDWICH|T_VEG|T_CHEESE,     IngDoors = new[]{D_CHEESE},                      LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_PeanutButterJellySandwichName",   Types = T_SANDWICH|T_VEG,              IngDoors = new[]{D_FRIDGE},                      LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_MeatballSubName",                 Types = T_SANDWICH|T_BEEF,             IngDoors = new[]{D_FRIDGE, D_PANTRY},            LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_AlfredoName",                     Types = T_ITALY|T_CHEESE|T_VEG,        IngDoors = new[]{D_CHEESE, D_FRIDGE},            LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_SpaghettiAndMeatballsName",       Types = T_ITALY|T_BEEF,                IngDoors = new[]{D_FRIDGE, D_PANTRY},            LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_CheeseAndCrackersName",           Types = T_VEG|T_CHEESE,                IngDoors = new[]{D_CHEESE},                      LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_OmeletteName",                    Types = T_BREAKFAST|T_VEG|T_CHEESE|T_YARD, IngDoors = new[]{D_FRIDGE, D_CHEESE},        LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_MushroomSoupName",                Types = T_YARD|T_VEG,                  IngDoors = new[]{D_PANTRY},                      LocDoors = Array.Empty<int>() },
        new Recipe { Name = "Recipe_MushroomStirfryName",             Types = T_YARD|T_VEG,                  IngDoors = new[]{D_FRIDGE},                      LocDoors = Array.Empty<int>() },

        // === Bedroom (Door 1) ===
        new Recipe { Name = "Recipe_BurgerName",                      Types = T_BEEF|T_CHEESE|T_SANDWICH,    IngDoors = new[]{D_FRIDGE, D_CHEESE},            LocDoors = new[]{D_BED} },
        new Recipe { Name = "Recipe_ApplePieName",                    Types = T_PIE,                         IngDoors = new[]{D_FRIDGE, D_BED},               LocDoors = new[]{D_BED} },
        new Recipe { Name = "Recipe_BlueberryPieName",                Types = T_PIE,                         IngDoors = new[]{D_FRIDGE, D_BED},               LocDoors = new[]{D_BED} },
        new Recipe { Name = "Recipe_ChocolateChipCookiesName",        Types = T_COOKIE,                      IngDoors = new[]{D_BED},                         LocDoors = new[]{D_BED} },
        new Recipe { Name = "Recipe_PeanutButterCookiesName",         Types = T_COOKIE,                      IngDoors = Array.Empty<int>(),                   LocDoors = new[]{D_BED} },
        new Recipe { Name = "Recipe_SugarCookiesName",                Types = T_COOKIE,                      IngDoors = Array.Empty<int>(),                   LocDoors = new[]{D_BED} },
        new Recipe { Name = "Recipe_ChocolateIceCreamName",           Types = T_ICECREAM,                    IngDoors = new[]{D_FRIDGE, D_FREEZE, D_BED},     LocDoors = new[]{D_BED} },
        new Recipe { Name = "Recipe_StrawberryIceCreamName",          Types = T_ICECREAM,                    IngDoors = new[]{D_FRIDGE, D_FREEZE},            LocDoors = new[]{D_BED} },

        // === Bathroom (Door 2) ===
        new Recipe { Name = "Recipe_ChickenNoodleSoupName",           Types = T_SKYSEA,                      IngDoors = new[]{D_BATH},                        LocDoors = new[]{D_BATH} },
        new Recipe { Name = "Recipe_CountryFriedChickenName",         Types = T_SKYSEA,                      IngDoors = new[]{D_BATH, D_FRIDGE, D_PANTRY},    LocDoors = new[]{D_BATH} },
        new Recipe { Name = "Recipe_ChickenParmesanName",             Types = T_ITALY|T_SKYSEA,              IngDoors = new[]{D_BATH, D_CHEESE},              LocDoors = new[]{D_BATH} },
        new Recipe { Name = "Recipe_FishChowderName",                 Types = T_SKYSEA,                      IngDoors = new[]{D_BATH, D_FRIDGE, D_PANTRY},    LocDoors = new[]{D_BATH} },
        new Recipe { Name = "Recipe_ChickenBurritoName",              Types = T_SKYSEA|T_CHEESE,             IngDoors = new[]{D_SHED, D_BATH, D_FRIDGE, D_CHEESE}, LocDoors = new[]{D_BATH} },
        new Recipe { Name = "Recipe_FishAndChipsName",                Types = T_SKYSEA,                      IngDoors = new[]{D_BATH, D_PANTRY},              LocDoors = new[]{D_BATH} },

        // === Oddities Room (Door 3) ===
        new Recipe { Name = "Recipe_BeanBurritoName",                 Types = T_VEG,                         IngDoors = new[]{D_SHED, D_ODD, D_FRIDGE},       LocDoors = new[]{D_ODD} },
        new Recipe { Name = "Recipe_HouseSaladName",                  Types = T_CHEESE|T_VEG,                IngDoors = new[]{D_FRIDGE, D_CHEESE},            LocDoors = new[]{D_ODD} },
        new Recipe { Name = "Recipe_FruitSaladName",                  Types = T_VEG,                         IngDoors = new[]{D_BED, D_FRIDGE},               LocDoors = new[]{D_ODD} },
        new Recipe { Name = "Recipe_ChipsAndGuacamoleName",           Types = T_VEG,                         IngDoors = new[]{D_BED, D_ODD},                  LocDoors = new[]{D_ODD} },
        new Recipe { Name = "Recipe_GalubJamunName",                  Types = T_VEG,                         IngDoors = new[]{D_FRIDGE, D_SHED},              LocDoors = new[]{D_ODD} },

        // === Freezer (Door 6) ===
        new Recipe { Name = "Recipe_VanillaIceCreamName",             Types = T_ICECREAM,                    IngDoors = new[]{D_FRIDGE, D_FREEZE},            LocDoors = new[]{D_FREEZE} },

        // === Cheese Cabinet (Door 8) ===
        new Recipe { Name = "Recipe_PizzaName",                       Types = T_VEG|T_ITALY,                 IngDoors = new[]{D_CHEESE},                      LocDoors = new[]{D_CHEESE} },

        // === Shed (Door 9) ===
        new Recipe { Name = "Recipe_BeefStewName",                    Types = T_BEEF,                        IngDoors = new[]{D_FRIDGE, D_PANTRY},            LocDoors = new[]{D_SHED} },
        new Recipe { Name = "Recipe_MeatloafName",                    Types = T_BEEF,                        IngDoors = new[]{D_FRIDGE, D_PANTRY},            LocDoors = new[]{D_SHED} },
        new Recipe { Name = "Recipe_PhillyCheesesteakName",           Types = T_BEEF|T_CHEESE,               IngDoors = new[]{D_FRIDGE, D_CHEESE},            LocDoors = new[]{D_SHED} },
        new Recipe { Name = "Recipe_BeefBurritoName",                 Types = T_BEEF|T_CHEESE,               IngDoors = new[]{D_SHED, D_FRIDGE, D_CHEESE},    LocDoors = new[]{D_SHED} },
        new Recipe { Name = "Recipe_NachosName",                      Types = T_CHEESE|T_BEEF,               IngDoors = new[]{D_BED, D_CHEESE, D_FRIDGE},     LocDoors = new[]{D_SHED} },

        // === Mug Cabinet (Door 10) ===
        new Recipe { Name = "Recipe_SteakAndEggsName",                Types = T_BREAKFAST|T_BEEF,            IngDoors = new[]{D_FRIDGE, D_PANTRY},            LocDoors = new[]{D_MUG} },

        // === Mug Cabinet + Bedroom (Door 10 + Door 1) ===
        new Recipe { Name = "Recipe_EggsAndBaconName",                Types = T_BREAKFAST,                   IngDoors = new[]{D_FRIDGE, D_BED},               LocDoors = new[]{D_MUG, D_BED} },

        // === Cereal Cabinet (Door 12) ===
        new Recipe { Name = "Recipe_FortunateGemsName",               Types = T_CEREAL,                      IngDoors = new[]{D_CEREAL, D_BOWL, D_FRIDGE},    LocDoors = new[]{D_CEREAL} },
    };

    public static readonly (string name, int types, int[] ingDoors)[] DefaultRecipes = new[]
    {
        ("Recipe_ToastName",       T_BREAKFAST | T_VEG, Array.Empty<int>()),
        ("Recipe_PancakeName",     T_BREAKFAST | T_VEG, Array.Empty<int>()),
        ("Recipe_FrenchFiresName", T_VEG,               new[]{ D_PANTRY }),
    };

    public struct CardNeed
    {
        public int CreatureId;
        public int TypeFlag;
        public int Count;
    }

    public static readonly CardNeed[] CreatureCardNeeds = new CardNeed[]
    {
        // Crow (12): 2 of ANY — defaults handle this
        new CardNeed { CreatureId = 13, TypeFlag = T_YARD,      Count = 2 }, // Hopper
        new CardNeed { CreatureId = 1,  TypeFlag = T_SANDWICH,  Count = 2 }, // Raccoon
        new CardNeed { CreatureId = 5,  TypeFlag = T_CHEESE,    Count = 3 }, // Rat
        new CardNeed { CreatureId = 9,  TypeFlag = T_ITALY,     Count = 3 }, // Goober
        new CardNeed { CreatureId = 8,  TypeFlag = T_SKYSEA,    Count = 4 }, // TreeOctopus
        new CardNeed { CreatureId = 6,  TypeFlag = T_BEEF,      Count = 3 }, // Grey
        new CardNeed { CreatureId = 14, TypeFlag = T_PIE,       Count = 1 }, // Jake (Pie)
        new CardNeed { CreatureId = 14, TypeFlag = T_COOKIE,    Count = 1 }, // Jake (Cookie)
        new CardNeed { CreatureId = 14, TypeFlag = T_ICECREAM,  Count = 1 }, // Jake (Ice Cream)
        new CardNeed { CreatureId = 7,  TypeFlag = T_BREAKFAST, Count = 4 }, // SassFoot
        new CardNeed { CreatureId = 3,  TypeFlag = T_VEG,       Count = 4 }, // Moth
    };

    static bool IsCookable(Recipe r, HashSet<int> openDoors)
    {
        foreach (int d in r.IngDoors)
            if (!openDoors.Contains(d)) return false;
        return true;
    }

    /// <summary>
    /// Ingredient-shuffle-aware cookability check. Determines if a recipe's ingredients
    /// are accessible given open doors and the shuffled ingredient layout.
    /// Falls back to vanilla IsCookable if ingredient shuffle is not active.
    /// </summary>
    static bool IsCookableEffective(Recipe r, HashSet<int> openDoors)
    {
        if (!Plugin.EnableIngredientShuffle.Value
            || Plugin.IngredientRando == null
            || !Plugin.IngredientRando.HasMapping)
            return IsCookable(r, openDoors);

        // With ingredient shuffle, check each finite ingredient against its
        // actual shuffled location. An ingredient is available if:
        //   1. Its finite copy is accessible (door 0 or behind an open door), OR
        //   2. Pantry/Fridge is open AND the finite copy is accessible
        //      (player can pick it up to unlock, then dispenser gives infinite)
        // Since condition 2 is a superset of condition 1, we just need to check
        // IsIngredientAccessible which looks at shuffled slot locations.
        //
        // Truly Fridge/Pantry-only items (Steak, Egg, Milk, Butter, Strawberry,
        // Lettuce) have no FiniteCosts entries, so they're naturally always available.

        bool pantryOpen = openDoors.Contains(4);
        bool fridgeOpen = openDoors.Contains(5);

        int recipeIdx = -1;
        for (int i = 0; i < Recipes.Length; i++)
            if (Recipes[i].Name == r.Name) { recipeIdx = i; break; }
        if (recipeIdx < 0) return IsCookable(r, openDoors);
        var costs = FiniteCosts[recipeIdx];

        foreach (var c in costs)
        {
            // If Pantry/Fridge is open AND the finite copy is accessible,
            // the ingredient is effectively infinite (unlocked then dispensed)
            if (pantryOpen && PantryInfiniteIngredients.Contains(c.N)
                && IsIngredientAccessible(c.N, openDoors))
                continue;
            if (fridgeOpen && FridgeInfiniteIngredients.Contains(c.N)
                && IsIngredientAccessible(c.N, openDoors))
                continue;

            // Otherwise check if at least one finite copy is accessible
            if (!IsIngredientAccessible(c.N, openDoors)) return false;
        }
        return true;
    }

    /// <summary>
    /// Check if at least one copy of an ingredient is accessible given open doors
    /// and shuffled ingredient locations.
    /// </summary>
    static bool IsIngredientAccessible(string ingredientName, HashSet<int> openDoors)
    {
        // Build reverse lookup: name → type IDs
        int targetType = -1;
        foreach (var kvp in IngredientRandomizer.TypeToName)
        {
            if (kvp.Value == ingredientName) { targetType = kvp.Key; break; }
        }
        if (targetType < 0) return true;  // Unknown ingredient = assume available

        // Check pinned items at their vanilla locations first
        // Mushroom (type 6) — Kitchen/Outside (door 0), always accessible
        if (targetType == 6) return true;
        // Rose (type 19) — Shed (door 9)
        if (targetType == 19) return openDoors.Contains(9);
        // Cereal (type 23) — Cereal Cabinet (door 12)
        if (targetType == 23) return openDoors.Contains(12);

        var shuffled = Plugin.IngredientRando.GetShuffled();
        if (shuffled == null) return true;

        for (int i = 0; i < IngredientRandomizer.TOTAL_SLOTS; i++)
        {
            if (shuffled[i] != targetType) continue;
            int door = IngredientRandomizer.Slots[i].Door;
            if (door == 0 || openDoors.Contains(door)) return true;
        }

        return false;
    }

    static bool IsAccessible(int[] locDoors, HashSet<int> openDoors)
    {
        foreach (int d in locDoors)
            if (!openDoors.Contains(d)) return false;
        return true;
    }

    // Count cookable+accessible recipes of a given type
    // assignment[i] = recipe index placed at location i
    static int CountCookableOfType(int[] assignment, int typeFlag, HashSet<int> openDoors)
    {
        int count = 0;

        // Defaults count only if their ingredients are accessible
        foreach (var d in DefaultRecipes)
            if ((d.types & typeFlag) != 0 && IsAccessible(d.ingDoors, openDoors)) count++;

        // Check assigned cards
        for (int loc = 0; loc < assignment.Length; loc++)
        {
            int ri = assignment[loc];
            if (ri < 0) continue;

            var recipe = Recipes[ri];
            // The RECIPE must match the type, be cookable, AND its card
            // must be at an accessible LOCATION
            if ((recipe.Types & typeFlag) != 0
                && IsAccessible(Recipes[loc].LocDoors, openDoors)
                && IsCookableEffective(recipe, openDoors))
            {
                count++;
            }
        }

        return count;
    }

    // Validate card shuffle against BFS stages
    // doorsBefore = doors the player has BEFORE satisfying these creatures
    static bool ValidateCardShuffle(
        int[] assignment,
        List<(HashSet<int> doorsBefore, List<int> creatures)> stages)
    {
        foreach (var stage in stages)
        {
            foreach (int creature in stage.creatures)
            {
                foreach (var need in CreatureCardNeeds)
                {
                    if (need.CreatureId != creature) continue;

                    int available = CountCookableOfType(assignment, need.TypeFlag, stage.doorsBefore);
                    if (available < need.Count)
                    {
                        Plugin.Log.LogInfo($"CARD LOGIC: Creature {creature} needs {need.Count} type 0x{need.TypeFlag:X} but only {available} available with doors [{string.Join(",", stage.doorsBefore.OrderBy(x => x))}]. Invalid.");
                        return false;
                    }
                }
            }
        }

        // Ingredient feasibility: can all creatures be fed with finite supplies?
        if (!ValidateIngredientFeasibility(assignment, stages))
        {
            Plugin.Log.LogInfo("CARD LOGIC: Ingredient feasibility check FAILED.");
            return false;
        }

        return true;
    }

    public static Dictionary<string, string> GenerateCardShuffle(KeyRandomizer keyRando, System.Random rng)
    {
        var stages = keyRando.ComputeBFSStages();
        if (stages.Count == 0)
        {
            Plugin.Log.LogError("CARD LOGIC: No BFS stages!");
            return null;
        }

        // Verify ALL creatures appear in BFS stages.
        // Key shuffle validates with vanilla requirements, but ingredient shuffle
        // can change which doors are needed (e.g. Fish moves, changing Tree Octopus
        // requirements). If the ingredient layout breaks reachability, retry.
        var allReachable = new HashSet<int>();
        foreach (var stage in stages)
            foreach (int c in stage.newCreatures)
                allReachable.Add(c);

        int[] expectedCreatures = { 1, 3, 5, 6, 7, 8, 9, 13, 14 }; // AllBFSCreatures
        var missing = expectedCreatures.Where(c => !allReachable.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            Plugin.Log.LogWarning($"CARD LOGIC: BFS incomplete — unreachable creatures: [{string.Join(",", missing)}]. Key+ingredient combo invalid, retrying.");
            return null;
        }

        Plugin.Log.LogInfo($"CARD LOGIC: {stages.Count} BFS stages computed.");
        for (int s = 0; s < stages.Count; s++)
        {
            var doorStr = string.Join(",", stages[s].doorsBefore.OrderBy(x => x));
            var creaStr = string.Join(",", stages[s].newCreatures);
            Plugin.Log.LogInfo($"  Stage {s}: doorsBefore=[{doorStr}] creatures=[{creaStr}]");
        }

        int n = Recipes.Length;
        int[] assignment = new int[n];

        // Phase 1: try random permutations
        int attempts = 0;
        while (attempts < 500)
        {
            attempts++;
            var indices = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).ToArray();
            for (int i = 0; i < n; i++) assignment[i] = indices[i];

            if (ValidateCardShuffle(assignment, stages))
            {
                Plugin.Log.LogInfo($"CARD SHUFFLE: Valid card shuffle found in {attempts} attempt(s).");
                return BuildResult(assignment);
            }
        }

        // Phase 2: smart placement
        Plugin.Log.LogInfo($"CARD SHUFFLE: Random failed after {attempts}, trying smart placement...");
        var smartResult = SmartPlacement(stages, rng);
        if (smartResult != null) return smartResult;

        Plugin.Log.LogError("CARD SHUFFLE: Could not find valid card shuffle!");
        return null;
    }

    static Dictionary<string, string> BuildResult(int[] assignment)
    {
        var result = new Dictionary<string, string>();
        for (int i = 0; i < Recipes.Length; i++)
            result[Recipes[i].Name] = Recipes[assignment[i]].Name;
        return result;
    }

    static Dictionary<string, string> SmartPlacement(
        List<(HashSet<int> doorsBefore, List<int> creatures)> stages,
        System.Random rng)
    {
        int n = Recipes.Length;

        for (int attempt = 0; attempt < 500; attempt++)
        {
            int[] assignment = Enumerable.Repeat(-1, n).ToArray();
            var usedRecipes = new HashSet<int>();
            bool failed = false;

            foreach (var stage in stages)
            {
                if (failed) break;

                foreach (int creature in stage.creatures)
                {
                    if (failed) break;

                    foreach (var need in CreatureCardNeeds)
                    {
                        if (need.CreatureId != creature) continue;

                        int existing = CountCookableOfType(assignment, need.TypeFlag, stage.doorsBefore);
                        int deficit = need.Count - existing;
                        if (deficit <= 0) continue;

                        // Candidate recipes: right type, cookable with current doors, not yet placed
                        var candidates = new List<int>();
                        for (int ri = 0; ri < n; ri++)
                        {
                            if (usedRecipes.Contains(ri)) continue;
                            if ((Recipes[ri].Types & need.TypeFlag) == 0) continue;
                            if (!IsCookableEffective(Recipes[ri], stage.doorsBefore)) continue;
                            candidates.Add(ri);
                        }

                        // Empty accessible locations
                        var emptyLocs = new List<int>();
                        for (int li = 0; li < n; li++)
                        {
                            if (assignment[li] >= 0) continue;
                            if (!IsAccessible(Recipes[li].LocDoors, stage.doorsBefore)) continue;
                            emptyLocs.Add(li);
                        }

                        candidates = candidates.OrderBy(_ => rng.Next()).ToList();
                        emptyLocs = emptyLocs.OrderBy(_ => rng.Next()).ToList();

                        for (int i = 0; i < deficit; i++)
                        {
                            if (i >= candidates.Count || i >= emptyLocs.Count)
                            {
                                failed = true;
                                Plugin.Log.LogInfo($"CARD SMART: Can't place type 0x{need.TypeFlag:X} for creature {creature} (need {deficit}, have {candidates.Count} candidates, {emptyLocs.Count} slots)");
                                break;
                            }
                            assignment[emptyLocs[i]] = candidates[i];
                            usedRecipes.Add(candidates[i]);
                        }
                    }
                }
            }

            if (failed) continue;

            // Fill remaining slots randomly
            var remaining = Enumerable.Range(0, n)
                .Where(i => !usedRecipes.Contains(i))
                .OrderBy(_ => rng.Next()).ToList();
            var emptySlots = Enumerable.Range(0, n)
                .Where(i => assignment[i] < 0)
                .OrderBy(_ => rng.Next()).ToList();

            for (int i = 0; i < Math.Min(remaining.Count, emptySlots.Count); i++)
                assignment[emptySlots[i]] = remaining[i];

            if (ValidateCardShuffle(assignment, stages))
            {
                Plugin.Log.LogInfo($"CARD SMART: Valid shuffle in {attempt + 1} smart attempt(s).");
                return BuildResult(assignment);
            }
        }

        return null;
    }

    // ===================================================================
    // Ingredient Feasibility Validation
    // ===================================================================

    // Ingredient cost for a recipe (only finite/trackable ingredients)
    struct IC { public string N; public int A; public bool W; } // Name, Amount, Whole
    static IC S(string n, int a) => new IC { N = n, A = a };
    static IC WH(string n, int a) => new IC { N = n, A = a, W = true };

    // Sliceable ingredients: name → slices per item
    static readonly Dictionary<string, int> SPI = new()
    {
        {"Tomato",3}, {"Bread",8}, {"Mushroom",2}, {"Carrot",4},
        {"Cheese",4}, {"Apple",2}, {"Lettuce",2}, {"Potato",4}
    };

    // Starting finite ingredients (kitchen + outside, no door needed)
    static readonly Dictionary<string, int> BaseIngredients = new()
    {
        {"Flour",2}, {"Sugar",2}, {"Pasta",1}, {"Salt",1}, {"Tomato",3},
        {"Broth",2}, {"Crackers",1}, {"PeanutButter",1}, {"Bread",1},
        {"MapleSyrup",1}, {"Mushroom",8}, {"Carrot",1}, {"Water",1}
    };

    // Ingredients unlocked by specific doors (finite quantities)
    static readonly Dictionary<int, Dictionary<string, int>> DoorAddedIngredients = new()
    {
        { 8, new() { {"Cheese",1} } },         // Cheese Cabinet
        { 1, new() { {"Blueberries",1}, {"Chips",1}, {"Apple",1}, {"ChocolateBar",1}, {"Bacon",1} } }, // Bedroom
        { 9, new() { {"Tortilla",1}, {"Rose",1} } },     // Shed
        { 3, new() { {"Avocado",1}, {"Beans",1} } },     // Oddities Room
        { 12, new() { {"Cereal",1} } },         // Cereal Cabinet
        { 2, new() { {"Chicken",1} } },          // Bathroom (Fish reserved for Tree Octopus summoning)
    };

    // Ingredients that become infinite when Pantry opens
    static readonly HashSet<string> PantryInfiniteIngredients = new()
    {
        "Flour", "Sugar", "Pasta", "Salt", "Tomato", "Broth", "Crackers",
        "PeanutButter", "Bread", "MapleSyrup", "Mushroom", "Carrot", "Water",
        "Potato", "Tortilla", "Apple", "Chips", "Rose", "Avocado",
        "ChocolateBar", "Beans", "Cereal"
    };

    // Ingredients that become infinite when Fridge opens
    static readonly HashSet<string> FridgeInfiniteIngredients = new()
    {
        "Cheese", "Blueberries", "Chicken", "Bacon"
        // Steak, Egg, Milk, Butter, Strawberry, Lettuce are Fridge-only (no finite source)
        // Fish reserved for Tree Octopus summoning pre-Fridge
    };

    // Finite ingredient costs per recipe (parallel to Recipes[], omitting always-infinite ingredients)
    // Always infinite: Steak, Milk, Egg, Butter, Strawberry, Lettuce, Chicken, Fish, Bacon
    // Potato/Ice excluded from costs when recipe has D_PANTRY/D_FREEZE (not reachable pre-Pantry anyway)
    static readonly IC[][] FiniteCosts = new IC[][]
    {
        new[] { S("Bread",2) },                                                 //  0: EggSandwich
        new[] { S("Bread",2), S("Cheese",1) },                                 //  1: GrilledCheese
        new[] { S("Bread",2), S("PeanutButter",1), S("Sugar",1) },             //  2: PBJ
        new[] { S("Salt",2), WH("Tomato",1), S("Bread",2) },                   //  3: MeatballSub
        new[] { S("Pasta",1), WH("Cheese",1) },                                //  4: Alfredo
        new[] { S("Pasta",1), WH("Tomato",1), S("Salt",2) },                   //  5: Spaghetti&Meatballs
        new[] { S("Crackers",1), S("Cheese",3) },                              //  6: Cheese&Crackers
        new[] { S("Mushroom",1), S("Cheese",1), S("Tomato",1) },               //  7: Omelet
        new[] { S("Mushroom",2), S("Broth",1) },                               //  8: MushroomSoup
        new[] { S("Mushroom",2), S("Pasta",1) },                               //  9: MushroomStirfry
        new[] { S("Salt",1), S("Cheese",1), S("Bread",2) },                    // 10: Burger
        new[] { S("Flour",1), S("Water",1), S("Sugar",1), S("Apple",1) },                    // 11: ApplePie
        new[] { S("Flour",1), S("Water",1), S("Sugar",1), S("Blueberries",1) },              // 12: BlueberryPie
        new[] { S("Flour",1), S("Water",1), S("Sugar",1), S("ChocolateBar",1) },             // 13: ChocChipCookies
        new[] { S("Flour",1), S("Water",1), S("Sugar",1), S("PeanutButter",1) },             // 14: PBCookies
        new[] { S("Flour",1), S("Water",1), S("Sugar",1) },                                  // 15: SugarCookies
        new[] { S("Sugar",1), S("ChocolateBar",1) },                           // 16: ChocIceCream
        new[] { S("Sugar",1) },                                                // 17: StrawberryIceCream
        new[] { S("Broth",1), S("Carrot",1), S("Pasta",1), S("Chicken",1) },  // 18: ChickenNoodleSoup
        new[] { S("Flour",1) },                                                // 19: CountryFriedChicken
        new[] { S("Pasta",1), WH("Tomato",1), S("Salt",1), S("Cheese",1), S("Chicken",1) },   // 20: ChickenParmesan
        new[] { S("Flour",1) },                                                // 21: FishChowder
        new[] { S("Tortilla",1), S("Cheese",1) },                              // 22: ChickenBurrito
        Array.Empty<IC>(),                                                      // 23: FishAndChips (Potato=D_PANTRY)
        new[] { S("Tortilla",1), S("Beans",1) },                               // 24: BeanBurrito
        new[] { S("Tomato",1), S("Cheese",1), S("Bread",1) },                  // 25: HouseSalad
        new[] { S("Blueberries",1), S("Apple",2) },                            // 26: FruitSalad
        new[] { S("Chips",1), S("Avocado",1), S("Salt",1) },                   // 27: ChipsAndGuac
        new[] { S("Flour",1), S("Water",1), S("Rose",1), S("MapleSyrup",1) },                // 28: GulabJamun
        new[] { S("Sugar",1) },                                                // 29: VanillaIceCream
        new[] { S("Flour",1), S("Water",1), WH("Tomato",1), S("Salt",1), S("Cheese",1) },   // 30: Pizza
        new[] { S("Broth",1), S("Carrot",1) },                                 // 31: BeefStew
        new[] { S("Salt",2), WH("Tomato",1) },                                 // 32: Meatloaf
        new[] { S("Cheese",1), S("Bread",2) },                                 // 33: PhillyCheesesteak
        new[] { S("Tortilla",1), S("Cheese",1) },                              // 34: BeefBurrito
        new[] { S("Chips",1), WH("Cheese",1), S("Salt",1) },                   // 35: Nachos
        Array.Empty<IC>(),                                                      // 36: SteakAndEggs (Potato=D_PANTRY)
        Array.Empty<IC>(),                                                      // 37: EggsAndBacon (all inf)
        new[] { S("Cereal",1) },                                               // 38: FortunateGems
    };

    static readonly IC[][] DefaultFiniteCosts = new IC[][]
    {
        new[] { S("Bread",1) },                         // Toast
        new[] { S("Flour",1), S("Water",1), S("MapleSyrup",1) },     // Pancakes
        Array.Empty<IC>(),                               // FrenchFries (Potato=D_PANTRY)
    };

    /// <summary>Ingredient pool tracker for backtracking solver.</summary>
    class IngPool
    {
        readonly Dictionary<string, int> _avail = new();
        readonly Dictionary<string, int> _wholeUsed = new();
        readonly Dictionary<string, int> _sliceUsed = new();

        int Get(Dictionary<string, int> d, string k) => d.TryGetValue(k, out int v) ? v : 0;
        void Add(Dictionary<string, int> d, string k, int v) => d[k] = Get(d, k) + v;

        public void SetAvail(string ing, int count) { _avail[ing] = count; }
        public void AddAvail(string ing, int count) { _avail[ing] = Get(_avail, ing) + count; }

        /// <summary>Set ingredient to infinite (clears usage tracking).</summary>
        public void SetInfinite(string ing)
        {
            _avail[ing] = int.MaxValue;
            _wholeUsed.Remove(ing);
            _sliceUsed.Remove(ing);
        }

        public bool CanCook(IC[] costs)
        {
            // Tentatively add costs, check each ingredient
            foreach (var c in costs)
            {
                int avail = Get(_avail, c.N);
                if (avail <= 0 && !_avail.ContainsKey(c.N)) continue; // ingredient not tracked = infinite
                if (avail == int.MaxValue) continue; // infinite

                if (SPI.TryGetValue(c.N, out int spi))
                {
                    int w = Get(_wholeUsed, c.N) + (c.W ? c.A : 0);
                    int s = Get(_sliceUsed, c.N) + (c.W ? 0 : c.A);
                    if (w + (s + spi - 1) / spi > avail) return false;
                }
                else
                {
                    int u = Get(_wholeUsed, c.N) + c.A;
                    if (u > avail) return false;
                }
            }
            return true;
        }

        public void Cook(IC[] costs)
        {
            foreach (var c in costs)
            {
                if (!_avail.ContainsKey(c.N) || Get(_avail, c.N) == int.MaxValue) continue;
                if (SPI.ContainsKey(c.N))
                {
                    if (c.W) Add(_wholeUsed, c.N, c.A);
                    else Add(_sliceUsed, c.N, c.A);
                }
                else
                    Add(_wholeUsed, c.N, c.A);
            }
        }

        public void Uncook(IC[] costs)
        {
            foreach (var c in costs)
            {
                if (!_avail.ContainsKey(c.N) || Get(_avail, c.N) == int.MaxValue) continue;
                if (SPI.ContainsKey(c.N))
                {
                    if (c.W) Add(_wholeUsed, c.N, -c.A);
                    else Add(_sliceUsed, c.N, -c.A);
                }
                else
                    Add(_wholeUsed, c.N, -c.A);
            }
        }
    }

    static IC[] GetRecipeCost(int recipeIdx)
    {
        if (recipeIdx >= 0) return FiniteCosts[recipeIdx];
        return DefaultFiniteCosts[-(recipeIdx + 1)];
    }

    /// <summary>
    /// Find all usable recipe indices (type-matching, cookable, card-accessible) at a stage.
    /// Positive indices = Recipes[], negative indices = defaults (-1=Toast, -2=Pancakes, -3=FrenchFries).
    /// </summary>
    static List<int> FindUsableRecipes(int[] assignment, int typeFlag, HashSet<int> openDoors)
    {
        var options = new HashSet<int>();

        for (int i = 0; i < DefaultRecipes.Length; i++)
        {
            if ((DefaultRecipes[i].types & typeFlag) != 0
                && IsAccessible(DefaultRecipes[i].ingDoors, openDoors))
                options.Add(-(i + 1));
        }

        for (int loc = 0; loc < Recipes.Length; loc++)
        {
            int ri = assignment[loc];
            if (ri < 0) continue;
            if ((Recipes[ri].Types & typeFlag) == 0) continue;
            if (!IsCookableEffective(Recipes[ri], openDoors)) continue;
            if (!IsAccessible(Recipes[loc].LocDoors, openDoors)) continue;
            options.Add(ri);
        }

        return options.ToList();
    }

    struct SolveNeed
    {
        public int CreatureId;
        public int TypeFlag;
        public int Count;
        public List<int> Options;
    }

    static bool SolveRecursive(
        List<SolveNeed> needs, int needIdx, int pickIdx,
        IngPool pool, Dictionary<int, HashSet<int>> perCreatureUsed)
    {
        if (needIdx >= needs.Count) return true;
        var need = needs[needIdx];
        if (pickIdx >= need.Count)
            return SolveRecursive(needs, needIdx + 1, 0, pool, perCreatureUsed);

        if (!perCreatureUsed.ContainsKey(need.CreatureId))
            perCreatureUsed[need.CreatureId] = new HashSet<int>();
        var used = perCreatureUsed[need.CreatureId];

        foreach (int ri in need.Options)
        {
            if (used.Contains(ri)) continue;
            var cost = GetRecipeCost(ri);
            if (!pool.CanCook(cost)) continue;

            pool.Cook(cost);
            used.Add(ri);

            if (SolveRecursive(needs, needIdx, pickIdx + 1, pool, perCreatureUsed))
                return true;

            used.Remove(ri);
            pool.Uncook(cost);
        }
        return false;
    }

    /// <summary>
    /// Validate that all creatures across all pre-Pantry stages can be fed
    /// without exceeding finite ingredient supplies.
    /// Uses shuffled ingredient locations when ingredient shuffle is active.
    /// </summary>
    public static bool ValidateIngredientFeasibility(
        int[] assignment,
        List<(HashSet<int> doorsBefore, List<int> creatures)> stages)
    {
        // Build ingredient pool — use shuffled data if ingredient shuffle is active
        var pool = new IngPool();

        bool useShuffled = Plugin.EnableIngredientShuffle.Value
                           && Plugin.IngredientRando != null
                           && Plugin.IngredientRando.HasMapping;

        Dictionary<string, int> baseIngs;
        Dictionary<int, Dictionary<string, int>> doorIngs;

        if (useShuffled)
        {
            baseIngs = Plugin.IngredientRando.GetShuffledBaseIngredients();
            doorIngs = Plugin.IngredientRando.GetShuffledDoorIngredients();
        }
        else
        {
            baseIngs = BaseIngredients;
            doorIngs = DoorAddedIngredients;
        }

        foreach (var kvp in baseIngs)
            pool.SetAvail(kvp.Key, kvp.Value);

        var doorsAdded = new HashSet<int>();
        var perCreatureUsed = new Dictionary<int, HashSet<int>>();

        // Pre-deduct Crow's feeding: Toast + Pancakes (always eaten before Stage 0)
        pool.Cook(DefaultFiniteCosts[0]); // Toast: Bread×1
        pool.Cook(DefaultFiniteCosts[1]); // Pancakes: Flour×1, Water×1, MapleSyrup×1

        foreach (var stage in stages)
        {
            bool pantryOpen = stage.doorsBefore.Contains(4);
            bool fridgeOpen = stage.doorsBefore.Contains(5);

            // Vanilla mode: both Pantry+Fridge open = everything infinite
            if (pantryOpen && fridgeOpen && !useShuffled)
                return true;

            // Add ingredients from newly opened doors
            foreach (int door in stage.doorsBefore)
            {
                if (doorsAdded.Contains(door)) continue;
                doorsAdded.Add(door);

                // Add finite ingredients from this door
                if (doorIngs.TryGetValue(door, out var items))
                    foreach (var kvp in items)
                        pool.AddAvail(kvp.Key, kvp.Value);

                if (!useShuffled)
                {
                    // Vanilla: Pantry/Fridge open = all their ingredients become infinite
                    if (door == 4) // Pantry
                        foreach (var ing in PantryInfiniteIngredients)
                            pool.SetInfinite(ing);
                    if (door == 5) // Fridge
                        foreach (var ing in FridgeInfiniteIngredients)
                            pool.SetInfinite(ing);
                }
            }

            // With ingredient shuffle: when Pantry or Fridge is open, ingredients
            // become infinite ONLY if the player can access their finite copy first
            // (picking it up unlocks it for the dispenser). Check each stage.
            if (useShuffled)
            {
                if (pantryOpen)
                    foreach (var ing in PantryInfiniteIngredients)
                        if (IsIngredientAccessible(ing, stage.doorsBefore))
                            pool.SetInfinite(ing);
                if (fridgeOpen)
                    foreach (var ing in FridgeInfiniteIngredients)
                        if (IsIngredientAccessible(ing, stage.doorsBefore))
                            pool.SetInfinite(ing);
            }

            // Build needs for this stage's creatures
            var needs = new List<SolveNeed>();
            foreach (int creature in stage.creatures)
            {
                foreach (var need in CreatureCardNeeds)
                {
                    if (need.CreatureId != creature) continue;
                    var options = FindUsableRecipes(assignment, need.TypeFlag, stage.doorsBefore);
                    needs.Add(new SolveNeed
                    {
                        CreatureId = creature,
                        TypeFlag = need.TypeFlag,
                        Count = need.Count,
                        Options = options
                    });
                }

                // Crow (creature 12) needs 2 of anything — defaults suffice, skip ingredient tracking
                if (creature == 12) continue;
            }

            if (needs.Count == 0) continue;

            // Sort by fewest options first (most constrained) for better pruning
            needs.Sort((a, b) => a.Options.Count.CompareTo(b.Options.Count));

            // Solve — on success, ingredients stay deducted for next stage
            if (!SolveRecursive(needs, 0, 0, pool, perCreatureUsed))
                return false;
        }
        return true;
    }

    static string DoorName(int id) => KeyRandomizer.DoorNames.TryGetValue(id, out var n) ? n : $"Door{id}";
    static string CreatureName(int id) => KeyRandomizer.CreatureNames.TryGetValue(id, out var n) ? n : $"Creature{id}";

    static readonly Dictionary<int, string> TypeNames = new()
    {
        {T_BREAKFAST,"Breakfast"}, {T_YARD,"Yard"}, {T_SANDWICH,"Sandwich"},
        {T_ITALY,"Italy"}, {T_BEEF,"Beef"}, {T_SKYSEA,"SkySea"},
        {T_VEG,"Veg"}, {T_CHEESE,"Cheese"}, {T_CEREAL,"Cereal"},
        {T_PIE,"Pie"}, {T_COOKIE,"Cookie"}, {T_ICECREAM,"Ice Cream"},
    };

    static string CleanRecipeName(string name)
    {
        string s = name;
        if (s.StartsWith("Recipe_")) s = s.Substring(7);
        if (s.EndsWith("Name")) s = s.Substring(0, s.Length - 4);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
                sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static string TypeFlagsToString(int types)
    {
        var tags = new List<string>();
        foreach (var kvp in TypeNames)
            if ((types & kvp.Key) != 0) tags.Add(kvp.Value);
        return string.Join(", ", tags);
    }

    public static void GenerateSpoilerLog(
        KeyRandomizer keyRando,
        Dictionary<string, string> cardMapping,
        List<(HashSet<int> doorsBefore, List<int> newCreatures)> stages)
    {
        try
        {
            string path = Path.Combine(BepInEx.Paths.PluginPath, "CK_AP_SpoilerLog.txt");
            using var w = new StreamWriter(path);

            w.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            w.WriteLine("║            CREATURE KITCHEN RANDOMIZER — SPOILER LOG        ║");
            w.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            w.WriteLine();
            bool fullShuffle = Plugin.EnablePantryShuffle.Value;
            w.WriteLine($"  Version:         {Plugin.VERSION}");
            w.WriteLine($"  Seed:            {Plugin.ActiveSeed}");
            if (fullShuffle && Plugin.KeyRando.ShufflePhase > 0)
            {
                string phaseDesc = Plugin.KeyRando.ShufflePhase switch
                {
                    1 => "Phase 1 (Pantry start - Moth gateway)",
                    2 => "Phase 2 (Fridge start - Pantry deep)",
                    3 => "Phase 3 (Fridge/Pantry start - fallback)",
                    _ => $"Phase {Plugin.KeyRando.ShufflePhase}"
                };
                w.WriteLine($"  Shuffle Phase:   {phaseDesc}");
            }
            w.WriteLine($"  Card Shuffle:    {Plugin.EnableCardShuffle.Value}");
            w.WriteLine($"  Key Shuffle:     {Plugin.EnableKeyShuffle.Value}");
            w.WriteLine($"  Full Shuffle:    {fullShuffle}");
            w.WriteLine($"  Mistake System:  {Plugin.EnableMistakeSystem.Value}");
            w.WriteLine($"  Ing. Shuffle:    {Plugin.EnableIngredientShuffle.Value}");
            w.WriteLine();

            // === Key/Item Assignments ===
            w.WriteLine("══════════════════════════════════════════════════════════════");
            w.WriteLine("  ITEM ASSIGNMENTS");
            w.WriteLine("══════════════════════════════════════════════════════════════");
            w.WriteLine();

            if (fullShuffle)
            {
                int tableItem = keyRando.GetSlotItem(KeyRandomizer.SLOT_TABLE);
                int crowItem = keyRando.GetSlotItem(KeyRandomizer.SLOT_CROW);
                int puzzleItem = keyRando.GetSlotItem(KeyRandomizer.SLOT_ODDITIES_PUZZLE);
                int mothItem = keyRando.GetSlotItem(KeyRandomizer.SLOT_MOTH);

                w.WriteLine($"  {"Source",-22} {"Gives",-25}");
                w.WriteLine($"  {"------",-22} {"-----",-25}");
                w.WriteLine($"  {"Starting Table",-22} {KeyRandomizer.ItemName(tableItem),-25}");
                w.WriteLine($"  {"Crow",-22} {KeyRandomizer.ItemName(crowItem),-25}");
                w.WriteLine($"  {"Oddities Puzzle",-22} {KeyRandomizer.ItemName(puzzleItem),-25}");

                foreach (int creature in new[] { 1, 5, 6, 7, 8, 9, 13, 14 })
                {
                    int item = keyRando.GetSlotItem(creature);
                    w.WriteLine($"  {CreatureName(creature),-22} {KeyRandomizer.ItemName(item),-25}");
                }

                w.WriteLine($"  {"Moth",-22} {KeyRandomizer.ItemName(mothItem),-25}");
            }
            else
            {
                int tableKey = keyRando.GetStartingTableKey();
                if (tableKey > 0)
                    w.WriteLine($"  Starting Table Key:  {DoorName(tableKey)}");
                else
                    w.WriteLine($"  Starting Table Key:  Pantry (default)");
                w.WriteLine();

                w.WriteLine($"  {"Creature",-18} {"Gives Key To",-20}");
                w.WriteLine($"  {"--------",-18} {"------------",-20}");
                w.WriteLine($"  {"Crow",-18} {"Fridge (locked)",-20}");

                foreach (int creature in new[] { 1, 5, 6, 7, 8, 9, 13, 14 })
                {
                    int originalKey = KeyRandomizer.KnownOriginalKeys.ContainsKey(creature)
                        ? KeyRandomizer.KnownOriginalKeys[creature] : -1;
                    int remappedKey = keyRando.GetRemappedLock(originalKey);
                    if (remappedKey < 0) remappedKey = originalKey;
                    w.WriteLine($"  {CreatureName(creature),-18} {DoorName(remappedKey),-20}");
                }
            }
            w.WriteLine();

            // === Ingredient Shuffle ===
            if (Plugin.EnableIngredientShuffle.Value && Plugin.IngredientRando.HasMapping)
            {
                w.WriteLine("══════════════════════════════════════════════════════════════");
                w.WriteLine("  INGREDIENT LOCATIONS");
                w.WriteLine("══════════════════════════════════════════════════════════════");
                w.WriteLine();
                w.WriteLine($"  {"Location",-20} {"Original",-18} {"Now",-18}");
                w.WriteLine($"  {"────────",-20} {"────────",-18} {"───",-18}");

                var shuffled = Plugin.IngredientRando.GetShuffled();
                if (shuffled != null)
                {
                    for (int i = 0; i < IngredientRandomizer.TOTAL_SLOTS; i++)
                    {
                        var slot = IngredientRandomizer.Slots[i];
                        string doorName = slot.Door > 0
                            ? KeyRandomizer.DoorNames.GetValueOrDefault(slot.Door, $"Door{slot.Door}")
                            : "Kitchen";
                        string origName = IngredientRandomizer.TypeToName.GetValueOrDefault(slot.OrigType, $"?{slot.OrigType}");
                        string newName = IngredientRandomizer.TypeToName.GetValueOrDefault(shuffled[i], $"?{shuffled[i]}");
                        string changed = slot.OrigType != shuffled[i] ? "" : " (unchanged)";
                        w.WriteLine($"  {doorName,-20} {origName,-18} {newName,-18}{changed}");
                    }
                }
                w.WriteLine();
            }

            // === Card Shuffle ===
            w.WriteLine("══════════════════════════════════════════════════════════════");
            w.WriteLine("  CARD LOCATIONS");
            w.WriteLine("══════════════════════════════════════════════════════════════");
            w.WriteLine();
            w.WriteLine($"  {"Vanilla Card Location",-35} {"Now Contains",-30}");
            w.WriteLine($"  {"─────────────────────",-35} {"────────────",-30}");

            foreach (var kvp in cardMapping.OrderBy(k => k.Key))
            {
                string from = CleanRecipeName(kvp.Key);
                string to = CleanRecipeName(kvp.Value);
                if (kvp.Key != kvp.Value)
                    w.WriteLine($"  {from,-35} {to,-30}");
                else
                    w.WriteLine($"  {from,-35} (unchanged)");
            }
            w.WriteLine();

            // === Progression Path ===
            w.WriteLine("══════════════════════════════════════════════════════════════");
            w.WriteLine("  EXPECTED PROGRESSION");
            w.WriteLine("══════════════════════════════════════════════════════════════");
            w.WriteLine();

            // Build reverse card mapping: shuffled recipe → vanilla location
            var reverseCards = new Dictionary<string, string>();
            foreach (var kvp in cardMapping)
                reverseCards[kvp.Value] = kvp.Key;

            for (int si = 0; si < stages.Count; si++)
            {
                var stage = stages[si];
                var doorList = stage.doorsBefore.OrderBy(x => x).Select(d => DoorName(d));
                w.WriteLine($"  ── Stage {si} ──");
                w.WriteLine($"  Doors Open: {string.Join(", ", doorList)}");
                w.WriteLine($"  Feed: {string.Join(", ", stage.newCreatures.Select(c => CreatureName(c)))}");
                w.WriteLine();

                foreach (int creature in stage.newCreatures)
                {
                    var needs = CreatureCardNeeds.Where(n => n.CreatureId == creature).ToList();
                    if (needs.Count == 0 && creature == 12)
                    {
                        w.WriteLine($"    {CreatureName(creature)}: 2 of any recipe (defaults work)");
                        w.WriteLine();
                        continue;
                    }

                    foreach (var need in needs)
                    {
                        string typeName = TypeFlagsToString(need.TypeFlag);
                        w.WriteLine($"    {CreatureName(creature)}: needs {need.Count} {typeName}");

                        // List accessible recipes of this type
                        // Check defaults
                        foreach (var d in DefaultRecipes)
                        {
                            if ((d.types & need.TypeFlag) != 0 && IsAccessible(d.ingDoors, stage.doorsBefore))
                                w.WriteLine($"      • {CleanRecipeName(d.name)} (default - always available)");
                        }

                        // Check shuffled cards
                        foreach (var r in Recipes)
                        {
                            if ((r.Types & need.TypeFlag) == 0) continue;
                            bool cookable = IsCookableEffective(r, stage.doorsBefore);

                            // Where is this recipe's card?
                            string cardLocation;
                            bool cardAccessible;

                            if (reverseCards.TryGetValue(r.Name, out string vanillaLoc))
                            {
                                int locIdx = Array.FindIndex(Recipes, x => x.Name == vanillaLoc);
                                cardAccessible = locIdx >= 0 && IsAccessible(Recipes[locIdx].LocDoors, stage.doorsBefore);
                                cardLocation = $"at {CleanRecipeName(vanillaLoc)}'s spot";
                            }
                            else
                            {
                                cardAccessible = IsAccessible(r.LocDoors, stage.doorsBefore);
                                cardLocation = "original spot";
                            }

                            string status;
                            if (cookable && cardAccessible)
                                status = "✓";
                            else if (!cookable && !cardAccessible)
                                status = "✗ ingredients + card locked";
                            else if (!cookable)
                                status = "✗ ingredients locked";
                            else
                                status = "✗ card locked";

                            w.WriteLine($"      {status} {CleanRecipeName(r.Name)} ({cardLocation})");
                        }
                        w.WriteLine();
                    }
                }

                // Show what doors open after this stage
                var newDoors = new List<string>();
                foreach (int creature in stage.newCreatures)
                {
                    int origKey = KeyRandomizer.KnownOriginalKeys.ContainsKey(creature)
                        ? KeyRandomizer.KnownOriginalKeys[creature] : -1;
                    int remapped = keyRando.GetRemappedLock(origKey);
                    if (remapped < 0) remapped = origKey;
                    if (remapped > 0)
                        newDoors.Add($"{CreatureName(creature)} → unlocks {DoorName(remapped)}");
                }
                if (newDoors.Count > 0)
                {
                    w.WriteLine($"  Unlocks:");
                    foreach (string nd in newDoors)
                        w.WriteLine($"    {nd}");
                }
                w.WriteLine();
            }

            w.WriteLine("══════════════════════════════════════════════════════════════");
            w.WriteLine("  END OF SPOILER LOG");
            w.WriteLine("══════════════════════════════════════════════════════════════");

            Plugin.Log.LogInfo($"SPOILER LOG: Written to {path}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Failed to write spoiler log: {ex.Message}");
        }
    }
}

// =====================================================================
// Ingredient Location Randomizer
// =====================================================================
public class IngredientRandomizer
{
    /// <summary>A physical ingredient spawn point in the game world.</summary>
    public struct IngSlot
    {
        public int OrigType;  // Original UnlockableItemID (1-24)
        public int Door;      // Door required to access (0 = kitchen/outside)
        public bool Pinned;   // If true, this slot keeps its original type (not shuffled)
    }

    /// <summary>Map UnlockableItemID → ingredient name used in recipe cost tracking.</summary>
    public static readonly Dictionary<int, string> TypeToName = new()
    {
        {1, "ChocolateBar"}, {2, "Sugar"}, {3, "Chips"}, {4, "Crackers"},
        {5, "Apple"}, {6, "Mushroom"}, {7, "Fish"}, {8, "Chicken"},
        {9, "Cheese"}, {10, "Salt"}, {11, "Flour"}, {12, "Bacon"},
        {13, "Beans"}, {14, "Broth"}, {15, "Pasta"}, {16, "MapleSyrup"},
        {17, "Tomato"}, {18, "PeanutButter"}, {19, "Rose"}, {20, "Avocado"},
        {21, "Blueberries"}, {22, "Tortilla"}, {23, "Cereal"}, {24, "Carrot"},
    };

    /// <summary>UnlockableItemIDs that are pinned and don't participate in shuffle.</summary>
    public static readonly HashSet<int> PinnedTypes = new() { 6, 19, 23 }; // Mushroom, Rose, Cereal

    /// <summary>All ingredient spawn slots.</summary>
    public static readonly IngSlot[] Slots;
    public const int TOTAL_SLOTS = 26;  // 15 kitchen + 11 behind doors (excl Rose,Cereal,Mushroom)

    static IngredientRandomizer()
    {
        var list = new List<IngSlot>();
        // Kitchen / Outside (door 0) — items that have ItemUnlockable
        // (Bread and Water do NOT have UnlockableItemID, so they are excluded)
        AddN(list, 11, 0, 2);   // Flour×2
        AddN(list, 2, 0, 2);   // Sugar×2
        AddN(list, 15, 0, 1);   // Pasta×1
        AddN(list, 10, 0, 1);   // Salt×1
        AddN(list, 17, 0, 3);   // Tomato×3
        AddN(list, 14, 0, 2);   // Broth×2
        AddN(list, 4, 0, 1);   // Crackers×1
        AddN(list, 18, 0, 1);   // PeanutButter×1
        AddN(list, 16, 0, 1);   // MapleSyrup×1
        AddN(list, 24, 0, 1);   // Carrot×1
        // == 15 kitchen slots ==
        // Behind locked doors
        AddN(list, 9, 8, 1);   // Cheese×1         — Cheese Cabinet (door 8)
        AddN(list, 21, 1, 1);   // Blueberries×1    — Bedroom (door 1)
        AddN(list, 3, 1, 1);   // Chips×1           — Bedroom
        AddN(list, 5, 1, 1);   // Apple×1           — Bedroom
        AddN(list, 1, 1, 1);   // ChocolateBar×1   — Bedroom
        AddN(list, 12, 1, 1);   // Bacon×1           — Bedroom
        AddN(list, 22, 9, 1);   // Tortilla×1       — Shed (door 9)
        // Rose (type 19) PINNED — puzzle item, not shuffled
        // Cereal (type 23) PINNED — game-ending item, not shuffled
        // Mushroom (type 6) PINNED — stump spawner, deferred for future
        AddN(list, 20, 3, 1);   // Avocado×1        — Oddities Room (door 3)
        AddN(list, 13, 3, 1);   // Beans×1           — Oddities Room
        AddN(list, 8, 2, 1);   // Chicken×1        — Bathroom (door 2)
        AddN(list, 7, 2, 1);   // Fish×1            — Bathroom
        // == 11 behind-door slots ==
        Slots = list.ToArray();

        if (Slots.Length != TOTAL_SLOTS)
            throw new Exception($"IngredientRandomizer: Expected {TOTAL_SLOTS} slots, got {Slots.Length}");
    }

    static void AddN(List<IngSlot> list, int type, int door, int count)
    {
        for (int i = 0; i < count; i++)
            list.Add(new IngSlot { OrigType = type, Door = door });
    }

    // ---------------------------------------------------------------
    // Instance state
    // ---------------------------------------------------------------
    private int[] _shuffled;                          // _shuffled[i] = new UnlockableItemID for slot i
    private Dictionary<int, int> _typeCounter = new();// runtime: tracks which slot index per original type
    private bool _initialized = false;
    private string _savePath;
    private System.Random _rng;

    // Visual swap cache — shared across instances
    public struct CachedVisuals
    {
        public FoodData FoodData;
        public Mesh Mesh;
        public Material[] Materials;
        public string ItemLabel;
    }
    private static Dictionary<int, CachedVisuals> _visualsCache = new();
    private static bool _visualsCached = false;

    public IngredientRandomizer(string savePath) { _savePath = savePath; }
    public void SetRng(System.Random rng) { _rng = rng; }
    public bool HasMapping => _shuffled != null && _shuffled.Length > 0;
    public int[] GetShuffled() => _shuffled;

    public void PrepareForNewGame()
    {
        _shuffled = null;
        _typeCounter.Clear();
        _initialized = false;
        // Mark cache for re-scan but keep existing entries as fallback.
        // Scene reload destroys objects, so cached Mesh/Material references
        // from the previous game may be stale. Re-scanning on the next
        // gameplay scene will overwrite them with fresh references.
        _visualsCached = false;
        if (File.Exists(_savePath)) File.Delete(_savePath);
        Plugin.Log.LogInfo("Ingredient randomizer reset for new game.");
    }

    public void ResetForRetry()
    {
        _shuffled = null;
        _typeCounter.Clear();
        _initialized = false;
        if (File.Exists(_savePath)) File.Delete(_savePath);
    }

    public void Initialize()
    {
        if (_initialized) return;
        if (!Plugin.EnableIngredientShuffle.Value) { _initialized = true; return; }

        if (File.Exists(_savePath) && LoadMapping())
        {
            _initialized = true;
            return;
        }

        Plugin.Log.LogInfo("INGREDIENT INIT: Generating ingredient shuffle...");

        // Safety: if no RNG set (e.g., continuing old save with new config), use random
        if (_rng == null)
        {
            Plugin.Log.LogWarning("INGREDIENT INIT: No RNG set, using random seed");
            _rng = new System.Random();
        }

        // Build pool = all original types (preserving counts)
        var pool = new int[TOTAL_SLOTS];
        for (int i = 0; i < TOTAL_SLOTS; i++)
            pool[i] = Slots[i].OrigType;

        // Fisher-Yates shuffle
        for (int i = pool.Length - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        _shuffled = pool;
        _initialized = true;
        SaveMapping();

        int changes = 0;
        for (int i = 0; i < TOTAL_SLOTS; i++)
        {
            if (Slots[i].OrigType != _shuffled[i])
            {
                changes++;
                string orig = TypeToName.GetValueOrDefault(Slots[i].OrigType, $"?{Slots[i].OrigType}");
                string news = TypeToName.GetValueOrDefault(_shuffled[i], $"?{_shuffled[i]}");
                string doorName = Slots[i].Door > 0
                    ? KeyRandomizer.DoorNames.GetValueOrDefault(Slots[i].Door, $"Door{Slots[i].Door}")
                    : "Kitchen";
                Plugin.Log.LogInfo($"INGREDIENT: [{doorName}] {orig} → {news}");
            }
        }
        Plugin.Log.LogInfo($"INGREDIENT SHUFFLE: {changes}/{TOTAL_SLOTS} slots changed.");
    }

    // ---------------------------------------------------------------
    // Runtime swap lookups
    // ---------------------------------------------------------------

    /// <summary>
    /// Called from ItemUnlockable.Start for non-mushroom items.
    /// Returns the new UnlockableItemID for this spawn, or -1 if no swap.
    /// Consumes the next slot matching the original type.
    /// </summary>
    public int GetSwapForType(int originalType)
    {
        if (_shuffled == null || originalType <= 0) return -1;

        int counter = _typeCounter.GetValueOrDefault(originalType, 0);
        _typeCounter[originalType] = counter + 1;

        // Find the (counter)-th slot with this original type
        int seen = 0;
        for (int i = 0; i < TOTAL_SLOTS; i++)
        {
            if (Slots[i].OrigType == originalType)
            {
                if (seen == counter) return _shuffled[i];
                seen++;
            }
        }

        // Overflow: more items of this type seen than expected
        Plugin.Log.LogWarning($"INGREDIENT: Overflow for type {originalType} (counter={counter}, expected={seen})");
        return originalType;  // Keep original
    }

    // ---------------------------------------------------------------
    // Feasibility data — modified ingredient distributions
    // ---------------------------------------------------------------

    /// <summary>Finite ingredients available without any door (kitchen/outside).</summary>
    public Dictionary<string, int> GetShuffledBaseIngredients()
    {
        var result = new Dictionary<string, int>();
        if (_shuffled == null) return result;
        for (int i = 0; i < TOTAL_SLOTS; i++)
        {
            if (Slots[i].Door != 0) continue;
            if (!TypeToName.TryGetValue(_shuffled[i], out string name)) continue;
            result[name] = result.GetValueOrDefault(name, 0) + 1;
        }

        // Add pinned items at their vanilla locations
        // Mushroom×8 in Kitchen/Outside (door 0)
        result["Mushroom"] = result.GetValueOrDefault("Mushroom", 0) + 8;
        // Bread and Water have no UnlockableItemID but are always in Kitchen
        result["Bread"] = result.GetValueOrDefault("Bread", 0) + 1;
        result["Water"] = result.GetValueOrDefault("Water", 0) + 1;

        return result;
    }

    /// <summary>Finite ingredients unlocked per door.</summary>
    public Dictionary<int, Dictionary<string, int>> GetShuffledDoorIngredients()
    {
        var result = new Dictionary<int, Dictionary<string, int>>();
        if (_shuffled == null) return result;
        for (int i = 0; i < TOTAL_SLOTS; i++)
        {
            int door = Slots[i].Door;
            if (door == 0) continue;
            if (!TypeToName.TryGetValue(_shuffled[i], out string name)) continue;
            if (!result.ContainsKey(door)) result[door] = new Dictionary<string, int>();
            result[door][name] = result[door].GetValueOrDefault(name, 0) + 1;
        }

        // Add pinned items at their vanilla locations
        // Rose×1 at Shed (door 9), Cereal×1 at Cereal Cabinet (door 12)
        if (!result.ContainsKey(9)) result[9] = new Dictionary<string, int>();
        result[9]["Rose"] = result[9].GetValueOrDefault("Rose", 0) + 1;
        if (!result.ContainsKey(12)) result[12] = new Dictionary<string, int>();
        result[12]["Cereal"] = result[12].GetValueOrDefault("Cereal", 0) + 1;

        return result;
    }

    // ---------------------------------------------------------------
    // Visual swap cache and application
    // ---------------------------------------------------------------

    public static void EnsureVisualsCached()
    {
        // Re-scan if not yet cached or if cache is incomplete
        if (_visualsCached && _visualsCache.Count >= 20) return;

        try
        {
            // Scan all ItemUnlockable instances (scene objects + asset prefabs)
            // Two-pass: first prefer "Raw" / non-cooked variants, then fill gaps
            var allUnlockables = Resources.FindObjectsOfTypeAll<ItemUnlockable>();

            // Pass 1: Prefer raw/uncooked variants — OVERWRITE existing entries
            // so stale references from previous scenes get replaced
            foreach (var u in allUnlockables)
            {
                int typeId = (int)u.GetUnlockItemID();
                if (typeId <= 0) continue;

                var go = u.gameObject;
                string goName = go.name;

                // Skip cooked/burnt variants — we want the raw pickup version
                if (goName.Contains("Burnt") || goName.Contains("Fried") || goName.Contains("Cooked"))
                    continue;
                // Skip Pantry copies (they may have different scale/setup)
                if (goName.Contains("Pantry")) continue;

                var mf = go.GetComponent<MeshFilter>();
                if (mf == null) mf = go.GetComponentInChildren<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue; // Need valid mesh

                var mr = go.GetComponent<MeshRenderer>();
                if (mr == null) mr = go.GetComponentInChildren<MeshRenderer>();
                var item = go.GetComponent<Item>();

                _visualsCache[typeId] = new CachedVisuals
                {
                    FoodData = item != null ? item.m_FoodData : null,
                    Mesh = mf.sharedMesh,
                    Materials = mr != null ? mr.sharedMaterials : null,
                    ItemLabel = item != null ? item.m_sItemLabel : ""
                };
            }

            // Pass 2: Fill any missing types with whatever we can find
            foreach (var u in allUnlockables)
            {
                int typeId = (int)u.GetUnlockItemID();
                if (typeId <= 0 || _visualsCache.ContainsKey(typeId)) continue;

                var go = u.gameObject;
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null) mf = go.GetComponentInChildren<MeshFilter>();
                var mr = go.GetComponent<MeshRenderer>();
                if (mr == null) mr = go.GetComponentInChildren<MeshRenderer>();
                var item = go.GetComponent<Item>();

                _visualsCache[typeId] = new CachedVisuals
                {
                    FoodData = item != null ? item.m_FoodData : null,
                    Mesh = mf != null ? mf.sharedMesh : null,
                    Materials = mr != null ? mr.sharedMaterials : null,
                    ItemLabel = item != null ? item.m_sItemLabel : ""
                };
            }

            int prevCount = _visualsCached ? _visualsCache.Count : 0;

            Plugin.Log.LogInfo($"INGREDIENT CACHE: Cached visuals for {_visualsCache.Count} ingredient types.{(_visualsCached ? " (re-scan)" : "")}");
            _visualsCached = true;

            // Only log individual types on first full cache or when new types found
            if (_visualsCache.Count > prevCount)
            {
                foreach (var kvp in _visualsCache)
                {
                    string name = TypeToName.GetValueOrDefault(kvp.Key, $"?{kvp.Key}");
                    Plugin.Log.LogInfo($"  CACHE: type {kvp.Key} ({name}) mesh={kvp.Value.Mesh?.name ?? "null"} food={kvp.Value.FoodData?.name ?? "null"}");
                }
            }
        }
        catch (Exception ex) { Plugin.Log.LogError($"INGREDIENT CACHE ERROR: {ex.Message}"); }
    }

    /// <summary>
    /// Apply a visual/component swap to an existing ingredient GameObject.
    /// Changes mesh, material, FoodData, UnlockableItemID, and label.
    /// </summary>
    public static void ApplyVisualSwap(GameObject go, int newType, int origType)
    {
        EnsureVisualsCached();

        if (!_visualsCache.TryGetValue(newType, out var visuals))
        {
            Plugin.Log.LogWarning($"ING SWAP: No cached visuals for type {newType} ({TypeToName.GetValueOrDefault(newType, "?")})");
            // Still change the ID even without visuals
            var u = go.GetComponent<ItemUnlockable>();
            if (u != null)
            {
                u.m_UnlockableItemID = (UnlockableItemID)newType;
                u.m_bForceSpawn = true;
            }
            return;
        }

        // Swap mesh
        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) mf = go.GetComponentInChildren<MeshFilter>();
        if (mf != null && visuals.Mesh != null) mf.sharedMesh = visuals.Mesh;

        // Swap materials
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) mr = go.GetComponentInChildren<MeshRenderer>();
        if (mr != null && visuals.Materials != null) mr.sharedMaterials = visuals.Materials;

        // Swap FoodData on the Item component
        var item = go.GetComponent<Item>();
        if (item != null && visuals.FoodData != null) item.m_FoodData = visuals.FoodData;
        if (item != null && !string.IsNullOrEmpty(visuals.ItemLabel)) item.m_sItemLabel = visuals.ItemLabel;

        // Swap UnlockableItemID and force spawn (so it doesn't self-destruct)
        var unlockable = go.GetComponent<ItemUnlockable>();
        if (unlockable != null)
        {
            unlockable.m_UnlockableItemID = (UnlockableItemID)newType;
            unlockable.m_bForceSpawn = true;
        }

        // Remove MushroomItem component if swapping away from mushroom
        if (origType == 6 && newType != 6)
        {
            var mushComp = go.GetComponent<MushroomItem>();
            if (mushComp != null) UnityEngine.Object.Destroy(mushComp);
        }

        string origName = TypeToName.GetValueOrDefault(origType, $"?{origType}");
        string newName = TypeToName.GetValueOrDefault(newType, $"?{newType}");
        Plugin.Log.LogInfo($"ING SWAP: {origName} → {newName} on '{go.name}'");
    }

    // ---------------------------------------------------------------
    // Save / Load
    // ---------------------------------------------------------------

    public void SaveMapping()
    {
        if (_shuffled == null) return;
        try
        {
            var lines = new List<string> { "{" };
            for (int i = 0; i < _shuffled.Length; i++)
            {
                string comma = i < _shuffled.Length - 1 ? "," : "";
                lines.Add($"  \"{i}\": \"{_shuffled[i]}\"{comma}");
            }
            lines.Add("}");
            File.WriteAllLines(_savePath, lines);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Failed to save ingredient mapping: {ex.Message}"); }
    }

    public bool LoadMapping()
    {
        try
        {
            if (!File.Exists(_savePath)) return false;
            string json = File.ReadAllText(_savePath);
            var mapping = new Dictionary<int, int>();
            foreach (var line in json.Split('\n'))
            {
                var trimmed = line.Trim().TrimEnd(',');
                if (trimmed.StartsWith("\""))
                {
                    int ci = trimmed.IndexOf(':');
                    if (ci > 0)
                    {
                        string key = trimmed.Substring(0, ci).Trim().Trim('"');
                        string value = trimmed.Substring(ci + 1).Trim().Trim('"');
                        if (int.TryParse(key, out int k) && int.TryParse(value, out int v))
                            mapping[k] = v;
                    }
                }
            }

            if (mapping.Count != TOTAL_SLOTS)
            {
                Plugin.Log.LogWarning($"INGREDIENT LOAD: Expected {TOTAL_SLOTS} entries, got {mapping.Count}");
                return false;
            }

            _shuffled = new int[TOTAL_SLOTS];
            for (int i = 0; i < TOTAL_SLOTS; i++)
                _shuffled[i] = mapping.TryGetValue(i, out int v) ? v : Slots[i].OrigType;

            Plugin.Log.LogInfo($"INGREDIENT LOAD: Loaded mapping with {TOTAL_SLOTS} entries.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Failed to load ingredient mapping: {ex.Message}");
            return false;
        }
    }
}

// =====================================================================
// Harmony Patches
// =====================================================================

[HarmonyPatch(typeof(NewGameButton), nameof(NewGameButton.StartNewGame))]
public class NewGamePatch
{
    // Guard against double-fire: the game's UI can trigger StartNewGame multiple
    // times in one frame (persistent Inspector listeners + our onClick.Invoke()).
    // The second call would overwrite the correct shuffle with a random seed.
    static bool _newGameStarted = false;
    public static void AllowNewGame() { _newGameStarted = false; }

    static void Postfix()
    {
        if (_newGameStarted)
        {
            Plugin.Log.LogInfo("New game button pressed! (duplicate call, ignoring)");
            return;
        }
        _newGameStarted = true;

        Plugin.Log.LogInfo("New game button pressed!");
        Plugin.Randomizer.PrepareForNewGame();
        Plugin.KeyRando.PrepareForNewGame();
        Plugin.IngredientRando.PrepareForNewGame();
        IngredientSwapPatch.Reset();
        KeyRewardGifterPatch.Reset();
        CrestHalfSwapPatch.ResetTracking();

        // Determine seed: resume uses saved seed, new game uses config or random
        int seed;
        if (Plugin.IsResumeAsNewGame)
        {
            seed = Plugin.ActiveSeed;  // Already loaded by MainMenuResumePatch
            Plugin.IsResumeAsNewGame = false;
            Plugin.Log.LogInfo($"RESUME AS NEW GAME: Using saved seed {seed}");
        }
        else
        {
            seed = Plugin.SeedConfig.Value;
            if (seed == 0)
                seed = new System.Random().Next(1, 999_999_999);
        }
        Plugin.ActiveSeed = seed;

        // Create one shared RNG from the seed — key shuffle consumes
        // from it first, then ingredient shuffle, then card shuffle.
        var rng = new System.Random(seed);
        Plugin.KeyRando.SetRng(rng);
        Plugin.IngredientRando.SetRng(rng);
        Plugin.Randomizer.SetRng(rng);

        Plugin.Log.LogInfo($"========================================");
        Plugin.Log.LogInfo($"  SEED: {seed}");
        Plugin.Log.LogInfo($"  Pantry Shuffle: {Plugin.EnablePantryShuffle.Value}");
        Plugin.Log.LogInfo($"  Ingredient Shuffle: {Plugin.EnableIngredientShuffle.Value}");
        Plugin.Log.LogInfo($"========================================");

        // Persist seed so it survives continue-game and can be shared
        try { File.WriteAllText(Plugin.SeedPath, seed.ToString()); }
        catch (Exception ex) { Plugin.Log.LogError($"Failed to save seed: {ex.Message}"); }

        Plugin.IsNewGame = true;
        MainMenuResumePatch.Reset();
    }
}

[HarmonyPatch(typeof(MainMenuSetup), nameof(MainMenuSetup.Start))]
public class MainMenuVersionPatch
{
    static void Postfix(MainMenuSetup __instance)
    {
        try
        {
            // Find the main menu canvas from the MainMenuSetup's own hierarchy
            var canvas = __instance.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                // Fallback: search for "MainMenuCanvas" by name
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                {
                    if (c.gameObject.name.Contains("MainMenu"))
                    {
                        canvas = c;
                        break;
                    }
                }
            }
            if (canvas == null)
            {
                Plugin.Log.LogWarning("VERSION LABEL: No main menu canvas found");
                return;
            }

            // Create version label GameObject
            var go = new GameObject("ModVersionLabel");
            go.transform.SetParent(canvas.transform, false);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = $"{Plugin.MOD_NAME} v{Plugin.VERSION}";
            tmp.fontSize = 16;
            tmp.color = new Color(1f, 1f, 1f, 0.5f);
            tmp.alignment = TextAlignmentOptions.BottomRight;
            tmp.raycastTarget = false;

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(-20, 10);
            rect.sizeDelta = new Vector2(400, 30);

            Plugin.Log.LogInfo($"VERSION LABEL: Added '{tmp.text}' to main menu");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"VERSION LABEL ERROR: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(DisableMenuButtons), nameof(DisableMenuButtons.EnableButtons))]
public class MainMenuResumePatch
{
    /// <summary>
    /// Rewires the Resume Game button on the main menu to start a fresh new game
    /// with the previously saved seed instead of loading the save file.
    /// Hooks EnableButtons because the ResumeGame component doesn't exist yet
    /// when MainMenuSetup.Start fires.
    /// </summary>
    static bool _rewired = false;

    public static void Reset() { _rewired = false; }

    static void Postfix()
    {
        // Reset the double-fire guard so a new game can be started from this menu
        NewGamePatch.AllowNewGame();

        if (_rewired) return;

        if (!File.Exists(Plugin.SeedPath)) { _rewired = true; return; }

        try
        {
            // Find the resume button by its known name (discovered via diagnostic scan)
            GameObject resumeGO = null;
            foreach (var btn in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (btn.gameObject.name == "ResumeGameButton")
                {
                    resumeGO = btn.gameObject;
                    break;
                }
            }
            if (resumeGO == null) return;  // Not on main menu yet

            var button = resumeGO.GetComponent<Button>();
            if (button == null) { _rewired = true; return; }

            string seedStr = File.ReadAllText(Plugin.SeedPath).Trim();
            if (!int.TryParse(seedStr, out int savedSeed) || savedSeed == 0)
            {
                _rewired = true;
                return;
            }

            _pendingResumeSeed = savedSeed;

            // CRITICAL: RemoveAllListeners only removes RUNTIME listeners.
            // The 4 persistent listeners (set in Unity Editor) survive and still
            // call TriggerResume, causing both a save load AND new game simultaneously.
            // Replace the entire onClick event to clear everything.
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener((UnityAction)OnResumeButtonClicked);

            // Also destroy the ResumeGame component so TriggerResume can't fire
            // even if something else references it
            var resumeComp = resumeGO.GetComponent<ResumeGame>();
            if (resumeComp != null)
                UnityEngine.Object.Destroy(resumeComp);

            _rewired = true;
            Plugin.Log.LogInfo($"MENU: Resume button rewired to new game with seed {savedSeed}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"MENU RESUME SETUP ERROR: {ex.Message}");
        }
    }

    static int _pendingResumeSeed = 0;

    static void OnResumeButtonClicked()
    {
        Plugin.Log.LogInfo($"RESUME: Button clicked — starting new game with seed {_pendingResumeSeed}");
        Plugin.ActiveSeed = _pendingResumeSeed;
        Plugin.IsResumeAsNewGame = true;

        // Find and invoke the actual New Game button through its UI Button component
        // to go through the proper UI event chain (transition, input management, etc.)
        var newGameBtn = UnityEngine.Object.FindObjectOfType<NewGameButton>();
        if (newGameBtn != null)
        {
            newGameBtn.m_bCheckForSavedData = false;
            var ngButton = newGameBtn.GetComponent<Button>();
            if (ngButton != null)
            {
                Plugin.Log.LogInfo("RESUME: Invoking New Game button onClick");
                ngButton.onClick.Invoke();
            }
            else
            {
                Plugin.Log.LogInfo("RESUME: No Button on NewGameButton, calling StartNewGame directly");
                newGameBtn.StartNewGame();
            }
        }
        else
        {
            Plugin.Log.LogError("RESUME: No NewGameButton found!");
            Plugin.IsResumeAsNewGame = false;
        }
    }
}

[HarmonyPatch(typeof(RecipeCard), nameof(RecipeCard.Start))]
public class RecipeCardStartPatch
{
    static void Postfix(RecipeCard __instance)
    {
        if (!Plugin.EnableCardShuffle.Value) return;

        if (!Plugin.IsNewGame && !Plugin.Randomizer.HasMapping)
            Plugin.Randomizer.LoadCollected();

        var newCard = Plugin.Randomizer.GetCardForLocation(__instance);
        if (newCard != null)
        {
            var original = __instance.m_RecipeCardUnlocked;
            string origName = original != null ? original.GetRecipeTitle() : "null";
            Plugin.Log.LogInfo($"SWAP: {origName} -> {newCard.GetRecipeTitle()}");
            __instance.m_RecipeCardUnlocked = newCard;
        }
    }
}

[HarmonyPatch(typeof(RecipeCard), nameof(RecipeCard.ObtainCard))]
public class RecipeCardObtainPatch
{
    static void Postfix(RecipeCard __instance)
    {
        if (!Plugin.EnableCardShuffle.Value) return;
        var cardData = __instance.m_RecipeCardUnlocked;
        if (cardData == null) return;
        Plugin.Randomizer.CollectRecipe(cardData.GetRecipeTitle());
    }
}

[HarmonyPatch(typeof(RecipeMasterList), nameof(RecipeMasterList.GetCreatedMeal))]
public class MistakeIfNotCollectedPatch
{
    static void Postfix(RecipeMasterList __instance, ref RecipeData __result)
    {
        if (!Plugin.EnableMistakeSystem.Value || !Plugin.EnableCardShuffle.Value) return;
        if (__result == null) return;

        var foodData = __result.GetFoodData();
        string foodName = foodData != null ? foodData.name : null;
        string cardTitle = FindCardTitleForRecipe(__result);

        bool isKnown = false;
        if (foodName != null && Plugin.Randomizer.IsRecipeCollected(foodName)) isKnown = true;
        if (!isKnown && cardTitle != null && Plugin.Randomizer.IsRecipeCollected(cardTitle)) isKnown = true;

        if (isKnown) return;

        var mistakeList = __instance.m_MistakeMealData;
        if (mistakeList != null && mistakeList.Count > 0)
        {
            Plugin.Log.LogInfo($"COOK BLOCKED: {cardTitle ?? foodName} - not collected! Forcing Mistake.");
            __result = mistakeList[0];
        }
    }

    static string FindCardTitleForRecipe(RecipeData recipe)
    {
        try
        {
            var allCards = Resources.FindObjectsOfTypeAll<RecipeCardData>();
            if (allCards == null) return null;
            foreach (var card in allCards)
                if (card.GetRecipeDataFromCard() == recipe) return card.GetRecipeTitle();
        }
        catch (Exception ex) { Plugin.Log.LogError($"Error finding card title: {ex.Message}"); }
        return null;
    }
}

// =====================================================================
// Key Shuffle Patches
// =====================================================================

[HarmonyPatch(typeof(Key), nameof(Key.Start))]
public class KeyStartPatch
{
    static void Postfix(Key __instance)
    {
        if (!Plugin.EnableKeyShuffle.Value) return;
        if (KeyRandomizer.AlreadyRemappedKeys.Contains(__instance.GetInstanceID())) return;

        int currentId = (int)__instance.GetKeyID();

        Plugin.KeyRando.Initialize();

        if (Plugin.EnablePantryShuffle.Value)
        {
            int? remapped = Plugin.KeyRando.GetRemappedLockId(currentId);
            if (!remapped.HasValue || remapped.Value == currentId) return;

            int newItem = remapped.Value;

            // If remapped to a crest half, swap the object entirely
            if (newItem == KeyRandomizer.CREST_LEFT || newItem == KeyRandomizer.CREST_RIGHT)
            {
                try
                {
                    var go = __instance.gameObject;
                    var crest = go.AddComponent<CrestHalf>();
                    crest.m_CrestSide = KeyRandomizer.NextCrestSide();

                    // Track so CrestHalfSwapPatch skips this when CrestHalf.Start fires
                    CrestHalfSwapPatch.TrackConverted(crest.GetInstanceID());

                    var item = go.GetComponent<Item>();
                    if (item != null) item.m_SecondaryFunctionality = null;

                    UnityEngine.Object.Destroy(__instance);
                    Plugin.Log.LogInfo($"KEY START: Key DoorLockID {currentId} -> {KeyRandomizer.ItemName(newItem)} (crest swap, side={crest.m_CrestSide})");
                }
                catch (Exception ex) { Plugin.Log.LogError($"KEY START CREST ERROR: {ex.Message}"); }
                return;
            }

            // Normal key remap
            if (KeyRandomizer.IsKey(newItem))
            {
                __instance.m_KeyID = (DoorLockID)newItem;
                KeyRandomizer.AlreadyRemappedKeys.Add(__instance.GetInstanceID());
                Plugin.Log.LogInfo($"KEY START: DoorLockID {currentId} -> {newItem} ({KeyRandomizer.ItemName(newItem)})");
            }
        }
    }
}

[HarmonyPatch(typeof(CreatureRewardGifter), nameof(CreatureRewardGifter.SpawnRewardItem))]
public class KeyRewardGifterPatch
{
    // Cached prefab references (grabbed from creatures at runtime)
    static GameObject _cachedKeyPrefab = null;
    static GameObject _cachedCrestPrefab = null;
    static bool _prefabsCached = false;

    static HashSet<int> _keysBefore;
    static HashSet<int> _crestsBefore;
    static int _targetItem = -1, _creatureId = -1;
    static bool _isMothGifter = false;
    static GameObject _originalPrefab = null; // to restore after spawn
    static GameObject _originalFinalPrefab = null; // to restore m_FinalRoomKeyPrefabReward

    // Flag so CrestHalfSwapPatch can skip creature-reward crests (FixCrestSide handles them)
    public static bool SpawnInProgress = false;

    public static void Reset() { _cachedKeyPrefab = null; _cachedCrestPrefab = null; _prefabsCached = false; }

    public static GameObject CachedKeyPrefab => _cachedKeyPrefab;
    public static GameObject CachedCrestPrefab => _cachedCrestPrefab;

    public static void EnsurePrefabsCached() { CachePrefabs(); }

    static void CachePrefabs()
    {
        if (_prefabsCached) return;
        _prefabsCached = true;

        // Scan ALL reward gifters in the scene to find key and crest prefabs
        foreach (var gifter in UnityEngine.Object.FindObjectsOfType<CreatureRewardGifter>())
        {
            var prefab = gifter.m_PrefabReward;
            if (prefab == null) continue;

            if (_cachedKeyPrefab == null && prefab.GetComponent<Key>() != null)
            {
                _cachedKeyPrefab = prefab;
                Plugin.Log.LogInfo($"PREFAB CACHE: Found Key prefab from {gifter.gameObject.name}");
            }
            if (_cachedCrestPrefab == null && prefab.GetComponent<CrestHalf>() != null)
            {
                _cachedCrestPrefab = prefab;
                Plugin.Log.LogInfo($"PREFAB CACHE: Found Crest prefab from {gifter.gameObject.name}");
            }

            if (_cachedKeyPrefab != null && _cachedCrestPrefab != null) break;
        }
    }

    static void Prefix(CreatureRewardGifter __instance)
    {
        _targetItem = -1; _creatureId = -1; _keysBefore = null; _crestsBefore = null;
        _isMothGifter = false; _originalPrefab = null; _originalFinalPrefab = null;
        SpawnInProgress = true;
        if (!Plugin.EnableKeyShuffle.Value) return;

        var reward = __instance.m_PrefabReward;
        if (reward == null) return;

        // Ensure we have both prefab references cached
        CachePrefabs();

        _creatureId = KeyRandomizer.IdentifyCreatureFromGifter(__instance);

        // Detect Moth: reward has CrestHalf, or creature type is MOTH
        bool hasCrestReward = reward.GetComponent<CrestHalf>() != null;
        _isMothGifter = (_creatureId == KeyRandomizer.MOTH) || hasCrestReward;

        // Look up target item from the correct slot
        bool fullShuffle = Plugin.EnablePantryShuffle.Value;
        int target = -1;
        if (fullShuffle && _isMothGifter)
        {
            target = Plugin.KeyRando.GetSlotItem(KeyRandomizer.SLOT_MOTH);
        }
        else if (_creatureId > 0)
        {
            int? t = Plugin.KeyRando.GetShuffledLockId(_creatureId);
            if (t.HasValue) target = t.Value;
        }

        if (target < 0) return;
        _targetItem = target;

        bool isCrestTarget = (_targetItem == KeyRandomizer.CREST_LEFT || _targetItem == KeyRandomizer.CREST_RIGHT);

        // === Prefab swap in Prefix (before SpawnRewardItem runs) ===
        if (_isMothGifter && !isCrestTarget && KeyRandomizer.IsKey(_targetItem))
        {
            // Moth should give a Key, not a CrestHalf. Swap the prefab.
            if (_cachedKeyPrefab != null)
            {
                _originalPrefab = __instance.m_PrefabReward;
                __instance.m_PrefabReward = _cachedKeyPrefab;
                // Also swap the fallback reward in case the game uses it
                _originalFinalPrefab = __instance.m_FinalRoomKeyPrefabReward;
                __instance.m_FinalRoomKeyPrefabReward = _cachedKeyPrefab;
                Plugin.Log.LogInfo($"PREFAB SWAP: Moth prefab -> Key prefab (target={KeyRandomizer.ItemName(_targetItem)})");
            }
            else
                Plugin.Log.LogWarning("PREFAB SWAP: No cached Key prefab available for Moth swap!");
        }
        else if (!_isMothGifter && isCrestTarget)
        {
            // Key creature should give a CrestHalf, not a Key. Swap the prefab.
            if (_cachedCrestPrefab != null)
            {
                _originalPrefab = __instance.m_PrefabReward;
                __instance.m_PrefabReward = _cachedCrestPrefab;
                // Also swap the fallback reward — SpawnRewardItem may use it if
                // the creature's vanilla door was already unlocked by another creature
                _originalFinalPrefab = __instance.m_FinalRoomKeyPrefabReward;
                __instance.m_FinalRoomKeyPrefabReward = _cachedCrestPrefab;
                Plugin.Log.LogInfo($"PREFAB SWAP: Creature {_creatureId} prefab -> Crest prefab (target={KeyRandomizer.ItemName(_targetItem)})");
            }
            else
                Plugin.Log.LogWarning($"PREFAB SWAP: No cached Crest prefab for creature {_creatureId}!");
        }

        // Snapshot Keys and CrestHalves before spawn
        _keysBefore = new HashSet<int>();
        foreach (var k in UnityEngine.Object.FindObjectsOfType<Key>())
            _keysBefore.Add(k.GetInstanceID());

        _crestsBefore = new HashSet<int>();
        foreach (var c in UnityEngine.Object.FindObjectsOfType<CrestHalf>())
            _crestsBefore.Add(c.GetInstanceID());

        Plugin.Log.LogInfo($"KEY GIFTER PRE: Creature {_creatureId} (moth={_isMothGifter}) target={KeyRandomizer.ItemName(_targetItem)}");
    }

    static void Postfix(CreatureRewardGifter __instance)
    {
        // Restore original prefabs so we don't corrupt the gifter for save/load
        if (_originalPrefab != null)
        {
            __instance.m_PrefabReward = _originalPrefab;
            _originalPrefab = null;
        }
        if (_originalFinalPrefab != null)
        {
            __instance.m_FinalRoomKeyPrefabReward = _originalFinalPrefab;
            _originalFinalPrefab = null;
        }

        if (_targetItem < 0) return;

        try
        {
            bool isCrestTarget = (_targetItem == KeyRandomizer.CREST_LEFT || _targetItem == KeyRandomizer.CREST_RIGHT);

            if (isCrestTarget)
            {
                // Target is a crest half — fix the side on the newly spawned CrestHalf
                FixCrestSide();
            }
            else if (KeyRandomizer.IsKey(_targetItem))
            {
                // Target is a key — fix the DoorLockID on the newly spawned Key
                FixKeyId();
            }
        }
        catch (Exception ex) { Plugin.Log.LogError($"KEY GIFTER ERROR: {ex.Message}\n{ex.StackTrace}"); }

        _targetItem = -1; _keysBefore = null; _crestsBefore = null;
        SpawnInProgress = false;
    }

    static void FixKeyId()
    {
        if (_keysBefore == null) return;
        foreach (var key in UnityEngine.Object.FindObjectsOfType<Key>())
        {
            if (!_keysBefore.Contains(key.GetInstanceID()))
            {
                int cur = (int)key.GetKeyID();
                if (cur != _targetItem)
                {
                    key.m_KeyID = (DoorLockID)_targetItem;
                    Plugin.Log.LogInfo($"KEY GIFTER APPLIED: Creature {_creatureId} DoorLockID {cur} -> {_targetItem}");
                }
                else
                    Plugin.Log.LogInfo($"KEY GIFTER OK: Creature {_creatureId} key DoorLockID {cur}");

                KeyRandomizer.AlreadyRemappedKeys.Add(key.GetInstanceID());
                return;
            }
        }
        Plugin.Log.LogWarning($"KEY GIFTER: Creature {_creatureId} -- NO NEW KEY FOUND!");
    }

    static void FixCrestSide()
    {
        if (_crestsBefore == null) return;

        foreach (var crest in UnityEngine.Object.FindObjectsOfType<CrestHalf>())
        {
            if (!_crestsBefore.Contains(crest.GetInstanceID()))
            {
                // Progressive crest: first crest is always LEFT, second is RIGHT
                var newSide = KeyRandomizer.NextCrestSide();
                var curSide = crest.GetCrestHalfSide();
                if (curSide != newSide)
                {
                    crest.m_CrestSide = newSide;
                    Plugin.Log.LogInfo($"CREST SIDE FIX: Creature {_creatureId} crest {curSide} -> {newSide} (progressive)");
                }
                else
                    Plugin.Log.LogInfo($"CREST OK: Creature {_creatureId} crest side {curSide} (progressive)");

                // Track so CrestHalfSwapPatch skips this when CrestHalf.Start fires later
                // (Unity defers Start, so SpawnInProgress is already false by then)
                CrestHalfSwapPatch.TrackConverted(crest.GetInstanceID());
                return;
            }
        }
        Plugin.Log.LogWarning($"CREST FIX: Creature {_creatureId} -- NO NEW CREST FOUND!");
    }
}

[HarmonyPatch(typeof(CrowMovement), nameof(CrowMovement.Start))]
public class CrowRewardPatch
{
    static void Postfix(CrowMovement __instance)
    {
        if (!Plugin.EnableKeyShuffle.Value || !Plugin.EnablePantryShuffle.Value) return;

        Plugin.KeyRando.Initialize();
        int crowItem = Plugin.KeyRando.GetSlotItem(KeyRandomizer.SLOT_CROW);
        if (crowItem < 0) return;

        var reward = __instance.m_FullyHappyReward;
        if (reward == null)
        {
            Plugin.Log.LogWarning("CROW PATCH: m_FullyHappyReward is null!");
            return;
        }

        bool isCrestTarget = (crowItem == KeyRandomizer.CREST_LEFT || crowItem == KeyRandomizer.CREST_RIGHT);

        if (isCrestTarget)
        {
            // Crow should give a CrestHalf, need to swap prefab
            // Cache crest prefab from scene first
            KeyRewardGifterPatch.EnsurePrefabsCached();
            var crestPrefab = KeyRewardGifterPatch.CachedCrestPrefab;
            if (crestPrefab != null)
            {
                __instance.m_FullyHappyReward = crestPrefab;
                Plugin.Log.LogInfo($"CROW PATCH: Swapped reward prefab to Crest for {KeyRandomizer.ItemName(crowItem)}");
            }
            else
                Plugin.Log.LogWarning("CROW PATCH: No cached Crest prefab for Crow!");
        }
        else if (KeyRandomizer.IsKey(crowItem))
        {
            // Crow gives a Key — the vanilla reward is already a Key prefab (Fridge).
            // The DoorLockID will be fixed when KeyUseRemapPatch fires on use.
            // But let's also fix it at spawn via Key.Start for immediate visual correctness.
            var keyComp = reward.GetComponent<Key>();
            if (keyComp != null)
            {
                Plugin.Log.LogInfo($"CROW PATCH: Crow reward is Key (vanilla Fridge), will remap {(int)keyComp.GetKeyID()} -> {crowItem} on use");
            }
        }
    }
}

[HarmonyPatch(typeof(CrestHalf), nameof(CrestHalf.Start))]
public class CrestHalfSwapPatch
{
    // CrestHalf instances already assigned a progressive side by other patches.
    // Unity defers CrestHalf.Start(), so it fires AFTER the code that created/fixed
    // the crest. Without this tracking, CrestHalfSwapPatch would call NextCrestSide()
    // a second time on the same crest. Sources:
    //   - KeyStartPatch: table key → crest conversion via AddComponent<CrestHalf>
    //   - FixCrestSide: creature reward crests fixed in SpawnRewardItem Postfix
    static HashSet<int> _convertedCrestIds = new();
    public static void TrackConverted(int instanceId) { _convertedCrestIds.Add(instanceId); }
    public static void ResetTracking() { _convertedCrestIds.Clear(); }

    static void Postfix(CrestHalf __instance)
    {
        if (!Plugin.EnableKeyShuffle.Value || !Plugin.EnablePantryShuffle.Value) return;

        // Skip crests already assigned a progressive side by KeyStartPatch or FixCrestSide
        if (_convertedCrestIds.Contains(__instance.GetInstanceID())) return;

        bool isClone = __instance.gameObject.name.Contains("(Clone)");

        if (isClone)
        {
            // Clone crests come from creature rewards or crow rewards.
            // Creature reward crests are tracked via _convertedCrestIds (registered by
            // FixCrestSide in the SpawnRewardItem Postfix), so they're already skipped above.
            // Any clone reaching here is a Crow reward — assign progressive side.
            var newSide = KeyRandomizer.NextCrestSide();
            __instance.m_CrestSide = newSide;
            Plugin.Log.LogInfo($"PROGRESSIVE CREST: Crow clone '{__instance.gameObject.name}' -> {newSide}");
            return;
        }

        // Non-clone: Oddities Room puzzle crest (vanilla scene object, LEFT side)
        var side = __instance.GetCrestHalfSide();
        if (side != CrestHalf.CrestSide.LEFT) return;

        Plugin.KeyRando.Initialize();
        int puzzleItem = Plugin.KeyRando.GetSlotItem(KeyRandomizer.SLOT_ODDITIES_PUZZLE);
        if (puzzleItem < 0) return;

        // If the puzzle slot IS a crest half, assign progressive side
        if (puzzleItem == KeyRandomizer.CREST_LEFT || puzzleItem == KeyRandomizer.CREST_RIGHT)
        {
            var newSide = KeyRandomizer.NextCrestSide();
            __instance.m_CrestSide = newSide;
            Plugin.Log.LogInfo($"PROGRESSIVE CREST: Oddities puzzle -> {newSide}");
            return;
        }

        // Puzzle slot is a key — swap CrestHalf component to Key
        if (!KeyRandomizer.IsKey(puzzleItem)) return;

        try
        {
            var go = __instance.gameObject;
            var key = go.AddComponent<Key>();
            key.m_KeyID = (DoorLockID)puzzleItem;

            var item = go.GetComponent<Item>();
            if (item != null) item.m_SecondaryFunctionality = key;

            UnityEngine.Object.Destroy(__instance);

            KeyRandomizer.AlreadyRemappedKeys.Add(key.GetInstanceID());
            Plugin.Log.LogInfo($"CREST SWAP: Oddities puzzle CrestHalf -> {KeyRandomizer.ItemName(puzzleItem)}");
        }
        catch (Exception ex) { Plugin.Log.LogError($"CREST SWAP ODDITIES ERROR: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(CreatureRewardSpawner), nameof(CreatureRewardSpawner.Start))]
public class KeySpawnerInitPatch
{
    static void Postfix(CreatureRewardSpawner __instance)
    {
        if (!Plugin.EnableKeyShuffle.Value) return;
        Plugin.KeyRando.Initialize();
    }
}

[HarmonyPatch(typeof(Key), nameof(Key.SecondaryAction))]
public class KeyUseRemapPatch
{
    static void Prefix(Key __instance)
    {
        if (!Plugin.EnableKeyShuffle.Value) return;
        if (KeyRandomizer.AlreadyRemappedKeys.Contains(__instance.GetInstanceID())) return;

        int cur = (int)__instance.GetKeyID();
        int? remapped = Plugin.KeyRando.GetRemappedLockId(cur);
        if (remapped.HasValue && remapped.Value != cur)
        {
            // Don't remap to crest half IDs
            if (!KeyRandomizer.IsKey(remapped.Value)) return;

            Plugin.Log.LogInfo($"KEY USE REMAP: DoorLockID {cur} -> {remapped.Value}");
            __instance.m_KeyID = (DoorLockID)remapped.Value;
            KeyRandomizer.AlreadyRemappedKeys.Add(__instance.GetInstanceID());
        }
    }
}

[HarmonyPatch(typeof(ItemUnlockable), nameof(ItemUnlockable.OnItemPickedUp))]
public class IngredientDebugPatch
{
    static void Prefix(ItemUnlockable __instance)
    {
        string obj = __instance.gameObject.name;
        string parent = __instance.transform.parent != null ? __instance.transform.parent.gameObject.name : "no parent";
        Plugin.Log.LogInfo($"INGREDIENT: '{__instance.m_UnlockableItemID}' on '{obj}' under '{parent}'");
    }
}

// =====================================================================
// Ingredient Shuffle Patches
// =====================================================================

[HarmonyPatch(typeof(ItemUnlockable), nameof(ItemUnlockable.Start))]
public class IngredientSwapPatch
{
    // Track instance IDs we've already swapped (to avoid re-processing)
    static HashSet<int> _alreadySwapped = new();

    public static void Reset() { _alreadySwapped.Clear(); }
    public static void MarkSwapped(int instanceId) { _alreadySwapped.Add(instanceId); }

    /// <summary>Check if a transform is inside a Pantry, Fridge, or Freezer (infinite spawns).</summary>
    public static bool IsInfiniteSpawnSource(Transform t)
    {
        var current = t;
        int depth = 0;
        while (current != null && depth < 8)
        {
            string name = current.gameObject.name;
            if (name.Contains("PantrySegment") || name.Contains("Fridge") || name.Contains("Freezer"))
                return true;
            current = current.parent;
            depth++;
        }
        return false;
    }

    static void Postfix(ItemUnlockable __instance)
    {
        if (!Plugin.EnableIngredientShuffle.Value) return;

        int instanceId = __instance.GetInstanceID();
        if (_alreadySwapped.Contains(instanceId)) return;
        _alreadySwapped.Add(instanceId);

        int origType = (int)__instance.GetUnlockItemID();
        if (origType <= 0) return;

        // Skip mushroom type — not shuffled (stump spawner system, deferred)
        if (origType == 6) return;
        // Skip special mushroom types (Oyster/Morel/Honey/Amanita)
        if (origType >= 25 && origType <= 28) return;
        // Skip pinned types (Rose, Cereal) — these stay in vanilla locations
        if (IngredientRandomizer.PinnedTypes.Contains(origType)) return;

        // Skip items spawned by Pantry / Fridge / Freezer (infinite sources)
        if (IsInfiniteSpawnSource(__instance.transform))
        {
            return;
        }

        // Ensure ingredient shuffle is initialized
        Plugin.IngredientRando.Initialize();
        if (!Plugin.IngredientRando.HasMapping) return;

        int newType = Plugin.IngredientRando.GetSwapForType(origType);
        if (newType <= 0 || newType == origType) return;

        IngredientRandomizer.ApplyVisualSwap(__instance.gameObject, newType, origType);
    }
}