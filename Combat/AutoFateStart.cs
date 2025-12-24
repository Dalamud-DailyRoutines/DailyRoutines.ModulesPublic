using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public class AutoFateStart : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoFateStartTitle"),
        Description = GetLoc("AutoFateStartDescription"),
        Category    = ModuleCategories.Combat,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static bool IsOnUpdate;
    
    protected override void Init() => 
        FrameworkManager.Reg(OnUpdate, throttleMS: 1000);

    private static unsafe void OnUpdate(IFramework _)
    {
        if (IsOnUpdate) return;
        
        try
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.Overworld || GameState.IsInPVPArea) return;
            
            IsOnUpdate = true;
            
            foreach (var obj in DService.ObjectTable)
            {
                if (obj.ObjectKind != ObjectKind.BattleNpc) continue;

                var gameObj = obj.ToStruct();
                if (gameObj == null || gameObj->NamePlateIconId != 60093 || gameObj->FateId == 0) continue;

                if (!LuminaGetter.TryGetRow<Fate>(gameObj->FateId, out var fateData)) continue;
                if (!Throttler.Throttle($"AutoFateStart-{fateData.Name.ExtractText()}", 1_000)) continue;
            
                ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.FateStart, gameObj->FateId, gameObj->EntityId);
                Chat(GetLoc("AutoFateStart-StartNotice", fateData.Name.ExtractText(), gameObj->NameString));
                break;
            }
        }
        finally
        {
            IsOnUpdate = false;
        }
    }

    protected override void Uninit()
    {
        FrameworkManager.Unreg(OnUpdate);
        IsOnUpdate = false;
    }
}
