using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoReplaceLowBlowWithInterject : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("AutoReplaceLowBlowWithInterjectTitle"),
        Description = GetLoc("AutoReplaceLowBlowWithInterjectDescription"),
        Category    = ModuleCategories.Action
    };

    private static readonly CompSig IsActionReplaceableSig = new("E8 ?? ?? ?? ?? 84 C0 74 69 8B D3");
    private delegate        bool IsActionReplaceableDelegate(uint actionID);
    private static          Hook<IsActionReplaceableDelegate> IsActionReplaceableHook;

    private static readonly CompSig GetAdjustedActionIDSig = new("E8 ?? ?? ?? ?? 89 03 8B 03");
    private delegate        uint GetAdjustedActionIDDelegate(ActionManager* manager, uint actionID);
    private static          Hook<GetAdjustedActionIDDelegate> GetAdjustedActionIDHook;

    private static readonly CompSig GetIconIDForSlotSig = new("40 57 48 83 EC ?? 0F B6 C2 41 8B F8");
    private delegate uint GetIconIDForSlotDelegate(
        RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint actionID);
    private static Hook<GetIconIDForSlotDelegate> GetIconIDForSlotHook;
    
    public override void Init()
    {
        IsActionReplaceableHook ??= IsActionReplaceableSig.GetHook<IsActionReplaceableDelegate>(IsActionReplaceableDetour);
        IsActionReplaceableHook.Enable();

        GetAdjustedActionIDHook ??= GetAdjustedActionIDSig.GetHook<GetAdjustedActionIDDelegate>(GetAdjustedActionIDDetour);
        GetAdjustedActionIDHook.Enable();

        GetIconIDForSlotHook ??= GetIconIDForSlotSig.GetHook<GetIconIDForSlotDelegate>(GetIconIDForSlotDetour);
        GetIconIDForSlotHook.Enable();
    }
    
    private static uint GetAdjustedActionIDDetour(ActionManager* manager, uint actionID) 
        => actionID == 7540 && IsReplaceNeeded() ? 7538 : GetAdjustedActionIDHook.Original(manager, actionID);

    private static uint GetIconIDForSlotDetour(
        RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint actionID) =>
        type == RaptureHotbarModule.HotbarSlotType.Action && IsReplaceNeeded()
            ? 808
            : GetIconIDForSlotHook.Original(slot, type, actionID);

    private static bool IsActionReplaceableDetour(uint actionID) => true;

    private static bool IsReplaceNeeded() => ActionManager.Instance()->IsActionOffCooldown(ActionType.Action, 7538) &&
                                             (DService.Targets.Target is IBattleChara { IsCastInterruptible: true } ||
                                              DService.Targets.Target is IBattleNpc { IsCastInterruptible  : true });
}
