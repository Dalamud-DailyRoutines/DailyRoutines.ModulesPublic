using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using KamiToolKit.Timelines;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Dalamud.Helpers;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.KamiToolKit.Nodes.CircleButton;
using OmenTools.Utils.FuzzyMatcher;
using RecipeSearchContextEx = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.RecipeSearchContext;
using AgentId = Dalamud.Game.Agent.AgentId;
using AgentRecipeNoteEx = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentRecipeNote;

namespace DailyRoutines.ModulesPublic;

internal partial class OptimizedRecipeNote : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("OptimizedRecipeNoteTitle"),
        Description         = Lang.Get("OptimizedRecipeNoteDescription"),
        Category            = ModuleCategory.CraftGather,
        ModulesPrerequisite = ["AutoShowItemNPCShopInfo"],
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedRecipeNote/preview-2.webp",
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedRecipeNote/preview-1.webp"
        ]
    };

    public override string ModuleName => nameof(OptimizedRecipeNote);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private static readonly CompSig SimpleCraftGetAmountUpperLimitSig =
        new("4C 8B DC 48 83 EC ?? 48 8B 81 ?? ?? ?? ?? 44 0F B6 CA");
    private delegate int SimpleCraftGetAmountUpperLimitDelegate
    (
        nint agent,
        bool eventCase
    );
    private Hook<SimpleCraftGetAmountUpperLimitDelegate>? SimpleCraftGetAmountUpperLimitHook;

    private static readonly CompSig     SimpleCraftAmountJudgeSig   = new("0F 87 ?? ?? ?? ?? 48 8B 81 ?? ?? ?? ?? 48 85 C0");
    private                 MemoryPatch SimpleCraftAmountJudgePatch = null!;

    private static readonly CompSig RecipeNotePraticeSettingSetupSig = new
    (
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? BA ?? ?? ?? ?? 49 8B F0 48 8B E9 E8 ?? ?? ?? ?? 48 8D 4E ?? 48 8B D8 E8 ?? ?? ?? ?? 48 8B D0 48 8B CB E8 ?? ?? ?? ?? 48 8D 4E"
    );
    private unsafe delegate AtkValue* RecipeNotePraticeSettingSetupDelegate
    (
        AtkEventListener* listener,
        AtkValue*         returnValue,
        AtkValue*         values
    );
    private Hook<RecipeNotePraticeSettingSetupDelegate>? RecipeNotePraticeSettingSetupHook;

    private Hook<RecipeSearchContextEx.Delegates.ScanRecipeRows>? ScanRecipeRowsHook;
    private Hook<AgentRecipeNoteEx.Delegates.RefreshRecipeList>?  RefreshRecipeListHook;

    private FuzzyMatcher<RecipeSearchEntry>                recipeMatcher          = null!;
    private FrozenDictionary<string, Recipe[]>             exactRecipeMatches     = null!;
    private FrozenDictionary<ushort, RecipeSearchCategory> recipeSearchCategories = null!;

    private readonly Dictionary<ushort, int> originalSearchRecipeOrder = [];
    private          ushort[]                sortedSearchRecipeIDs     = [];
    private          bool                    isSearchViewSwitchPending;

    private Config config = null!;

    private readonly Dictionary<uint, CalculationResult> calculationResults = [];

    private TextButtonNode?    calculateRecipeButton;
    private TextButtonNode?    switchJobButton;
    private TextureButtonNode? clearSearchButton;
    private CircleButtonNode?  searchViewButton;

    private TextButtonNode?      displayOthersButton;
    private HorizontalListNode?  displayOthersJobsLayout;
    private List<IconButtonNode> displayOthersJobButtons = [];

    private List<IconButtonNode> materialSourceButtons = [];

    private TextButtonNode? levelRecipeButton;
    private TextButtonNode? specialRecipeButton;
    private TextButtonNode? masterRecipeButton;

    private DalamudLinkPayload? installRaphaelLinkPayload;
    private Task?               installRaphaelTask;

    private uint lastRecipeID;

    private int  pendingQuickSynthRemaining;
    private bool pendingUseHQIngredient;
    private bool pendingAllNQResult;

    protected override unsafe void Init()
    {
        // ja → nop
        SimpleCraftAmountJudgePatch = new
        (
            SimpleCraftAmountJudgeSig.Get(),
            [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
        );

        TaskHelper ??= new() { TimeoutMS = 15_000 };

        config = Config.Load(this) ?? new();

        RestoreSearchHistory();

        var searchEntries = LuminaGetter.Get<Recipe>()
                                        .Where(static recipe => recipe is { AmountResult: > 0, ItemResult.RowId: > 0 })
                                        .GroupBy(static recipe => recipe.ItemResult.RowId)
                                        .Select
                                        (static group => new RecipeSearchEntry
                                         (
                                             group.First().ItemResult.Value.Name.ExtractText(),
                                             [.. group.OrderBy(static recipe => recipe.DisplayPriority).ThenBy(static recipe => recipe.RowId)]
                                         )
                                        )
                                        .ToArray();
        recipeMatcher = new(searchEntries, static entry => [new(entry.Name, RECIPE_NAME_WEIGHT, RECIPE_PINYIN_WEIGHT)]);
        exactRecipeMatches = searchEntries.GroupBy(static entry => entry.Name, StringComparer.Ordinal)
                                          .ToFrozenDictionary
                                          (
                                              static group => group.Key,
                                              static group => group.SelectMany(static entry => entry.Recipes)
                                                                   .OrderBy(static recipe => recipe.DisplayPriority)
                                                                   .ThenBy(static recipe => recipe.RowId)
                                                                   .ToArray(),
                                              StringComparer.Ordinal
                                          );

        recipeSearchCategories = searchEntries.SelectMany(static entry => entry.Recipes)
                                              .ToDictionary(static recipe => (ushort)recipe.RowId, GetRecipeSearchCategory)
                                              .OrderBy(static entry => entry.Value.GroupID)
                                              .ThenBy(static entry => entry.Value.WeaponJob)
                                              .ThenBy(static entry => entry.Value.Slot)
                                              .ThenBy(static entry => entry.Value.DisplayPriority)
                                              .ThenBy(static entry => entry.Key)
                                              .Select(static (entry, index) => KeyValuePair.Create(entry.Key, entry.Value with { Order = index }))
                                              .ToFrozenDictionary();

        RefreshRecipeListHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentRecipeNoteEx.MemberFunctionPointers),
            nameof(AgentRecipeNoteEx.MemberFunctionPointers.RefreshRecipeList),
            (AgentRecipeNoteEx.Delegates.RefreshRecipeList)RefreshRecipeListDetour
        );
        RefreshRecipeListHook.Enable();

        ScanRecipeRowsHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(RecipeSearchContextEx.MemberFunctionPointers),
            nameof(RecipeSearchContextEx.MemberFunctionPointers.ScanRecipeRows),
            (RecipeSearchContextEx.Delegates.ScanRecipeRows)ScanRecipeRowsDetour
        );
        ScanRecipeRowsHook.Enable();

        SimpleCraftGetAmountUpperLimitHook =
            SimpleCraftGetAmountUpperLimitSig.GetHook<SimpleCraftGetAmountUpperLimitDelegate>(SimpleCraftGetAmountUpperLimitDetour);

        RecipeNotePraticeSettingSetupHook =
            RecipeNotePraticeSettingSetupSig.GetHook<RecipeNotePraticeSettingSetupDelegate>(RecipeNotePraticeSettingSetupDetour);

        SimpleCraftAmountJudgePatch.Enable();
        SimpleCraftGetAmountUpperLimitHook.Enable();
        IAgentLifecycle.Instance().RegisterListener(AgentEvent.PreReceiveEvent, AgentId.RecipeNote, OnAgentRecipeNote);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "SynthesisSimple", OnSynthesisSimple);

        if (config.IsMorePraticeQuality)
            RecipeNotePraticeSettingSetupHook.Enable();

        IAgentLifecycle.Instance().RegisterListener(AgentEvent.PreShow, AgentId.RecipeNote, OnRecipeNoteShow);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,           "RecipeNote", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,            "RecipeNote", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "RecipeNote", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize,         "RecipeNote", OnAddon);
    }

    protected override void Uninit()
    {
        SaveSearchHistory();
        RefreshRecipeListHook.Disable();
        if (config.IsEquipmentSearchView)
            RestoreCraftTypeSearchView();

        isSearchViewSwitchPending = false;
        recipeMatcher.Dispose();
        exactRecipeMatches     = null!;
        recipeSearchCategories = null!;

        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        IAddonLifecycle.Instance().UnregisterListener(OnSynthesisSimple);
        IAgentLifecycle.Instance().UnregisterListener(OnAgentRecipeNote);
        IAgentLifecycle.Instance().UnregisterListener(OnRecipeNoteShow);

        RemoveAllNodes();

        AddonActionsPreview.Addon?.Dispose();
        AddonActionsPreview.Addon = null;

        foreach (var x in calculationResults.Values)
        {
            LinkPayloadManager.Instance().Unreg(x.CopyLinkPayload.CommandId);
            LinkPayloadManager.Instance().Unreg(x.PreviewLinkPayload.CommandId);
        }

        calculationResults.Clear();

        if (installRaphaelLinkPayload != null)
            LinkPayloadManager.Instance().Unreg(installRaphaelLinkPayload.CommandId);
        installRaphaelTask = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-CategoryButtons"), ref config.IsCategoryButtons))
        {
            config.Save(this);
            RemoveAllNodes();
        }

        ImGuiOm.TextDisabledWrapped(Lang.Get("OptimizedRecipeNote-Config-CategoryButtons-Help"));

        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-CaculateRecipeButton"), ref config.IsCaculateRecipeButton))
        {
            config.Save(this);
            RemoveAllNodes();
        }

        ImGuiOm.TextDisabledWrapped(Lang.Get("OptimizedRecipeNote-Config-CaculateRecipeButton-Help"));

        if (ImGui.Checkbox(Lang.Get("OptimizedRecipeNote-Config-MorePraticeQuality"), ref config.IsMorePraticeQuality))
        {
            config.Save(this);

            RecipeNotePraticeSettingSetupHook.Toggle(config.IsMorePraticeQuality);
        }

        ImGuiOm.TextDisabledWrapped(Lang.Get("OptimizedRecipeNote-Config-MorePraticeQuality-Help"));
    }

    private unsafe void ScanRecipeRowsDetour(RecipeSearchContextEx* context, uint rowIndex, uint rowCount)
    {
        var     query   = context->SearchText.ToString();
        ref var results = ref *context->SearchResults;
        results.Clear();
        sortedSearchRecipeIDs = [];

        if (context->IsExactMatch)
        {
            if (exactRecipeMatches.TryGetValue(query, out var recipes))
            {
                foreach (var recipe in recipes)
                    results.AddCopy(recipe.RowId);
            }
        }
        else
        {
            foreach (var entry in recipeMatcher.Search(query))
            {
                foreach (var recipe in entry.Recipes)
                    results.AddCopy(recipe.RowId);
            }
        }

        context->RowStartIndex = context->RowEndIndex;
        context->CanIterate    = false;
        context->IsComplete    = true;
    }

    private static RecipeSearchCategory GetRecipeSearchCategory(Recipe recipe)
    {
        var item  = recipe.ItemResult.Value;
        var slots = item.EquipSlotCategory.Value;
        var isWeapon = slots.MainHand > 0 ||
                       slots.OffHand  > 0;
        var slot = slots switch
        {
            { MainHand: > 0 }                    => 0,
            { OffHand: > 0 }                     => 1,
            { Head: > 0 }                        => 2,
            { Body: > 0 }                        => 3,
            { Legs: > 0 }                        => 4,
            { Gloves: > 0 }                      => 5,
            { Feet: > 0 }                        => 6,
            { Ears: > 0 }                        => 7,
            { Neck: > 0 }                        => 8,
            { Wrists: > 0 }                      => 9,
            { FingerL: > 0 } or { FingerR: > 0 } => 10,
            _                                    => 11
        };

        if (isWeapon)
            return new
            (
                0,
                item.ClassJobCategory.RowId,
                slot,
                recipe.DisplayPriority,
                item.ItemUICategory.RowId,
                Lang.Get("OptimizedRecipeNote-SearchView-Weapons")
            );

        if (item.EquipSlotCategory.RowId != 0)
            return new
            (
                item.ClassJobCategory.RowId + 1,
                0,
                slot,
                recipe.DisplayPriority,
                item.ItemUICategory.RowId,
                item.ClassJobCategory.Value.Name.ExtractText()
            );

        return new
        (
            NON_EQUIPMENT_GROUP_OFFSET + recipe.CraftType.RowId,
            0,
            slot,
            recipe.DisplayPriority,
            item.ItemUICategory.RowId,
            recipe.CraftType.Value.Name.ExtractText()
        );
    }

    private unsafe int RefreshRecipeListDetour(AgentRecipeNoteEx* agent)
    {
        var recipeNote = RecipeNote.Instance();
        var recipeList = recipeNote->RecipeList;

        if (!config.IsEquipmentSearchView   ||
            agent->RecipeSearchOpen != 2    ||
            !recipeNote->IsRecipeListReady  ||
            recipeList              == null ||
            recipeList->RecipeCount == 0)
        {
            return RefreshRecipeListHook.Original(agent);
        }

        var recipes  = recipeList->RecipesSpan;
        var isSorted = recipes.Length == sortedSearchRecipeIDs.Length;

        if (isSorted)
        {
            for (var index = 0; index < recipes.Length; index++)
            {
                if (recipes[index].RecipeId == sortedSearchRecipeIDs[index])
                    continue;

                isSorted = false;
                break;
            }
        }

        if (!isSorted)
        {
            originalSearchRecipeOrder.Clear();
            for (var index = 0; index < recipes.Length; index++)
                originalSearchRecipeOrder.Add(recipes[index].RecipeId, index);

            SortSearchRecipes(agent, (left, right) => recipeSearchCategories[left.RecipeId].Order.CompareTo(recipeSearchCategories[right.RecipeId].Order));
            sortedSearchRecipeIDs = new ushort[recipes.Length];
            for (var index = 0; index < recipes.Length; index++)
                sortedSearchRecipeIDs[index] = recipes[index].RecipeId;
        }

        var refreshFlags = RefreshRecipeListHook.Original(agent);
        if (refreshFlags == 0)
            return refreshFlags;

        var strings          = AtkStage.Instance()->GetStringArrayData()[RECIPE_STRING_ARRAY_INDEX];
        var filter           = agent->ItemCategoryFilters[agent->SelectedItemCategoryFilter];
        var visibleIndex     = 0;
        var pageStart        = agent->PageIndex * SEARCH_PAGE_SIZE;
        var groupID          = uint.MaxValue;
        var groupStringIndex = 0;

        foreach (ref var recipe in recipes)
        {
            var category = recipeSearchCategories[recipe.RecipeId];
            if (filter                != 0 &&
                category.ItemCategory != filter)
                continue;

            var indexInPage = visibleIndex++ - pageStart;
            if (indexInPage < 0)
                continue;

            if (indexInPage >= SEARCH_PAGE_SIZE)
                break;

            var stringIndex = SEARCH_GROUP_STRING_OFFSET + (indexInPage * SEARCH_ENTRY_STRING_COUNT);

            if (category.GroupID != groupID)
            {
                groupID          = category.GroupID;
                groupStringIndex = stringIndex;
                strings->SetValue(stringIndex, category.Name);
            }
            else
                strings->SetValue(stringIndex, strings->StringArray[groupStringIndex], managed: false);
        }

        return refreshFlags;
    }

    private unsafe void SortSearchRecipes(AgentRecipeNoteEx* agent, Comparison<RecipeNote.RecipeEntry> comparison)
    {
        var recipeNote = RecipeNote.Instance();
        var recipeList = recipeNote->RecipeList;
        var recipes    = recipeList->RecipesSpan;
        recipes.Sort(comparison);

        foreach (ref var recipe in recipes)
        {
            RebindInlineString(ref recipe.ItemName);
            foreach (ref var ingredient in recipe.Ingredients)
                RebindInlineString(ref ingredient.Name);
        }

        isSearchViewSwitchPending = true;
        agent->PageIndex          = 0;
        var firstRecipeIndex = agent->FindRecipeIndexInPage(0, 0);
        recipeList->SelectedIndex          = (ushort)firstRecipeIndex;
        agent->SelectedRecipeIndex         = firstRecipeIndex;
        agent->SelectedRecipeIndexInSearch = (ushort)firstRecipeIndex;

        if (recipes[firstRecipeIndex].RecipeId != recipeNote->CraftingRecipeId)
            recipeNote->CraftingRecipeId = 0;

        return;

        static void RebindInlineString(ref Utf8String text)
        {
            if (text.IsUsingInlineBuffer)
                text.StringPtr = (byte*)Unsafe.AsPointer(ref text.InlineBuffer[0]);
        }
    }

    private unsafe void RestoreCraftTypeSearchView()
    {
        var agent = (AgentRecipeNoteEx*)AgentRecipeNote.Instance();
        if (agent->RecipeSearchOpen != 2 ||
            !agent->IsAgentActive())
            return;

        var recipeNote = RecipeNote.Instance();
        var recipeList = recipeNote->RecipeList;

        if (recipeNote->IsRecipeListReady                                &&
            recipeList                   != null                         &&
            recipeList->RecipeCount      == sortedSearchRecipeIDs.Length &&
            sortedSearchRecipeIDs.Length != 0)
        {
            var recipes = recipeList->RecipesSpan;

            for (var index = 0; index < recipes.Length; index++)
            {
                if (recipes[index].RecipeId != sortedSearchRecipeIDs[index])
                    return;
            }

            SortSearchRecipes(agent, (left, right) => originalSearchRecipeOrder[left.RecipeId].CompareTo(originalSearchRecipeOrder[right.RecipeId]));
        }

        originalSearchRecipeOrder.Clear();
        sortedSearchRecipeIDs = [];
        agent->RefreshAddon(1);
    }

    public static unsafe int SimpleCraftGetAmountUpperLimitDetour
    (
        nint agentRecipeNote,
        bool isHQ
    )
    {
        var selectedRecipe = RecipeNote.Instance()->RecipeList->SelectedRecipe;
        if (selectedRecipe == null) return 0;

        var maxPortion = 9999;

        foreach (var ingredient in selectedRecipe->Ingredients)
        {
            if (ingredient.ItemId == 0) continue;

            var itemCountNQ = InventoryManager.Instance()->GetInventoryItemCount(ingredient.ItemId);
            var itemCountHQ = InventoryManager.Instance()->GetInventoryItemCount(ingredient.ItemId, true);

            var itemCount = isHQ ?
                                itemCountNQ + itemCountHQ :
                                itemCountNQ;
            if (itemCount == 0) return 0;

            var portion = itemCount / ingredient.Amount;
            if (portion == 0) return 0;

            maxPortion = (int)MathF.Min(portion, maxPortion);
        }

        return maxPortion;
    }

    private static unsafe int GetCraftableAmount
    (
        Recipe recipe
    )
    {
        var maxPortion = 9999;

        for (var index = 0; index < recipe.Ingredient.Count; index++)
        {
            var ingredient = recipe.Ingredient[index];
            if (!ingredient.IsValid || ingredient.RowId == 0) continue;

            var amount = recipe.AmountIngredient[index];
            if (amount == 0) continue;

            var itemCount = InventoryManager.Instance()->GetInventoryItemCount(ingredient.RowId) +
                            InventoryManager.Instance()->GetInventoryItemCount(ingredient.RowId, true);
            maxPortion = Math.Min(maxPortion, itemCount / amount);
        }

        return maxPortion;
    }

    private static unsafe void RefreshRecipeMaterials()
    {
        var recipeList = RecipeNote.Instance()->RecipeList;
        if (recipeList == null) return;

        var recipe = recipeList->SelectedRecipe;
        if (recipe == null) return;

        for (var index = 0; index < recipe->Ingredients.Length; index++)
        {
            ref var ingredient = ref recipe->Ingredients[index];
            if (ingredient.ItemId == 0 || ingredient.Amount == 0) continue;

            var nqAmount = Math.Min
            (
                ingredient.Amount,
                InventoryManager.Instance()->GetInventoryItemCount(ingredient.ItemId)
            );
            var hqAmount = Math.Min(ingredient.Amount - nqAmount, InventoryManager.Instance()->GetInventoryItemCount(ingredient.ItemId, true));
            ingredient.NQCount = (byte)nqAmount;
            ingredient.HQCount = (byte)hqAmount;
        }
    }

    private unsafe AtkValue* RecipeNotePraticeSettingSetupDetour
    (
        AtkEventListener* listener,
        AtkValue*         returnValue,
        AtkValue*         values
    )
    {
        // 初始品质
        values[1].SetUInt(values[1].UInt * 2);

        // 当前品质
        values[0].SetUInt(values[1].UInt);

        // 初始品质文字
        values[2].SetManagedString(ISeStringEvaluator.Instance().EvaluateFromAddon(14222, [values[1].UInt]));

        return RecipeNotePraticeSettingSetupHook.Original(listener, returnValue, values);
    }

    private unsafe void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                RestoreSearchHistory();
                break;

            case AddonEvent.PreFinalize:
                isSearchViewSwitchPending = false;
                SaveSearchHistory();

                calculateRecipeButton   = null;
                switchJobButton         = null;
                displayOthersButton     = null;
                displayOthersJobsLayout = null;
                clearSearchButton       = null;
                searchViewButton        = null;
                levelRecipeButton       = null;
                specialRecipeButton     = null;
                masterRecipeButton      = null;

                materialSourceButtons.Clear();
                displayOthersJobButtons.Clear();
                break;

            case AddonEvent.PostRequestedUpdate:
                if (isSearchViewSwitchPending)
                {
                    isSearchViewSwitchPending = false;
                    var agent = (AgentRecipeNoteEx*)AgentRecipeNote.Instance();
                    if (agent->RecipeSearchOpen == 2 &&
                        agent->IsRecipeListAvailable &&
                        RecipeNote.Instance()->IsRecipeListReady)
                        agent->SelectRecipeByVisibleIndex(0);

                    searchViewButton?.IsEnabled = true;
                }

                SaveSelectedRecipe();
                SaveSearchHistory();

                if (!AgentRecipeNote.Instance()->RecipeSearchOpen && TryGetCurrentRecipe(out var recipeID, out _))
                    lastRecipeID = recipeID;

                UpdateRecipeAddonButtons();

                break;

            case AddonEvent.PostDraw:
                if (!RecipeNoteAddon->IsAddonAndNodesReady()) return;

                // 求解配方按钮
                CreateCaculateRecipeButton();

                // 切换职业按钮
                CreateSwitchJobButton();

                // 材料来源按钮
                CreateMaterialSourceButtons();

                // 显示其余按钮
                CreateDisplayOthersButtons();

                // 清除搜索按钮
                CreateClearSearchButton();

                // 切换搜索视图按钮
                CreateSearchViewButton();

                // 分类按钮
                CreateCategoryButtons();

                UpdateRecipeAddonButtons();

                break;
        }

        return;

        void UpdateRecipeAddonButtons()
        {
            if (RecipeNoteAddon == null) return;

            UpdateCaculateRecipeButton();

            UpdateSwitchJobButton();

            UpdateClearSearchButton();

            searchViewButton?.IsVisible = ((AgentRecipeNoteEx*)AgentRecipeNote.Instance())->RecipeSearchOpen == 2;

            UpdateDisplayOthersButtons();

            UpdateMaterialSourceButtons();
        }
    }

    private unsafe void OnRecipeNoteShow
    (
        AgentEvent type,
        AgentArgs  args
    )
    {
        var agent = (AgentRecipeNoteEx*)AgentRecipeNote.Instance();

        if (agent->IsAgentActive())
            return;

        var classJob = LocalPlayerState.ClassJob;
        var memoryJob = classJob is >= 8 and <= 15 ?
                            classJob :
                            0;

        if (!config.SelectedRecipes.TryGetValue(memoryJob, out var recipeID))
            return;

        agent->OpenRecipeByRecipeID(recipeID);
        args.PreventOriginal();
    }

    private unsafe void SaveSelectedRecipe()
    {
        var agent = (AgentRecipeNoteEx*)AgentRecipeNote.Instance();
        if (!RecipeNoteAddon->IsAddonAndNodesReady() ||
            !agent->IsAgentActive()                  ||
            !agent->IsRecipeListAvailable            ||
            agent->PendingState is 1 or 3 or 5       ||
            !TryGetCurrentRecipe(out var recipeID, out var recipe))
            return;

        var classJob  = LocalPlayerState.ClassJob;
        var isCrafter = classJob is >= 8 and <= 15;
        if (isCrafter &&
            recipe.CraftType.RowId + 8 != classJob)
            return;

        var memoryJob = isCrafter ?
                            classJob :
                            0;
        if (config.SelectedRecipes.TryGetValue(memoryJob, out var selectedRecipeID) &&
            selectedRecipeID == recipeID)
            return;

        config.SelectedRecipes[memoryJob] = recipeID;
        config.Save(this);
    }

    private unsafe void RestoreSearchHistory()
    {
        var agent = (AgentRecipeNoteEx*)AgentRecipeNote.Instance();

        List<string> history = [];
        foreach (ref var term in agent->RecipeSearchHistory)
            history.Add(term.ToString());

        List<SearchHistoryEntry> entries = [];
        foreach (ref var entry in agent->RecipeSearchHistoryEntries)
            entries.Add(new(entry.Text.ToString(), entry.RecipeID));

        if (config.SearchHistory.SequenceEqual(history) &&
            config.SearchHistoryEntries.SequenceEqual(entries))
            return;

        history.AddRange(config.SearchHistory);
        entries.AddRange(config.SearchHistoryEntries);

        for (var index = history.Count - 1; index >= 0; index--)
        {
            using var text = new Utf8String(history[index]);
            agent->PushSearchHistory(&text);
        }

        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var       entry = entries[index];
            using var text  = new Utf8String(entry.Text);
            agent->PushSearchHistoryEntry(&text, (int)entry.RecipeID);
            (&text)->Ctor();
        }

        SaveSearchHistory();
    }

    private unsafe void SaveSearchHistory()
    {
        var agent = (AgentRecipeNoteEx*)AgentRecipeNote.Instance();
        if (agent->RecipeSearchHistory.Count        == 0 &&
            agent->RecipeSearchHistoryEntries.Count == 0)
            return;

        List<string> history = [];
        foreach (ref var term in agent->RecipeSearchHistory)
            history.Add(term.ToString());

        List<SearchHistoryEntry> entries = [];
        foreach (ref var entry in agent->RecipeSearchHistoryEntries)
            entries.Add(new(entry.Text.ToString(), entry.RecipeID));

        if (config.SearchHistory.SequenceEqual(history) &&
            config.SearchHistoryEntries.SequenceEqual(entries))
            return;

        config.SearchHistory        = history;
        config.SearchHistoryEntries = entries;
        config.Save(this);
    }

    #region 原生界面元素插入

    private unsafe void RemoveAllNodes()
    {
        calculateRecipeButton?.Dispose();
        calculateRecipeButton = null;

        clearSearchButton?.Dispose();
        clearSearchButton = null;

        searchViewButton?.Dispose();
        searchViewButton = null;

        levelRecipeButton?.Dispose();
        levelRecipeButton = null;

        specialRecipeButton?.Dispose();
        specialRecipeButton = null;

        masterRecipeButton?.Dispose();
        masterRecipeButton = null;

        displayOthersButton?.Dispose();
        displayOthersButton = null;

        displayOthersJobButtons.Clear();

        displayOthersJobsLayout?.Dispose(); // 把子节点都一并清除掉了
        displayOthersJobsLayout = null;

        foreach (var x in materialSourceButtons)
            x.Dispose();
        materialSourceButtons.Clear();

        if (RecipeNoteAddon != null)
        {
            var resNode0 = RecipeNoteAddon->GetNodeById(95);
            resNode0->Timeline = null;
            if (resNode0 != null)
                resNode0->SetXFloat(46);

            var resNode1 = RecipeNoteAddon->GetNodeById(88);
            resNode1->Timeline = null;
            if (resNode1 != null)
                resNode1->SetXFloat(0);

            var resNode2 = RecipeNoteAddon->GetNodeById(84);
            resNode2->Timeline = null;
            if (resNode2 != null)
                resNode2->SetXFloat(0);
        }

        switchJobButton?.Dispose();
        switchJobButton = null;

        calculateRecipeButton?.Dispose();
        calculateRecipeButton = null;
    }

    // 清除搜索按钮
    private unsafe void CreateClearSearchButton()
    {
        if (clearSearchButton != null)
            return;

        clearSearchButton = new()
        {
            IsVisible          = true,
            Position           = new(130, 26),
            Size               = new(24),
            TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
            TextureCoordinates = Vector2.Zero,
            TextureSize        = new(28),
            OnClick = () =>
            {
                var addon = (AddonRecipeNote*)RecipeNoteAddon;
                if (addon                  == null ||
                    addon->SearchTextInput == null)
                    return;

                addon->SearchTextInput->SetText(string.Empty);

                var agent = (AgentRecipeNoteEx*)AgentRecipeNote.Instance();
                agent->RecipeSearch.Clear();
                clearSearchButton.IsVisible = false;

                if (lastRecipeID            != 0 &&
                    agent->RecipeSearchOpen != 0)
                    agent->OpenRecipeByRecipeID(lastRecipeID);
            }
        };

        clearSearchButton.AttachNode(RecipeNoteAddon->GetNodeById(24));
    }

    private unsafe void UpdateClearSearchButton()
    {
        if (clearSearchButton == null)
            return;

        var addon = (AddonRecipeNote*)RecipeNoteAddon;
        clearSearchButton.IsVisible = addon                  != null &&
                                      addon->SearchTextInput != null &&
                                      addon->SearchTextInput->HasEvaluatedText();
    }

    private unsafe void CreateSearchViewButton()
    {
        if (searchViewButton != null)
            return;

        var nextCategoryButton = RecipeNoteAddon->GetComponentButtonById(44);
        if (nextCategoryButton != null)
            nextCategoryButton->OwnerNode->SetFixedPosition(250, nextCategoryButton->OwnerNode->Y);

        var button = RecipeNoteAddon->GetNodeById(SEARCH_VIEW_ANCHOR_NODE_ID);
        searchViewButton = new()
        {
            Size     = new(SEARCH_VIEW_BUTTON_SIZE),
            Position = new(457, 82),
            Icon = config.IsEquipmentSearchView ?
                       CircleButtonIcon.Helmet :
                       CircleButtonIcon.Sort,
            TextTooltip = Lang.Get
            (
                config.IsEquipmentSearchView ?
                    "OptimizedRecipeNote-SearchView-CraftType" :
                    "OptimizedRecipeNote-SearchView-Equipment"
            ),
            OnClick = () =>
            {
                var agent = (AgentRecipeNoteEx*)AgentRecipeNote.Instance();
                if (isSearchViewSwitchPending     ||
                    agent->RecipeSearchOpen != 2  ||
                    agent->RecipeSearchProcessing ||
                    !agent->IsRecipeListAvailable ||
                    !RecipeNote.Instance()->IsRecipeListReady)
                    return;

                isSearchViewSwitchPending    = true;
                searchViewButton.IsEnabled   = false;
                config.IsEquipmentSearchView = !config.IsEquipmentSearchView;
                config.Save(this);
                searchViewButton.Icon = config.IsEquipmentSearchView ?
                                            CircleButtonIcon.Helmet :
                                            CircleButtonIcon.Sort;
                searchViewButton.TextTooltip = Lang.Get
                (
                    config.IsEquipmentSearchView ?
                        "OptimizedRecipeNote-SearchView-CraftType" :
                        "OptimizedRecipeNote-SearchView-Equipment"
                );
                searchViewButton.HideTooltip();
                searchViewButton.ShowTooltip();

                if (config.IsEquipmentSearchView)
                    agent->RefreshAddon(1);
                else
                    RestoreCraftTypeSearchView();
            }
        };
        searchViewButton.AttachNode(button->ParentNode);
    }

    // 切换分类按钮
    private unsafe void CreateCategoryButtons()
    {
        if (!config.IsCategoryButtons ||
            (levelRecipeButton   != null &&
             specialRecipeButton != null &&
             masterRecipeButton  != null))
            return;

        if (levelRecipeButton == null)
        {
            levelRecipeButton = new()
            {
                IsVisible   = true,
                Position    = new(0, 32),
                Size        = new(58, 38),
                TextTooltip = LuminaWrapper.GetAddonText(1710),
                OnClick = () =>
                {
                    AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 2;
                    var button = RecipeNoteAddon->GetComponentButtonById(35);

                    if (button != null)
                    {
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                    }
                }
            };
            levelRecipeButton.BackgroundNode.IsVisible = false;
            levelRecipeButton.AttachNode(RecipeNoteAddon->GetNodeById(32));
        }

        if (specialRecipeButton == null)
        {
            specialRecipeButton = new()
            {
                IsVisible   = true,
                Position    = new(50, 32),
                Size        = new(58, 38),
                TextTooltip = LuminaWrapper.GetAddonText(1711),
                OnClick = () =>
                {
                    AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 0;
                    var button = RecipeNoteAddon->GetComponentButtonById(35);

                    if (button != null)
                    {
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                    }
                }
            };
            specialRecipeButton.BackgroundNode.IsVisible = false;
            specialRecipeButton.AttachNode(RecipeNoteAddon->GetNodeById(32));
        }

        if (masterRecipeButton == null)
        {
            masterRecipeButton = new()
            {
                IsVisible   = true,
                Position    = new(102, 32),
                Size        = new(58, 38),
                TextTooltip = LuminaWrapper.GetAddonText(14212),
                OnClick = () =>
                {
                    AgentRecipeNote.Instance()->SelectedRecipeCategoryPage = 1;
                    var button = RecipeNoteAddon->GetComponentButtonById(35);

                    if (button != null)
                    {
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                        IFramework.Instance().Run(() => button->Click());
                    }
                }
            };
            masterRecipeButton.BackgroundNode.IsVisible = false;
            masterRecipeButton.AttachNode(RecipeNoteAddon->GetNodeById(32));
        }
    }

    // 显示其余配方按钮
    private unsafe void CreateDisplayOthersButtons()
    {
        if (displayOthersButton     != null &&
            displayOthersJobsLayout != null)
            return;

        if (displayOthersButton == null)
        {
            displayOthersButton = new()
            {
                Position  = new(0, -32),
                Size      = new(140, 32),
                String    = Lang.Get("OptimizedRecipeNote-Button-ShowOtherRecipes"),
                IsVisible = true,
                OnClick = () =>
                {
                    if (!TryGetCurrentRecipe(out _, out var recipe))
                        return;

                    if (!SameItemRecipes.TryGetValue(recipe.ItemResult.RowId, out _))
                        return;

                    AgentRecipeNote.Instance()->SearchRecipeByItemId(recipe.ItemResult.RowId);
                }
            };

            displayOthersButton.LabelNode.AutoAdjustTextSize();
            displayOthersButton.AttachNode(RecipeNoteAddon->GetNodeById(57));
        }

        if (displayOthersJobsLayout == null)
        {
            displayOthersJobsLayout = new()
            {
                IsVisible   = true,
                ItemSpacing = 5,
                Size        = new(220, 24),
                Position    = new(142, -30)
            };

            for (var i = 0U; i < 8; i++)
            {
                var iconButtonNode = new IconButtonNode
                {
                    IconId      = 62008 + i,
                    Size        = new(24),
                    IsVisible   = true,
                    TextTooltip = LuminaWrapper.GetJobName(8 + i)
                };

                var iconNode = new IconImageNode
                {
                    IconId         = 62008 + i,
                    Size           = new(24),
                    IsVisible      = true,
                    ImageNodeFlags = ImageNodeFlags.AutoFit
                };

                iconNode.AddTimeline
                (
                    new TimelineBuilder()
                        .AddFrameSetWithFrame(1,  10, 1,  Vector2.Zero, 255, multiplyColor: new(100.0f))
                        .AddFrameSetWithFrame(11, 17, 11, Vector2.Zero, 255, multiplyColor: new(100.0f))
                        .AddFrameSetWithFrame
                        (
                            18,
                            26,
                            18,
                            Vector2.Zero + new Vector2(0.0f, 1.0f),
                            255,
                            multiplyColor: new(100.0f)
                        )
                        .AddFrameSetWithFrame(27, 36, 27, Vector2.Zero, 153, multiplyColor: new(80.0f))
                        .AddFrameSetWithFrame(37, 46, 37, Vector2.Zero, 255, multiplyColor: new(100.0f))
                        .AddFrameSetWithFrame(47, 53, 47, Vector2.Zero, 255, multiplyColor: new(100.0f))
                        .Build()
                );

                iconButtonNode.BackgroundNode.IsVisible = false;
                iconButtonNode.ImageNode.IsVisible      = false;
                iconNode.AttachNode(iconButtonNode);

                displayOthersJobButtons.Add(iconButtonNode);
                displayOthersJobsLayout.AddNode(iconButtonNode);
            }

            displayOthersJobsLayout.AttachNode(RecipeNoteAddon->GetNodeById(57));
        }
    }

    private unsafe void UpdateDisplayOthersButtons()
    {
        if (displayOthersButton           == null ||
            displayOthersJobsLayout       == null ||
            displayOthersJobButtons.Count != 8)
            return;

        if (!TryGetCurrentRecipe(out _, out var recipe) ||
            !SameItemRecipes.TryGetValue(recipe.ItemResult.RowId, out var allRecipes))
        {
            displayOthersButton.IsVisible     = false;
            displayOthersJobsLayout.IsVisible = false;
            return;
        }

        displayOthersButton.IsVisible     = true;
        displayOthersJobsLayout.IsVisible = true;

        var allCraftTypes = allRecipes.ToDictionary(x => x.CraftType.RowId, x => x.RowId);

        for (var i = 0U; i < 8; i++)
        {
            var node = displayOthersJobButtons[(int)i];

            if (allCraftTypes.TryGetValue(i, out var otherRecipeID))
            {
                node.Alpha     = 1;
                node.IsEnabled = true;
                node.OnClick = () =>
                {
                    if (TryGetCurrentRecipe(out var recipeID, out _) &&
                        recipeID == otherRecipeID)
                        return;

                    AgentRecipeNote.Instance()->OpenRecipeByRecipeId(otherRecipeID);
                };
            }
            else
            {
                node.Alpha     = 0.2f;
                node.IsEnabled = false;
                node.OnClick   = () => { };
            }
        }
    }

    // 材料来源
    private unsafe void CreateMaterialSourceButtons()
    {
        if (materialSourceButtons.Count != 0)
            return;

        for (var i = 0U; i < 6; i++)
        {
            var componentNode = RecipeNoteAddon->GetComponentNodeById(89 + i);

            var index = i;
            var buttonNode = new IconButtonNode
            {
                IconId    = 60412,
                Size      = new(32),
                IsVisible = true,
                Position  = new(-26, 8f),
                OnClick = () =>
                {
                    if (!TryGetCurrentRecipe(out _, out var recipe) ||
                        !recipe.Ingredient[(int)index].IsValid)
                        return;

                    var item        = recipe.Ingredient[(int)index].Value;
                    var sourceState = ItemSourceInfo.Query(item.RowId).State;
                    var hasNPCShop  = sourceState == ItemSourceQueryState.Ready;

                    // 既能 NPC 买到又能市场布告板
                    if (item.ItemSearchCategory.RowId > 0 && hasNPCShop)
                    {
                        if (!PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                            OpenShopListByItemIDIPC.InvokeFunc(item.RowId);
                        else
                            ChatManager.Instance().SendMessage($"/pdr market {item.Name}");
                    }
                    else if (hasNPCShop)
                        OpenShopListByItemIDIPC.InvokeFunc(item.RowId);
                    else if (item.ItemSearchCategory.RowId > 0)
                        ChatManager.Instance().SendMessage($"/pdr market {item.Name}");
                }
            };

            var backgroundNode = (SimpleNineGridNode)buttonNode.BackgroundNode;

            backgroundNode.TexturePath        = "ui/uld/partyfinder_hr1.tex";
            backgroundNode.TextureCoordinates = new(38, 2);
            backgroundNode.TextureSize        = new(32, 34);
            backgroundNode.LeftOffset         = 0;
            backgroundNode.RightOffset        = 0;

            materialSourceButtons.Add(buttonNode);
            buttonNode.AttachNode(componentNode);
        }
    }

    private unsafe void UpdateMaterialSourceButtons()
    {
        if (materialSourceButtons.Count != 6)
            return;

        if (!TryGetCurrentRecipe(out _, out var recipe))
            return;

        var maxIngredientAmount = 0;

        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            if (recipe.Ingredient[i] is not { IsValid: true, RowId: > 100 }) continue;
            if (recipe.AmountIngredient[i] is var amount && amount > maxIngredientAmount)
                maxIngredientAmount = amount;
        }

        var appendOffset = maxIngredientAmount >= 10 ?
                               30 :
                               10;
        var resNode0 = RecipeNoteAddon->GetNodeById(95);
        resNode0->Timeline = null;
        if (resNode0 != null)
            resNode0->SetXFloat(46 + appendOffset);

        var resNode1 = RecipeNoteAddon->GetNodeById(88);
        resNode1->Timeline = null;
        if (resNode1 != null)
            resNode1->SetXFloat(0 + appendOffset);

        var resNode2 = RecipeNoteAddon->GetNodeById(84);
        resNode2->Timeline = null;
        if (resNode2 != null)
            resNode2->SetXFloat(0 + appendOffset);

        var resNodeProgress = RecipeNoteAddon->GetNodeById(2);
        if (resNodeProgress != null)
            resNodeProgress->SetAlpha(0);

        for (var d = 0; d < materialSourceButtons.Count; d++)
        {
            if (!recipe.Ingredient[d].IsValid) break;

            var item        = recipe.Ingredient[d].Value;
            var sourceState = ItemSourceInfo.Query(item.RowId).State;
            var hasNPCShop  = sourceState == ItemSourceQueryState.Ready;

            var    button = materialSourceButtons[d];
            string sourceText;

            button.X = -6 +
                       (maxIngredientAmount >= 10 ?
                            -20 :
                            0);

            // 既能 NPC 买到又能市场布告板
            if (item.ItemSearchCategory.RowId > 0 && hasNPCShop)
            {
                button.IconId = 60412;
                sourceText    = $"{LuminaWrapper.GetAddonText(350)} / {LuminaWrapper.GetAddonText(548)} [{Lang.Get("ConflictKey")}]";
            }
            else if (hasNPCShop)
            {
                button.IconId = 60412;
                sourceText    = $"{LuminaWrapper.GetAddonText(350)}";
            }
            else if (item.ItemSearchCategory.RowId > 0)
            {
                button.IconId = 60570;
                sourceText    = $"{LuminaWrapper.GetAddonText(548)}";
            }
            else
                sourceText = string.Empty;

            if (sourceText == string.Empty)
                button.IsVisible = false;
            else
            {
                button.IsVisible   = true;
                button.TextTooltip = sourceText;
            }
        }
    }

    // 切换职业
    private unsafe void CreateSwitchJobButton()
    {
        if (switchJobButton != null)
            return;

        switchJobButton = new()
        {
            Position = new(228, 522),
            Size     = new(140, 36),
            String   = Lang.Get("OptimizedRecipeNote-Button-SwitchJob"),
            OnClick = () =>
            {
                if (!TryGetCurrentRecipe(out var recipeID, out var recipe))
                    return;

                // 职业对了
                if (recipe.CraftType.RowId == LocalPlayerState.ClassJob - 8) return;

                // 能直接切换
                if (!ICondition.Instance()[ConditionFlag.PreparingToCraft])
                {
                    LocalPlayerState.SwitchGearset(recipe.CraftType.RowId + 8);
                    return;
                }

                TaskHelper.Enqueue(() => AgentRecipeNote.Instance()->Hide());
                TaskHelper.Enqueue(() => !ICondition.Instance()[ConditionFlag.PreparingToCraft]);
                TaskHelper.Enqueue(() => LocalPlayerState.SwitchGearset(recipe.CraftType.RowId + 8));
                TaskHelper.Enqueue(() => AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeID));
            }
        };

        switchJobButton.AttachNode(RecipeNoteAddon->GetNodeById(57));
    }

    private unsafe void UpdateSwitchJobButton()
    {
        if (switchJobButton == null)
            return;

        if (!TryGetCurrentRecipe(out _, out var recipe))
        {
            switchJobButton.IsVisible = false;
            return;
        }

        var startCraftButton = RecipeNoteAddon->GetComponentButtonById(104);

        if (recipe.CraftType.RowId != LocalPlayerState.ClassJob - 8)
        {
            switchJobButton.IsVisible = true;
            if (startCraftButton != null)
                startCraftButton->OwnerNode->ToggleVisibility(false);

            switchJobButton.TextTooltip = LuminaWrapper.GetJobName(8 + recipe.CraftType.RowId);

            for (var i = 102U; i < 105; i++)
            {
                var buttonNode = RecipeNoteAddon->GetComponentButtonById(i);
                if (buttonNode != null)
                    buttonNode->SetEnabledState(false);
            }
        }
        else
        {
            switchJobButton.IsVisible = false;
            if (startCraftButton != null)
                startCraftButton->OwnerNode->ToggleVisibility(true);

            for (var i = 102U; i < 105; i++)
            {
                var buttonNode = RecipeNoteAddon->GetComponentButtonById(i);
                if (buttonNode != null)
                    buttonNode->SetEnabledState(true);
            }
        }
    }

    // 求解配方
    private unsafe void CreateCaculateRecipeButton()
    {
        if (!config.IsCaculateRecipeButton ||
            calculateRecipeButton != null)
            return;

        calculateRecipeButton = new()
        {
            Position = new(228, 490),
            Size     = new(140, 32),
            String   = Lang.Get("OptimizedRecipeNote-Button-CaculateRecipe")
        };

        calculateRecipeButton.OnClick = () =>
        {
            if (!IDalamudPluginInterface.Instance().IsPluginEnabled(RaphaelIPC.INTERNAL_NAME))
            {
                PrintInstallRaphaelPluginMessage();
                return;
            }

            if (!TryGetCurrentRecipe(out var recipeID, out var recipe))
                return;

            // 职业不对
            if (recipe.CraftType.RowId != LocalPlayerState.ClassJob - 8) return;

            var craftPoint    = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.CraftingPoints);
            var craftsmanship = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.Craftsmanship);
            var control       = PlayerState.Instance()->GetAttributeByIndex(PlayerAttribute.Control);
            var id = RaphaelIPC.StartCalculation
            (
                recipeID,
                new()
                {
                    BackloadProgress  = true,
                    EnsureReliability = true,
                    TargetQuality     = GetRecipeMaxQuality(recipe),
                    MaxThreads        = Environment.ProcessorCount,
                    TimeoutSeconds    = 90
                }
            );

            calculateRecipeButton.IsEnabled = false;
            TaskHelper.Enqueue
            (
                () =>
                {
                    var response = RaphaelIPC.GetCalculationStatus(id);

                    switch (response.Status)
                    {
                        case RaphaelCalculationStatus.Success:
                            calculateRecipeButton.IsEnabled = true;

                            var copyLinkPayload    = LinkPayloadManager.Instance().Reg(OnClickCopyPayload,    out _);
                            var previewLinkPayload = LinkPayloadManager.Instance().Reg(OnClickPreviewPayload, out _);
                            calculationResults[id] = new
                            (
                                response.Actions,
                                response.FinalProgress,
                                response.FinalQuality,
                                recipeID,
                                craftPoint,
                                craftsmanship,
                                control,
                                copyLinkPayload,
                                previewLinkPayload
                            );
                            PrintActionsMessage(calculationResults[id]);
                            return true;
                        case RaphaelCalculationStatus.Failed:
                            calculateRecipeButton.IsEnabled = true;
                            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
                                NotifyHelper.Instance().ChatError(response.ErrorMessage);

                            TaskHelper.Abort();
                            return true;
                        default:
                            return false;
                    }
                },
                "请求技能数据"
            );
        };

        calculateRecipeButton.AttachNode(RecipeNoteAddon->GetNodeById(57));
    }

    private void UpdateCaculateRecipeButton()
    {
        if (!config.IsCaculateRecipeButton ||
            calculateRecipeButton == null)
            return;

        if (!TryGetCurrentRecipe(out _, out var recipe) ||
            recipe.CraftType.RowId != LocalPlayerState.ClassJob - 8)
        {
            calculateRecipeButton.IsVisible = false;
            return;
        }

        calculateRecipeButton.IsVisible = true;
    }

    #endregion

    #region 消息事件

    private void OnClickInstallRaphaelPayload
    (
        uint     id,
        SeString _
    )
    {
        if (IDalamudPluginInterface.Instance().InstalledPlugins.Any(x => x.InternalName == "Raphael.Dalamud"))
        {
            ChatManager.Instance().SendMessage("/xlenableplugin Raphael.Dalamud");
            return;
        }

        if (installRaphaelTask != null) return;

        installRaphaelTask = IFramework.Instance()
                                       .RunOnTick
                                       (async () => await DalamudReflector.AddPlugin
                                                    (
                                                        "https://raw.githubusercontent.com/AtmoOmen/DalamudPlugins/main/pluginmaster.json",
                                                        "Raphael.Dalamud"
                                                    )
                                       )
                                       .ContinueWith(_ => installRaphaelTask = null);
    }

    private void OnClickPreviewPayload
    (
        uint     id,
        SeString _
    )
    {
        if (calculationResults.FirstOrDefault(x => x.Value.PreviewLinkPayload.CommandId == id) is not { Value.RecipeID: > 0 } result) return;

        AddonActionsPreview.OpenWithActions(this, result.Value);
    }

    private void OnClickCopyPayload
    (
        uint     id,
        SeString _
    )
    {
        if (calculationResults.FirstOrDefault(x => x.Value.CopyLinkPayload.CommandId == id) is not { Value.RecipeID: > 0 } result)
            return;

        var builder = new StringBuilder();
        foreach (var action in result.Value.Actions)
            builder.AppendLine($"/ac {LuminaWrapper.GetActionName(action)} <wait.3>");
        ImGui.SetClipboardText(builder.ToString());

        NotifyHelper.Toast(Lang.Get("OptimizedRecipeNote-Message-MacrosCopied"));
    }

    #endregion

    #region 消息发送

    private static void PrintActionsMessage
    (
        CalculationResult result
    )
    {
        using var rented = new RentedSeStringBuilder();

        var classJobString = rented.AppendIcon((uint)result.GetJob().ToBitmapFontIcon())
                                   .Append(result.GetJob().Name)
                                   .ToReadOnlySeString();
        rented.Clear();

        rented.Append
              (
                  Lang.GetSe
                  (
                      "OptimizedRecipeNote-Message-SolveResult",
                      new Dictionary<string, object>
                      {
                          ["classJob"] = classJobString,
                          ["item"]     = ReadOnlySeString.CreateItemLink(result.GetRecipe().ItemResult.RowId, false),
                          ["step"]     = result.Actions.Count,
                          ["second"]   = result.Actions.Count * 3
                      }
                  )
              )
              .AppendNewLine()
              .AppendDalamudSeString(result.CopyLinkPayload)
              .Append("  \ue06f ")
              .Append(Lang.Get("OptimizedRecipeNote-Message-CopyAsMacro"))
              .PopLink()
              .AppendNewLine()
              .AppendDalamudSeString(result.PreviewLinkPayload)
              .Append("  \ue06f ")
              .Append(Lang.Get("OptimizedRecipeNote-Message-PreviewSteps"))
              .PopLink();

        NotifyHelper.Instance().Chat(rented.ToReadOnlySeString());
    }

    private void PrintInstallRaphaelPluginMessage()
    {
        installRaphaelLinkPayload ??= LinkPayloadManager.Instance().Reg(OnClickInstallRaphaelPayload, out _);

        using var rented = new RentedSeStringBuilder();
        rented.AppendIcon((uint)BitmapFontIcon.Warning)
              .Append(Lang.Get("OptimizedRecipeNote-Message-InstallRaphael"))
              .AppendNewLine()
              .AppendDalamudSeString(installRaphaelLinkPayload)
              .Append("  \ue06f ")
              .Append(Lang.Get("OptimizedRecipeNote-Message-ToInstall"))
              .PopLink();

        NotifyHelper.Instance().Chat(rented.ToReadOnlySeString());
        NotifyHelper.ToastError(Lang.Get("OptimizedRecipeNote-Message-InstallRaphael"));
    }

    #endregion

    #region 简易制作分批

    private unsafe void OnAgentRecipeNote
    (
        AgentEvent type,
        AgentArgs  args
    )
    {
        if (pendingQuickSynthRemaining != 0) return;

        var formatted = args as AgentReceiveEventArgs;
        if (formatted.EventKind != 1) return;

        var atkValues = (AtkValue*)formatted.AtkValues;
        if (atkValues == null) return;

        var count = atkValues[0].Int;
        if (count <= MAX_QUICK_SYNTHESIS_COUNT) return;

        pendingQuickSynthRemaining = count - MAX_QUICK_SYNTHESIS_COUNT;
        pendingUseHQIngredient     = atkValues[1].Bool;
        pendingAllNQResult         = atkValues[2].Bool; // 顺序不确定，不过颠倒了也没事

        atkValues[0].SetInt(MAX_QUICK_SYNTHESIS_COUNT);

        var message = Lang.Get
        (
            "OptimizedRecipeNote-Message-QuickSynthBatch",
            new Dictionary<string, object>
            {
                ["batchCount"] = MathF.Ceiling((float)count / MAX_QUICK_SYNTHESIS_COUNT)
            }
        );
        NotifyHelper.Instance().Chat(message);
    }

    private unsafe void OnSynthesisSimple
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (SynthesisSimple == null) return;

        var button = SynthesisSimple->GetComponentButtonById(22);
        if (button == null) return;

        var currentCount = SynthesisSimple->AtkValues[3].UInt;
        var maxCount     = SynthesisSimple->AtkValues[4].UInt;
        if (currentCount != maxCount) return;

        if (pendingQuickSynthRemaining == 0)
        {
            pendingQuickSynthRemaining = 0;
            pendingUseHQIngredient     = false;
            pendingAllNQResult         = false;

            var message = Lang.Get("OptimizedRecipeNote-Message-QuickSynthFinish");
            NotifyHelper.Instance().Chat(message, false);
            NotifyHelper.Toast(message);
            return;
        }

        var batchSize = Math.Min(pendingQuickSynthRemaining, MAX_QUICK_SYNTHESIS_COUNT);
        pendingQuickSynthRemaining -= batchSize;

        TaskHelper.Enqueue(() => button->Click());
        TaskHelper.Enqueue(() => !SynthesisSimple->IsAddonAndNodesReady() && RecipeNoteAddon->IsAddonAndNodesReady());
        TaskHelper.Enqueue(() => AgentId.RecipeNote.SendEvent(0, 9));
        TaskHelper.Enqueue(() => SynthesisSimpleDialog->IsAddonAndNodesReady());
        TaskHelper.Enqueue(() => AgentId.RecipeNote.SendEvent(1, batchSize, pendingUseHQIngredient, pendingAllNQResult));
    }

    #endregion

    #region 工具

    private static unsafe bool TryGetCurrentRecipe
    (
        out uint   recipeID,
        out Recipe recipe
    )
    {
        recipeID = 0;
        recipe   = default;

        var recipeList = UIState.Instance()->RecipeNote.RecipeList;
        if (recipeList == null) return false;

        var data = recipeList->SelectedRecipe;
        if (data == null) return false;

        if (!LuminaGetter.TryGetRow(data->RecipeId, out Recipe recipeRow) ||
            recipeRow.ItemResult.Value is not { RowId: > 0 })
            return false;

        recipeID = data->RecipeId;
        recipe   = recipeRow;
        return true;
    }

    private static int GetRecipeMaxProgress
    (
        Recipe recipe
    ) =>
        (int)(GetRecipeLevelTable(recipe).Difficulty * (float)recipe.DifficultyFactor / 100f);

    private static int GetRecipeMaxQuality
    (
        Recipe recipe
    ) =>
        (int)(GetRecipeLevelTable(recipe).Quality * (float)recipe.QualityFactor / 100f);

    private static RecipeLevelTable GetRecipeLevelTable
    (
        Recipe recipe
    ) =>
        recipe.Number == 0 && LocalPlayerState.CurrentLevel < 100 ?
            LuminaGetter.Get<RecipeLevelTable>().First(x => x.ClassJobLevel == LocalPlayerState.CurrentLevel) :
            recipe.RecipeLevelTable.Value;

    #endregion

    private record CalculationResult
    (
        List<uint>         Actions,
        int                FinalProgress,
        int                FinalQuality,
        uint               RecipeID,
        int                CraftPoint,
        int                Craftmanship,
        int                Control,
        DalamudLinkPayload CopyLinkPayload,
        DalamudLinkPayload PreviewLinkPayload
    )
    {
        public Recipe GetRecipe() =>
            LuminaGetter.GetRowOrDefault<Recipe>(RecipeID);

        public ClassJob GetJob() =>
            LuminaGetter.GetRowOrDefault<ClassJob>(GetRecipe().CraftType.RowId + 8);
    }

    private sealed record RecipeSearchEntry
    (
        string   Name,
        Recipe[] Recipes
    );

    private sealed record RecipeSearchCategory
    (
        uint   GroupID,
        uint   WeaponJob,
        int    Slot,
        ushort DisplayPriority,
        uint   ItemCategory,
        string Name
    )
    {
        public int Order { get; init; }
    }

    private record SearchHistoryEntry
    (
        string Text,
        uint   RecipeID
    );

    private class Config : ModuleConfig
    {
        public Dictionary<uint, uint> SelectedRecipes = [];

        public List<string> SearchHistory = [];

        public List<SearchHistoryEntry> SearchHistoryEntries = [];

        public bool IsEquipmentSearchView;

        // 分类
        public bool IsCategoryButtons = true;

        // 求解配方
        public bool IsCaculateRecipeButton = true;

        // 突破制作练习初期品质上限
        public bool IsMorePraticeQuality = true;
    }

    #region IPC

    [IPCSubscriber("DailyRoutines.Modules.AutoShowItemNPCShopInfo.OpenByItemID")]
    private static IPCSubscriber<uint, bool> OpenShopListByItemIDIPC;

    #endregion

    #region 常量

    private static readonly FrozenDictionary<uint, List<Recipe>> SameItemRecipes =
        LuminaGetter.Get<Recipe>()
                    .GroupBy(x => x.ItemResult.RowId)
                    .DistinctBy(x => x.Key)
                    .Where(x => x.Key > 0 && x.Count() > 1)
                    .ToFrozenDictionary(x => x.Key, x => x.DistinctBy(d => d.CraftType.RowId).ToList());

    private const int   RECIPE_NAME_WEIGHT         = 1000;
    private const int   RECIPE_PINYIN_WEIGHT       = 900;
    private const int   MAX_QUICK_SYNTHESIS_COUNT  = 255;
    private const uint  NON_EQUIPMENT_GROUP_OFFSET = 0x10000;
    private const int   RECIPE_STRING_ARRAY_INDEX  = 26;
    private const int   SEARCH_PAGE_SIZE           = 100;
    private const int   SEARCH_GROUP_STRING_OFFSET = 67;
    private const int   SEARCH_ENTRY_STRING_COUNT  = 3;
    private const uint  SEARCH_VIEW_ANCHOR_NODE_ID = 56;
    private const float SEARCH_VIEW_BUTTON_SIZE    = 28f;

    private static readonly FrozenSet<uint> CraftFailedLogMessages =
    [
        1131, // <if(gnum7,<sheet(ObjStr,gnum7,0)>,gstr2)>处于异常状态，无法进行制作作业。
        1133,
        1134,
        1135,
        1136,
        1137,
        1138,
        1139,
        1140,
        1141,
        1142,
        1143,
        1144,
        1145,
        1146,
        1147,
        1148,
        1149,
        1160
    ];

    #endregion
}
