using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayMSQProgress : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDisplayMSQProgressTitle"),
        Description = GetLoc("AutoDisplayMSQProgressDescription"),
        Category    = ModuleCategories.UIOptimization
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "ScenarioTree", OnAddon);
        if (IsAddonAndNodesReady(InfosOm.ScenarioTree))
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Throttle("ScenarioTree", 1_000)) return;
        
        var addon = InfosOm.ScenarioTree;
        if (!IsAddonAndNodesReady(addon)) return;

        if (!TryGetCurrentExpansionMSQProgress(out var result)) return;
        if (result.Remaining == 0) return;
        if (!LuminaGetter.TryGetRow<Quest>(result.FirstIncompleteQuest, out var questData)) return;
        
        var text = $"{questData.Name.ExtractText()} ({result.Remaining} / {result.PercentComplete:F1}%)";
        addon->AtkValues[7].SetManagedString(text);
        addon->OnRefresh(addon->AtkValuesCount, addon->AtkValues);

        var button = addon->GetComponentButtonById(13);
        if (button == null) return;
        
        var textNode = (AtkTextNode*)button->UldManager.SearchNodeById(6);
        if (textNode == null) return;
        
        textNode->SetText(text);
    }

    private static bool TryGetCurrentExpansionMSQProgress(out MSQProgressResult result)
    {
        var uiState = UIState.Instance();

        var msqQuests = LuminaGetter.Get<Quest>()
                                    .Where(x => x.JournalGenre.Value.Icon == 61412 && !string.IsNullOrEmpty(x.Name.ToString()))
                                    .ToList();
        
        var firstIncompleteID   = (uint)AgentScenarioTree.Instance()->Data->CurrentScenarioQuest + 65536;
        var firstIncompleteData = LuminaGetter.GetRowOrDefault<Quest>(firstIncompleteID);
        
        var currentExpansionQuests = msqQuests
                                     .Where(x => x.JournalGenre.RowId == firstIncompleteData.JournalGenre.RowId)
                                     .OrderBy(x => x.RowId)
                                     .ToList();
        
        var completedCount = 0;
        foreach (var quest in currentExpansionQuests)
        {
            var isCompleted = uiState->IsUnlockLinkUnlockedOrQuestCompleted(
                quest.RowId,
                quest.TodoParams.Max(x => x.ToDoCompleteSeq));

            if (isCompleted)
                completedCount++;
        }
        
        var totalCount = firstIncompleteData.JournalGenre.RowId == 1
                             ? AdjustARRTotalCount(currentExpansionQuests.Count)
                             : currentExpansionQuests.Count;
        
        var remaining = totalCount - completedCount;
        var percentComplete = totalCount > 0
                                  ? completedCount * 100f / totalCount
                                  : 100f;

        result = new(remaining, percentComplete, firstIncompleteID);
        return true;
    }

    private static int AdjustARRTotalCount(int baseCount)
    {
        var adjustedCount = baseCount;
        var playerState   = PlayerState.Instance();

        if (playerState->StartTown != 1)
            adjustedCount -= 23;
        if (playerState->StartTown != 2)
            adjustedCount -= 23;
        if (playerState->StartTown != 3)
            adjustedCount -= 24;

        return adjustedCount - 8;
    }
    
    private readonly struct MSQProgressResult(int remaining, float percentComplete, uint firstIncompleteQuest)
    {
        public readonly int   Remaining              = remaining;
        public readonly float PercentComplete        = percentComplete;
        public readonly uint  FirstIncompleteQuest = firstIncompleteQuest;
    }
}
