using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DailyRoutines.ModulesPublic;

public class AutoPetFollow : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoPetFollowTitle"),
        Description = GetLoc("AutoPetFollowDescription"),
        Category    = ModuleCategories.Action,
    };

    private static readonly HashSet<uint> ValidClassJobs = [26, 27, 28];
    
    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.Condition.ConditionChange += OnConditionChanged;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
    }

    private static unsafe void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat            ||
            value                                     ||
            GameState.IsInPVPArea                     ||
            DService.Condition[ConditionFlag.Mounted] ||
            !ValidClassJobs.Contains(LocalPlayerState.ClassJob))
            return;

        var localPlayer   = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var pet = CharacterManager.Instance()->LookupPetByOwnerObject(localPlayer);
        if (pet == null || !pet->GetIsTargetable()) return;

        ExecuteCommandManager.ExecuteCommandComplex(ExecuteCommandComplexFlag.PetAction, 0xE0000000, 2);

        if (ModuleConfig.SendNotification)
            NotificationInfo(GetLoc("AutoPetFollow-Notification"));
    }

    protected override void Uninit() => 
        DService.Condition.ConditionChange -= OnConditionChanged;

    public class Config : ModuleConfiguration
    {
        public bool SendNotification = true;
    }
}
