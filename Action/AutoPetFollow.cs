using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DailyRoutines.Modules;

public class AutoPetFollow : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoPetFollowTitle"),
        Description = GetLoc("AutoPetFollowDescription"),
        Category = ModuleCategories.Action,
    };

    private static Config ModuleConfig = null!;
    
    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.Condition.ConditionChange += OnConditionChanged;
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
    }

    private static unsafe void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is not ConditionFlag.InCombat || value) return;

        if (DService.ClientState.LocalPlayer is not { ClassJob.RowId: 26 or 27 or 28 } player) return;

        var isPetSummoned = CharacterManager.Instance()->LookupPetByOwnerObject((BattleChara*)player.Address) != null;
        if (!isPetSummoned) return;

        ExecuteCommandManager.ExecuteCommandComplex(ExecuteCommandComplexFlag.PetAction, 0xE0000000, 2);

        if (ModuleConfig.SendNotification)
            NotificationInfo(GetLoc("AutoPetFollow-Notification"));
    }

    public override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;
    }

    public class Config : ModuleConfiguration
    {
        public bool SendNotification = true;
    }
}
