using System;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using RecipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote;

namespace DailyRoutines.ModulesPublic;

public unsafe class QuickSynthesisMore : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("QuickSynthesisMoreTitle"),
        Description = GetLoc("QuickSynthesisMoreDescription"),
        Category    = ModuleCategories.System,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true, AllDefaultEnabled = true };

    private static readonly CompSig SimpleCraftAmountJudgeSig = new("0F 87 ?? ?? ?? ?? 48 8B 81 ?? ?? ?? ?? 48 85 C0");
    // ja → nop
    private static readonly MemoryPatch SimpleCraftAmountJudgePatch =
        new(SimpleCraftAmountJudgeSig.Get(), [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]);

    private static readonly CompSig                                       SimpleCraftGetAmountUpperLimitSig = new("4C 8B DC 48 83 EC ?? 48 8B 81 ?? ?? ?? ?? 44 0F B6 CA");
    private delegate        int                                           SimpleCraftGetAmountUpperLimitDelegate(nint agent, bool eventCase);
    private static          Hook<SimpleCraftGetAmountUpperLimitDelegate>? SimpleCraftGetAmountUpperLimitHook;

    protected override void Init()
    {
        SimpleCraftAmountJudgePatch.Enable();

        SimpleCraftGetAmountUpperLimitHook ??= 
            SimpleCraftGetAmountUpperLimitSig.GetHook<SimpleCraftGetAmountUpperLimitDelegate>(SimpleCraftGetAmountUpperLimitDetour);
        SimpleCraftGetAmountUpperLimitHook.Enable();
    }

    public static int SimpleCraftGetAmountUpperLimitDetour(nint agentRecipeNote, bool isHQ)
    {
        var selectedRecipe = RecipeNote.Instance()->RecipeList->SelectedRecipe;
        if (selectedRecipe == null) return 0;

        var maxPortion = 255;
        foreach (var ingredient in selectedRecipe->Ingredients)
        {
            if (ingredient.ItemId == 0) continue;

            var itemCountNQ = InventoryManager.Instance()->GetInventoryItemCount(ingredient.ItemId);
            var itemCountHQ = InventoryManager.Instance()->GetInventoryItemCount(ingredient.ItemId, true);

            var itemCount = isHQ ? itemCountNQ + itemCountHQ : itemCountNQ;
            if (itemCount == 0) return 0;

            var portion = itemCount / ingredient.Amount;
            if (portion == 0) return 0;

            portion = Math.Min(255, portion);
            maxPortion = Math.Min(portion, maxPortion);
        }

        return maxPortion;
    }

    protected override void Uninit() => 
        SimpleCraftAmountJudgePatch.Disable();
}
