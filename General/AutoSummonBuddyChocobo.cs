using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSummonBuddyChocobo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSummonBuddyChocoboTitle"),
        Description = Lang.Get("AutoSummonBuddyChocoboDescription"),
        Category    = ModuleCategory.General,
        Author      = ["Veever"]
    };

    private Config config = null!;

    private bool hasNotifiedInCurrentZone;

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        Cleanup();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoSummonBuddyChocobo-AutoSwitchStance"), ref config.AutoSwitchStance))
            config.Save(this);

        if (config.AutoSwitchStance)
        {
            using (ImRaii.PushIndent())
            {
                var isFirst = true;

                foreach (var checkPoint in Enum.GetValues<ChocoboStance>())
                {
                    if (!LuminaGetter.TryGetRow<BuddyAction>((uint)checkPoint, out var buddyAction)) continue;

                    if (!isFirst)
                        ImGui.SameLine();
                    isFirst = false;

                    if (ImGui.RadioButton(buddyAction.Name.ToString(), config.Stance == checkPoint))
                    {
                        config.Stance = checkPoint;
                        config.Save(this);
                    }
                }
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoSummonBuddyChocobo-NotBattleJobUsingGys"), ref config.NotBattleJobUsingGysahl))
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
            config.Save(this);
    }

    private void OnZoneChanged(uint u)
    {
        Cleanup();

        if (!IsZoneValid()) return;

        LocalPlayerState.Instance().PlayerMoveStateChanged += OnPlayerMoving;
        DService.Instance().Condition.ConditionChange      += OnConditionChanged;
        LogMessageManager.Instance().RegPost(OnLogMessage);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.Mounted) return;

        if (!IsZoneValid())
        {
            Cleanup();
            return;
        }

        if (value) return;

        OnPlayerMoving(false);
    }

    // 给那种原地挂机但一定要陆行鸟在场的人准备的
    private void OnLogMessage(uint logMessageID, LogMessageQueueItem item)
    {
        if (logMessageID != 1328) return;

        if (!IsZoneValid())
        {
            Cleanup();
            return;
        }

        OnPlayerMoving(false);
    }

    private void OnPlayerMoving(bool state)
    {
        if (!IsZoneValid())
        {
            Cleanup();
            return;
        }

        if (state)
        {
            TaskHelper.Abort();
            return;
        }

        if (LocalPlayerState.Object is not { IsDead: false } ||
            DService.Instance().Condition.IsOccupiedInEvent  ||
            DService.Instance().Condition.IsOnMount)
            return;

        if (!config.NotBattleJobUsingGysahl && LocalPlayerState.ClassJobData.DohDolJobIndex != -1)
            return;

        var companionInfo = UIState.Instance()->Buddy.CompanionInfo;

        if (companionInfo.TimeLeft > 300)
        {
            if (config.AutoSwitchStance && companionInfo.ActiveCommand != (int)config.Stance)
                TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.BuddyAction, (uint)config.Stance));
            return;
        }

        if (LocalPlayerState.GetItemCount(GYSAHL_GREENS_ITEM_ID) <= 3)
        {
            if (hasNotifiedInCurrentZone) return;
            hasNotifiedInCurrentZone = true;

            var notificationMessage = Lang.Get("AutoSummonBuddyChocobo-NotificationMessage");
            if (config.SendChat)
                NotifyHelper.Instance().Chat(notificationMessage);
            if (config.SendNotification)
                NotifyHelper.Instance().NotificationInfo(notificationMessage);
            if (config.SendTTS)
                NotifyHelper.Speak(notificationMessage);

            return;
        }

        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue
        (() => { UseActionManager.Instance().UseActionLocation(ActionType.Item, GYSAHL_GREENS_ITEM_ID, extraParam: 0xFFFF); }
        );
    }

    private void Cleanup()
    {
        LocalPlayerState.Instance().PlayerMoveStateChanged -= OnPlayerMoving;
        DService.Instance().Condition.ConditionChange      -= OnConditionChanged;
        LogMessageManager.Instance().Unreg(OnLogMessage);

        TaskHelper?.Abort();

        hasNotifiedInCurrentZone = false;
    }

    // 因为在鸟棚里的话没可能隔空取出来, 必定要先回去取出然后再召唤, 期间要切换至少三个区域
    private static bool IsZoneValid() =>
        !GameState.IsInPVPArea                                           &&
        GameState.TerritoryIntendedUse == TerritoryIntendedUse.Overworld &&
        !PlayerState.Instance()->IsPlayerStateFlagSet(PlayerStateFlag.IsBuddyInStable);

    private enum ChocoboStance
    {
        FreeStance     = 0x04,
        DefenderStance = 0x05,
        AttackerStance = 0x06,
        HealerStance   = 0x07
    }

    private class Config : ModuleConfig
    {
        public bool AutoSwitchStance;

        public bool          NotBattleJobUsingGysahl;
        public bool          SendChat;
        public bool          SendNotification = true;
        public bool          SendTTS;
        public ChocoboStance Stance = ChocoboStance.FreeStance;
    }
    
    #region 常量

    private const uint GYSAHL_GREENS_ITEM_ID = 4868;

    #endregion
}
