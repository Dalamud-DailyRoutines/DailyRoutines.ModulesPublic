using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using ContentRoulette = Lumina.Excel.Sheets.ContentRoulette;
using InstanceContent = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifyRouletteBonus : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoNotifyRouletteBonus"),
        Description = GetLoc("AutoNotifyRouletteBonusDescription"),
        Category = ModuleCategories.Notice,
        Author = ["BoxingBunny"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private const int ROULETTE_BONUS_ARRAY_SIZE = 11;

    private static readonly FrozenDictionary<byte, uint> RouletteIndexToRowID = new Dictionary<byte, uint>
    {
        [1] = 1, [2] = 2, [3] = 3, [4] = 4, [5] = 5,
        [6] = 6, [7] = 8, [8] = 9, [9] = 15, [10] = 17
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<ContentsRouletteRole, RoleData> RoleDataMap = new Dictionary<ContentsRouletteRole, RoleData>
    {
        [ContentsRouletteRole.Tank] = new(BitmapFontIcon.Tank, 37, KnownColor.DodgerBlue.ToVector4(), LuminaWrapper.GetAddonText(1082)),
        [ContentsRouletteRole.Healer] = new(BitmapFontIcon.Healer, 504, KnownColor.LimeGreen.ToVector4(), LuminaWrapper.GetAddonText(1083)),
        [ContentsRouletteRole.Dps] = new(BitmapFontIcon.DPS, 545, KnownColor.OrangeRed.ToVector4(), LuminaWrapper.GetAddonText(2786))
    }.ToFrozenDictionary();

    private static readonly ContentRoulette[] CachedRoulettes =
        LuminaGetter.Get<ContentRoulette>()
            .Where(x => x is { RowId: > 0, ContentRouletteRoleBonus.RowId: > 0 })
            .OrderBy(x => x.ContentRouletteRoleBonus.RowId)
            .ToArray();

    private static Config ModuleConfig = null!;
    private static ContentsRouletteRole[]? LastKnownRoles;
    private static bool PendingRefreshAfterDuty;

    private static readonly CompSig SetContentRouletteRoleBonusSig = new("48 89 4C 24 ?? 55 41 56 48 83 EC ?? ?? ?? ?? 4C 8B F1");
    private delegate nint SetContentRouletteRoleBonusDelegate(nint a1, nint a2, uint a3);
    private static Hook<SetContentRouletteRoleBonusDelegate>? SetContentRouletteRoleBonusHook;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        if (LastKnownRoles is null)
        {
            LastKnownRoles = new ContentsRouletteRole[ROULETTE_BONUS_ARRAY_SIZE];                                                                                                                 
            Array.Fill(LastKnownRoles, (ContentsRouletteRole)3);  
        }

        SetContentRouletteRoleBonusHook ??= SetContentRouletteRoleBonusSig.GetHook<SetContentRouletteRoleBonusDelegate>(SetContentRouletteRoleBonusDetour);
        SetContentRouletteRoleBonusHook.Enable();

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
        PendingRefreshAfterDuty = DService.Instance().Condition[ConditionFlag.BoundByDuty];
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;

        SetContentRouletteRoleBonusHook?.Dispose();
        SetContentRouletteRoleBonusHook = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            ModuleConfig.Save(this);

        ImGui.NewLine();

        using var table = ImRaii.Table("RouletteConfigTable", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn(GetLoc("Roulette"), ImGuiTableColumnFlags.NoHeaderLabel, 0.3f);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1082), ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1083), ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(2786), ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn(GetLoc("AutoNotifyRouletteBonus-OnlyIncomplete"), ImGuiTableColumnFlags.NoHeaderLabel, 0.1f);
        ImGui.TableSetupColumn(GetLoc("AutoNotifyRouletteBonus-CurrentBouns"), ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableHeadersRow();

        ImGui.TableSetColumnIndex(4);
        ImGui.Text(GetLoc("AutoNotifyRouletteBonus-OnlyIncomplete"));
        ImGuiOm.HelpMarker(GetLoc("AutoNotifyRouletteBonus-OnlyIncompleteHelp"));

        foreach (var roulette in CachedRoulettes)
        {
            var rowID = roulette.RowId;
            if (!ModuleConfig.Roulettes.TryGetValue(rowID, out var config))
            {
                config = new RouletteConfig();
                ModuleConfig.Roulettes[rowID] = config;
                ModuleConfig.Save(this);
            }

            var isEnabled = config.Tank || config.Healer || config.DPS;

            ImGui.TableNextRow();
            using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4(), !isEnabled))
            {
                ImGui.TableNextColumn();
                ImGui.Text(roulette.Name.ToString());

                for (var role = 0; role <= 2; role++)
                {
                    ImGui.TableNextColumn();
                    var check = role switch
                    {
                        0 => config.Tank,
                        1 => config.Healer,
                        2 => config.DPS
                    };

                    if (ImGui.Checkbox($"##Role_{rowID}_{role}", ref check))
                    {
                        switch (role)
                        {
                            case 0: config.Tank = check; break;
                            case 1: config.Healer = check; break;
                            case 2: config.DPS = check; break;
                        }
                        ModuleConfig.Save(this);
                    }
                }

                ImGui.TableNextColumn();
                if (ImGui.Checkbox($"##Incomplete_{rowID}", ref config.OnlyIncomplete))
                    ModuleConfig.Save(this);

                ImGui.TableNextColumn();
                if (LastKnownRoles is not null)
                {
                    var bonusIndex = roulette.ContentRouletteRoleBonus.RowId;
                    if (bonusIndex is > 0 and < ROULETTE_BONUS_ARRAY_SIZE)
                    {
                        var currentRole = LastKnownRoles[bonusIndex];
                        if (RoleDataMap.TryGetValue(currentRole, out var roleData))
                            ImGui.TextColored(roleData.ImGuiColor, roleData.Name);
                        else
                            ImGui.Text("-");
                    }
                    else
                    {
                        ImGui.Text("-");
                    }
                }
                else
                {
                    ImGui.Text("-");
                }
            }
        }
    }

    private static nint SetContentRouletteRoleBonusDetour(nint a1, nint a2, uint a3)
    {
        var result = SetContentRouletteRoleBonusHook.Original(a1, a2, a3);
        OnRoleBonusUpdated();
        return result;
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.BoundByDuty) return;
        if (value)
            PendingRefreshAfterDuty = true;
        else
            TryRefreshAfterDuty();
    }

    private static void TryRefreshAfterDuty()
    {
        if (!PendingRefreshAfterDuty) return;
        if (GameState.ContentFinderCondition != 0) return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null) return;
        
        PendingRefreshAfterDuty = false;
        OnRoleBonusUpdated();
    }

    private static void OnRoleBonusUpdated()
    {
        if (!GameState.IsLoggedIn) return;
        if (GameState.ContentFinderCondition != 0) return;
        var agent = AgentContentsFinder.Instance();
        if (agent is null) return;

        var currentRoles = agent->ContentRouletteRoleBonuses.ToArray();

        for (var index = 1; index < ROULETTE_BONUS_ARRAY_SIZE; index++)
        {
            var currentRole = currentRoles[index];
            var lastRole = LastKnownRoles[index];

            if (currentRole == lastRole) continue;

            LastKnownRoles[index] = currentRole;

            if (!RouletteIndexToRowID.TryGetValue((byte)index, out var rowId)) continue;
            if (!ModuleConfig.Roulettes.TryGetValue(rowId, out var config)) continue;

            var roleIndex = (int)currentRole;
            if (roleIndex > 2) continue;

            var shouldAlert = roleIndex switch
            {
                0 => config.Tank,
                1 => config.Healer,
                2 => config.DPS
            };

            if (!shouldAlert) continue;
            if (config.OnlyIncomplete && IsRouletteComplete(rowId)) continue;

            NotifyRoleBonus(rowId, currentRole);
        }
    }

    private static void NotifyRoleBonus(uint rowID, ContentsRouletteRole role)
    {
        if (!Throttler.Throttle($"AutoNotifyRouletteBonus-{rowID}-{role}", 60_000))
            return;

        if (!LuminaGetter.TryGetRow<ContentRoulette>(rowID, out var roulette))
            return;

        var rouletteName = roulette.Name.ToString();

        if (!RoleDataMap.TryGetValue(role, out var roleData))
            return;

        if (ModuleConfig.SendChat)
        {
            var message = new SeStringBuilder();
            message.Append(rouletteName).Append(" -> ");
            message.AddIcon(roleData.Icon);
            message.AddUiForeground(roleData.Name, roleData.UIColor);

            Chat(message.Build());
        }

        if (ModuleConfig.SendNotification)
            NotificationInfo($"{rouletteName} -> {roleData.Name}", GetLoc("AutoNotifyRouletteBonus"));

        if (ModuleConfig.SendTTS)
            Speak($"{rouletteName} {roleData.Name}");
    }

    private static bool IsRouletteComplete(uint rowID)
    {
        if (rowID > byte.MaxValue) return false;
        var instanceContent = InstanceContent.Instance();
        return instanceContent->IsRouletteComplete((byte)rowID);
    }

    private sealed record RoleData(
        BitmapFontIcon Icon,
        ushort UIColor,
        Vector4 ImGuiColor,
        string Name
    );

    private class Config : ModuleConfiguration
    {
        public bool SendChat;
        public bool SendNotification;
        public bool SendTTS;

        public Dictionary<uint, RouletteConfig> Roulettes = [];
    }

    private class RouletteConfig
    {
        public bool Tank;
        public bool Healer;
        public bool DPS;
        public bool OnlyIncomplete;
    }
}
