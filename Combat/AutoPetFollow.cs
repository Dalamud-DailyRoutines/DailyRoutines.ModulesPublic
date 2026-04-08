using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;
using OmenTools.Threading;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public class AutoPetFollow : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoPetFollowTitle"),
        Description = Lang.Get("AutoPetFollowDescription"),
        Category    = ModuleCategory.Combat
    };
    
    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
    }
    
    protected override void Uninit() =>
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);
    }

    private unsafe void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat                       ||
            value                                                ||
            GameState.IsInPVPArea                                ||
            DService.Instance().Condition[ConditionFlag.Mounted] ||
            !ValidClassJobs.Contains(LocalPlayerState.ClassJob))
            return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var pet = CharacterManager.Instance()->LookupPetByOwnerObject(localPlayer);
        if (pet == null || !pet->GetIsTargetable()) return;

        ExecuteCommandManager.Instance().ExecuteCommandComplex(ExecuteCommandComplexFlag.PetAction, 0xE0000000, 2);

        if (config.SendNotification && Throttler.Shared.Throttle("AutoPetFollow-SendNotification", 10_000))
            NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoPetFollow-Notification"));
    }
    
    private class Config : ModuleConfig
    {
        public bool SendNotification = true;
    }
    
    #region 常量
    
    private static readonly FrozenSet<uint> ValidClassJobs = [26, 27, 28];
    
    #endregion
}
