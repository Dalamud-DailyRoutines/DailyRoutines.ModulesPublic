using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;
using FateState = Dalamud.Game.ClientState.Fates.FateState;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoFateStart : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoFateStartTitle"),
        Description = Lang.Get("AutoFateStartDescription"),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    protected override void Init() =>
        FrameworkManager.Instance().Reg(OnUpdate, throttleMS: 2_000);
    
    protected override void Uninit() =>
        FrameworkManager.Instance().Unreg(OnUpdate);

    private static void OnUpdate(IFramework framework)
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.Overworld ||
            GameState.IsInPVPArea                                            ||
            LocalPlayerState.ClassJobData.DohDolJobIndex != -1               ||
            DService.Instance().Condition[ConditionFlag.InCombat]            ||
            FateManager.Instance()->CurrentFate != null)
            return;
        
        List<IFate> fatesInPre = [];
        foreach (var fate in DService.Instance().Fate)
        {
            if (fate.State != FateState.Preparing) continue;
            fatesInPre.Add(fate);
        }
        
        if (fatesInPre.Count == 0) return;

        var isAnyWithinRadius = false;
        foreach (var fate in fatesInPre)
        {
            if (LocalPlayerState.DistanceTo2DSquared(fate.Position.ToVector2()) > 100 * 100)
                continue;

            isAnyWithinRadius = true;
            break;
        }
        
        if (!isAnyWithinRadius)
            return;

        foreach (var charaPtr in CharacterManager.Instance()->BattleCharas)
        {
            if (charaPtr                        == null || 
                charaPtr.Value                  == null ||
                charaPtr.Value->NamePlateIconId != 60093)
                continue;
            
            if (!LuminaGetter.TryGetRow(charaPtr.Value->FateId, out Fate row))
                continue;
            
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.FateStart, row.RowId, charaPtr.Value->EntityId);
            if (Throttler.Shared.Throttle($"AutoFateStart-Fate-{row.RowId}", 60_000))
                NotifyHelper.Instance().Chat(Lang.Get("AutoFateStart-StartNotice", row.Name));
            
            return;
        }
    }
}
