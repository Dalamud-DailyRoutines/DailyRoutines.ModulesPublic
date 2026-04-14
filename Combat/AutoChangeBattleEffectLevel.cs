using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Config;
using Dalamud.Interface.Components;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoChangeBattleEffectLevel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoChangeBattleEffectLevelTitle"),
        Description = Lang.Get("AutoChangeBattleEffectLevelDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["Siren"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config         config = null!;
    private EffectSetting? lastAppliedSettings;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        PlayersManager.Instance().ReceivePlayersAround += OnPlayerReceived;

        if (GameState.IsLoggedIn)
            OnPlayerReceived([]);
    }

    protected override void Uninit() =>
        PlayersManager.Instance().ReceivePlayersAround -= OnPlayerReceived;

    protected override void ConfigUI()
    {
        using var tab = ImRaii.TabBar("TabBar");
        if (!tab) return;

        using (var item = ImRaii.TabItem(Lang.Get("OutOfDuty")))
        {
            if (item)
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("AutoChangeBattleEffectLevel-PlayerThreshold"));

                using (ImRaii.PushIndent())
                {
                    ImGui.SetNextItemWidth(100f * GlobalUIScale);
                    ImGui.InputUInt($"{LuminaWrapper.GetAddonText(16347)}##LimitLow", ref config.AroundCountThresholdLow);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        config.Save(this);

                    ImGui.SetNextItemWidth(100f * GlobalUIScale);
                    ImGui.InputUInt($"{LuminaWrapper.GetAddonText(16346)}##LimitHigh", ref config.AroundCountThresholdHigh);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        config.Save(this);
                }

                ImGui.NewLine();

                if (ImGui.CollapsingHeader
                    (
                        $"＜ {DService.Instance().SeStringEvaluator.EvaluateFromAddon(12871, [config.AroundCountThresholdLow])}",
                        ImGuiTreeNodeFlags.DefaultOpen
                    ))
                    DrawBattleEffectSetting("Low", config.OverworldLow);

                if (ImGui.CollapsingHeader
                    (
                        $"{DService.Instance().SeStringEvaluator.EvaluateFromAddon(12871, [config.AroundCountThresholdLow])}" +
                        $" ≤ X ≤ "                                                                                                  +
                        $"{DService.Instance().SeStringEvaluator.EvaluateFromAddon(12871, [config.AroundCountThresholdHigh])}",
                        ImGuiTreeNodeFlags.DefaultOpen
                    ))
                    DrawBattleEffectSetting("Medium", config.OverworldMedium);

                if (ImGui.CollapsingHeader
                    (
                        $"＞ {DService.Instance().SeStringEvaluator.EvaluateFromAddon(12871, [config.AroundCountThresholdHigh])}",
                        ImGuiTreeNodeFlags.DefaultOpen
                    ))
                    DrawBattleEffectSetting("High", config.OverworldHigh);
            }
        }

        using (var item = ImRaii.TabItem(Lang.Get("InDuty")))
        {
            if (item)
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Default"));

                using (ImRaii.PushIndent())
                    DrawBattleEffectSetting("DefaultDuty", config.DutyDefault);

                ImGui.NewLine();

                foreach (var contentType in LuminaGetter.Get<ContentType>())
                {
                    var name = contentType.Name.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    if (config.DutySpecific.TryAdd(contentType.RowId, new()))
                        config.Save(this);

                    var setting = config.DutySpecific[contentType.RowId];

                    if (!ImageHelper.TryGetGameIcon(contentType.Icon, out var image)) continue;

                    if (ImGuiOm.TreeNodeImageWithText(image.Handle, new(ImGui.GetTextLineHeightWithSpacing()), $"{name} ({contentType.RowId})"))
                    {
                        DrawBattleEffectSetting($"Duty_{contentType.RowId}", setting);
                        ImGui.TreePop();
                    }
                }
            }
        }
    }

    private void DrawBattleEffectSetting(string id, EffectSetting setting)
    {
        using var idPush = ImRaii.PushId(id);

        var isEnabled = setting.IsEnabled;

        if (ImGuiComponents.ToggleButton("Enable", ref isEnabled))
        {
            setting.IsEnabled = isEnabled;
            config.Save(this);
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Lang.Get("Enable"));

        if (!isEnabled) return;

        ImGui.Spacing();
        ImGui.Spacing();

        var selfSetting = setting.Self;

        if (DrawBattleEffectLevelCombo(LuminaWrapper.GetAddonText(4087), ref selfSetting))
        {
            setting.Self = selfSetting;
            config.Save(this);
        }

        ImGui.Spacing();

        var partySetting = setting.Party;

        if (DrawBattleEffectLevelCombo(LuminaWrapper.GetAddonText(4088), ref partySetting))
        {
            setting.Party = partySetting;
            config.Save(this);
        }

        ImGui.Spacing();

        var otherSetting = setting.Other;

        if (DrawBattleEffectLevelCombo(LuminaWrapper.GetAddonText(4089), ref otherSetting))
        {
            setting.Other = otherSetting;
            config.Save(this);
        }

        ImGui.Spacing();

        var enemySetting = setting.Enemy;

        if (DrawBattleEffectLevelCombo(LuminaWrapper.GetAddonText(4109), ref enemySetting))
        {
            setting.Enemy = enemySetting;
            config.Save(this);
        }
    }

    private static bool DrawBattleEffectLevelCombo(string label, ref BattleEffectLevel value)
    {
        var returnValue = false;

        using var id = ImRaii.PushId(label);

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), label);

        using var indent = ImRaii.PushIndent();

        ImGui.Spacing();

        foreach (var level in Enum.GetValues<BattleEffectLevel>())
        {
            ImGui.SameLine(0, 10f * GlobalUIScale);

            if (ImGui.RadioButton(LuminaWrapper.GetAddonText((uint)level + 7823), level == value))
            {
                value       = level;
                returnValue = true;
            }
        }

        return returnValue;
    }

    private void OnPlayerReceived(IReadOnlyList<IPlayerCharacter> characters)
    {
        EffectSetting? targetSetting = null;

        if (GameState.ContentFinderCondition > 0)
        {
            if (config.DutySpecific.TryGetValue(GameState.ContentFinderConditionData.ContentType.RowId, out var specificConfig) &&
                specificConfig.IsEnabled)
                targetSetting = specificConfig;

            targetSetting ??= config.DutyDefault;
        }
        else
        {
            var playerCount = PlayersManager.Instance().PlayersAroundCount;

            if (playerCount < config.AroundCountThresholdLow)
                targetSetting = config.OverworldLow;
            else if (playerCount < config.AroundCountThresholdHigh)
                targetSetting = config.OverworldMedium;
            else
                targetSetting = config.OverworldHigh;
        }

        if (targetSetting is not { IsEnabled: true }) return;

        ApplySetting(targetSetting);
    }

    private void ApplySetting(EffectSetting? settings)
    {
        if (settings == null) return;
        if (lastAppliedSettings != null && settings == lastAppliedSettings)
            return;

        try
        {
            DService.Instance().GameConfig.UiConfig.Set(nameof(UiConfigOption.BattleEffectSelf),       (uint)settings.Self);
            DService.Instance().GameConfig.UiConfig.Set(nameof(UiConfigOption.BattleEffectParty),      (uint)settings.Party);
            DService.Instance().GameConfig.UiConfig.Set(nameof(UiConfigOption.BattleEffectOther),      (uint)settings.Other);
            DService.Instance().GameConfig.UiConfig.Set(nameof(UiConfigOption.BattleEffectPvPEnemyPc), (uint)settings.Enemy);

            lastAppliedSettings = settings.Clone();
        }
        catch
        {
            // ignored
        }
    }

    private class Config : ModuleConfig
    {
        public uint AroundCountThresholdHigh = 40;
        public uint AroundCountThresholdLow  = 20;

        public EffectSetting DutyDefault = new()
        {
            Self  = BattleEffectLevel.All,
            Party = BattleEffectLevel.All,
            Other = BattleEffectLevel.Limited,
            Enemy = BattleEffectLevel.All
        };

        public Dictionary<uint, EffectSetting> DutySpecific = [];

        public EffectSetting OverworldHigh = new()
        {
            Self  = BattleEffectLevel.All,
            Party = BattleEffectLevel.None,
            Other = BattleEffectLevel.None,
            Enemy = BattleEffectLevel.All
        };

        public EffectSetting OverworldLow = new()
        {
            Self  = BattleEffectLevel.All,
            Party = BattleEffectLevel.All,
            Other = BattleEffectLevel.All,
            Enemy = BattleEffectLevel.All
        };

        public EffectSetting OverworldMedium = new()
        {
            Self  = BattleEffectLevel.All,
            Party = BattleEffectLevel.Limited,
            Other = BattleEffectLevel.None,
            Enemy = BattleEffectLevel.All
        };
    }

    private sealed class EffectSetting : IEquatable<EffectSetting>
    {
        public bool IsEnabled { get; set; }

        /// <summary>
        ///     自己
        /// </summary>
        public BattleEffectLevel Self { get; set; }

        /// <summary>
        ///     小队
        /// </summary>
        public BattleEffectLevel Party { get; set; } = BattleEffectLevel.None;

        /// <summary>
        ///     他人
        /// </summary>
        public BattleEffectLevel Other { get; set; } = BattleEffectLevel.None;

        /// <summary>
        ///     对战时的敌方玩家
        /// </summary>
        public BattleEffectLevel Enemy { get; set; }

        public bool Equals(EffectSetting? other)
        {
            if (other is null) return false;
            return Self  == other.Self  &&
                   Party == other.Party &&
                   Other == other.Other &&
                   Enemy == other.Enemy;
        }

        public EffectSetting Clone() =>
            (EffectSetting)MemberwiseClone();

        public override bool Equals(object? obj) =>
            Equals(obj as EffectSetting);

        public override int GetHashCode() =>
            HashCode.Combine(Self, Party, Other, Enemy);

        public static bool operator ==(EffectSetting? left, EffectSetting? right) =>
            Equals(left, right);

        public static bool operator !=(EffectSetting? left, EffectSetting? right) =>
            !Equals(left, right);
    }

    private enum BattleEffectLevel : uint
    {
        /// <summary>
        ///     完全显示
        /// </summary>
        All,

        /// <summary>
        ///     简单显示
        /// </summary>
        Limited,

        /// <summary>
        ///     不显示
        /// </summary>
        None
    }
}
