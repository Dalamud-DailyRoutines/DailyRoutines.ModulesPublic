using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic.AutoRecordPartyFinderSettings;

// TODO: 可以加个备注, 以后的 KPI
public unsafe partial class AutoRecordPartyFinderSettings : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRecordPartyFinderSettingTitle"),
        Description = Lang.Get("AutoRecordPartyFinderSettingDescription"),
        Category    = ModuleCategory.Recruitment,
        Author      = ["status102"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private bool isAppliedOnce;

    private AutoRecordPartyFinderSettingAddon? addon;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new();

        addon ??= new(this)
        {
            InternalName          = "DRAutoRecordPartyFinderSetting",
            Title                 = Info.Title,
            Size                  = new(340f, 410f),
            RememberClosePosition = false
        };

        DService.Instance().AgentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.LookingForGroup, OnAgent);
    }

    protected override void Uninit()
    {
        DService.Instance().AgentLifecycle.UnregisterListener(OnAgent);

        addon?.Dispose();
        addon = null;
    }

    private void OnAgent(AgentEvent type, AgentArgs args)
    {
        var formatted = args as AgentReceiveEventArgs;

        var atkValues = (AtkValue*)formatted.AtkValues;
        if (atkValues == null) return;

        if (formatted.EventKind != 3) return;

        var eventCase = atkValues[0].Int;

        switch (eventCase)
        {
            // 装等
            case 11:
                config.Last.AvgIL         = atkValues[1].UInt;
                config.Last.IsEnableAvgIL = atkValues[2].Bool;
                break;

            // 副本
            case 12:
                config.Last.Category = atkValues[1].UInt;
                config.Last.Duty     = 0;
                break;

            // 副本
            case 13:
                config.Last.Duty = atkValues[1].UInt;
                break;

            // 描述
            case 15:
                var bytes = atkValues[1].String.AsReadOnlySeString().Data.ToArray().ToList();
                bytes.Add(0);

                config.Last.DescriptionBytes = bytes.ToArray();
                break;

            // 密码
            case 16:
                config.Last.Password = atkValues[1].Int;
                break;

            case 18:
                switch (atkValues[2].UInt)
                {
                    case 1:
                        config.Last.WelcomeNew = true;
                        break;
                    case 3:
                        switch (atkValues[1].UInt)
                        {
                            case 0:
                                config.Last.Unrestricted = true;
                                break;
                            case 1:
                                config.Last.MinIL = true;
                                break;
                            case 2:
                                config.Last.SilenceEcho = true;
                                break;
                        }

                        break;
                }

                config.Last.WelcomeNew = true;
                break;
            case 19:
                switch (atkValues[2].UInt)
                {
                    case 1:
                        config.Last.WelcomeNew = false;
                        break;
                    case 3:
                        switch (atkValues[1].UInt)
                        {
                            case 0:
                                config.Last.Unrestricted = false;
                                break;
                            case 1:
                                config.Last.MinIL = false;
                                break;
                            case 2:
                                config.Last.SilenceEcho = false;
                                break;
                        }

                        break;
                }

                break;

            case 20:
                switch (atkValues[2].UInt)
                {
                    // 目的
                    case 0:
                        config.Last.Goal = atkValues[1].UInt;
                        break;
                    // 完成度要求
                    case 2 when atkValues[1].UInt < 3:
                        if (atkValues[1].UInt == 1)
                        {
                            config.Last.SetFinishRequirement = atkValues[1].UInt;
                            break;
                        }

                        if (atkValues[1].UInt == 3)
                        {
                            config.Last.FinishRequirement = atkValues[1].UInt;
                            break;
                        }

                        config.Last.SetFinishRequirement = atkValues[1].UInt;
                        config.Last.FinishRequirement    = atkValues[1].UInt;
                        break;
                    case 2:
                        config.Last.FinishRequirement = atkValues[1].UInt;
                        break;
                    // 分配方式
                    case 4:
                        config.Last.LootType = atkValues[1].UInt;
                        break;
                }

                break;

            // 设定密码
            case 21:
                config.Last.IsSetPassword = atkValues[1].UInt;
                break;

            // 在服务器内
            case 22:
                config.Last.IsLocal = atkValues[1].UInt;
                break;

            // 职业不重复
            case 23:
                config.Last.NoSameJob = atkValues[1].UInt;
                break;

            // 所有人
            case 33:
                config.Last.SetAllSlotsEveryone = atkValues[1].Bool;
                break;

            // 仅特职
            case 34:
                config.Last.OnlyClassJob = atkValues[1].Bool;
                break;

            // 队伍组成形式
            case 35:
                config.Last.Type = atkValues[1].UInt;
                break;

            // 不招募观战
            case 36:
                config.Last.NoAudiences = atkValues[1].Bool;
                break;

            default:
                return;
        }

        config.Save(this);
    }

    private void ApplyPreset(PartyFinderSetting setting)
    {
        if (!LookingForGroup->IsAddonAndNodesReady() || !LookingForGroupCondition->IsAddonAndNodesReady())
            return;

        if (setting.Type != null)
            TaskHelper.Enqueue(() => SendEvent(35, setting.Type.Value, 0));
        if (setting.Category != null)
        {
            TaskHelper.Enqueue(() => SendEvent(12, setting.Category.Value, 0));
            if (setting.Category.Value != 0 && setting.Duty != null)
                TaskHelper.Enqueue(() => SendEvent(13, setting.Duty.Value, 0));
        }

        if (setting.IsEnableAvgIL != null)
        {
            TaskHelper.Enqueue(() => SendEvent(11, 1, setting.IsEnableAvgIL.Value));
            if (setting.IsEnableAvgIL.Value && setting.AvgIL != null)
                TaskHelper.Enqueue(() => SendEvent(11, setting.AvgIL.Value, setting.IsEnableAvgIL.Value));
        }

        if (setting.DescriptionBytes != null)
            TaskHelper.Enqueue(() => SendEvent(15, new ReadOnlySeString(setting.DescriptionBytes), 0));

        if (setting.IsSetPassword != null)
        {
            TaskHelper.Enqueue(() => SendEvent(21, setting.IsSetPassword.Value, 0));
            if (setting.IsSetPassword.Value != 0 && setting.Password != null)
                TaskHelper.Enqueue(() => SendEvent(16, setting.Password.Value, 0));
        }

        if (setting.WelcomeNew != null)
            TaskHelper.Enqueue(() => SendEvent(setting.WelcomeNew.Value ? 18 : 19, 0, 1));
        if (setting.Unrestricted != null)
            TaskHelper.Enqueue(() => SendEvent(setting.Unrestricted.Value ? 18 : 19, 0, 3));
        if (setting.MinIL != null)
            TaskHelper.Enqueue(() => SendEvent(setting.MinIL.Value ? 18 : 19, 1, 3));
        if (setting.SilenceEcho != null)
            TaskHelper.Enqueue(() => SendEvent(setting.SilenceEcho.Value ? 18 : 19, 2, 3));
        if (setting.Goal != null)
            TaskHelper.Enqueue(() => SendEvent(20, setting.Goal.Value, 0));

        if (setting.SetFinishRequirement != null)
        {
            TaskHelper.Enqueue(() => SendEvent(20, setting.SetFinishRequirement.Value, 2));
            if (setting.SetFinishRequirement.Value != 1 && setting.FinishRequirement != null)
                TaskHelper.Enqueue(() => SendEvent(20, setting.FinishRequirement.Value, 2));
        }

        if (setting.LootType != null)
            TaskHelper.Enqueue(() => SendEvent(20, setting.LootType.Value, 4));
        if (setting.IsLocal != null)
            TaskHelper.Enqueue(() => SendEvent(22, setting.IsLocal.Value, 0));
        if (setting.NoSameJob != null)
            TaskHelper.Enqueue(() => SendEvent(23, setting.NoSameJob.Value, 0));
        if (setting.SetAllSlotsEveryone != null)
            TaskHelper.Enqueue(() => SendEvent(33, setting.SetAllSlotsEveryone.Value, 0));
        if (setting.OnlyClassJob != null)
            TaskHelper.Enqueue(() => SendEvent(34, setting.OnlyClassJob.Value, 0));
        if (setting.NoAudiences != null)
            TaskHelper.Enqueue(() => SendEvent(36, setting.NoAudiences.Value, 0));
        
        if (TaskHelper.IsBusy)
        {
            TaskHelper.Enqueue(() => LookingForGroupCondition->Close(true));
            TaskHelper.Enqueue(() => LookingForGroup->Callback(14));
        }

        return;

        void SendEvent(params object[] args) =>
            AgentId.LookingForGroup.SendEvent(3, args);
    }

    private class PartyFinderSetting
    {
        // 任务类别 - Index
        public uint? Category = 0;

        // 副本 - Index
        public uint? Duty = 0;

        // 欢迎新人
        public bool? WelcomeNew = false;

        // 目的 - Index
        public uint? Goal = 1;

        // 设立完成度要求
        public uint? SetFinishRequirement = 1;

        public uint? FinishRequirement = 2;

        // 将空位全部设置为所有人
        public bool? SetAllSlotsEveryone = false;

        // 仅特职
        public bool? OnlyClassJob = false;

        // 职业不重复
        public uint? NoSameJob = 0;

        // 在服务器内
        public uint? IsLocal = 1;

        // 设定密码
        public uint? IsSetPassword = 0;

        // 密码
        public int? Password = 0000;

        // 解除限制
        public bool? Unrestricted = false;

        // 最低装等
        public bool? MinIL = false;

        // 超越之力无效化
        public bool? SilenceEcho = false;

        // 分配方式
        public uint? LootType = 0;

        // 队伍组成形式
        public uint? Type = 0;

        // 不招募观战（水晶冲突）
        public bool? NoAudiences = false;

        // 招募描述
        public byte[]? DescriptionBytes = [0];

        // 启用装等
        public bool? IsEnableAvgIL = false;
        
        // 装等
        public uint? AvgIL = 1;

        // 界面显示用
        public string? DisplayName;

        // 兼容用
        public string? Name
        {
            get;
            set
            {
                field = null;
                if (string.IsNullOrEmpty(value)) return;
                
                DisplayName = value;
            }
        }
        
        // 兼容用
        public string? Description
        {
            get;
            set
            {
                field = null;
                if (string.IsNullOrEmpty(value)) return;
                DescriptionBytes = Encoding.UTF8.GetBytes(value + "\0");
            }
        }

        public PartyFinderSetting Copy() =>
            new()
            {
                DisplayName          = DisplayName,
                Category             = Category,
                Duty                 = Duty,
                AvgIL                = AvgIL,
                IsEnableAvgIL        = IsEnableAvgIL,
                WelcomeNew           = WelcomeNew,
                Goal                 = Goal,
                SetFinishRequirement = SetFinishRequirement,
                FinishRequirement    = FinishRequirement,
                SetAllSlotsEveryone  = SetAllSlotsEveryone,
                OnlyClassJob         = OnlyClassJob,
                NoSameJob            = NoSameJob,
                IsLocal              = IsLocal,
                IsSetPassword        = IsSetPassword,
                Password             = Password,
                Unrestricted         = Unrestricted,
                MinIL                = MinIL,
                SilenceEcho          = SilenceEcho,
                LootType             = LootType,
                Type                 = Type,
                NoAudiences          = NoAudiences,
                DescriptionBytes     = DescriptionBytes
            };
    }

    private class Config : ModuleConfig
    {
        public override string PreviousModuleName => "AutoRecordPartyFinderSetting";

        public PartyFinderSetting Last = new();

        public List<PartyFinderSetting> Slot = [];
    }
}
