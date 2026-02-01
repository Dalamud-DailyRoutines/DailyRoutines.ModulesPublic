using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using ContentRoulette = Lumina.Excel.Sheets.ContentRoulette;
using InstanceContent = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifyRouletteBonus : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoNotifyRouletteBonusTitle"),
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
        [ContentsRouletteRole.Tank] = new(BitmapFontIcon.Tank, 37, LuminaWrapper.GetAddonText(1082)),
        [ContentsRouletteRole.Healer] = new(BitmapFontIcon.Healer, 504, LuminaWrapper.GetAddonText(1083)),
        [ContentsRouletteRole.Dps] = new(BitmapFontIcon.DPS, 545, LuminaWrapper.GetAddonText(2786))
    }.ToFrozenDictionary();

    private static readonly ContentRoulette[] CachedRoulettes =
        LuminaGetter.Get<ContentRoulette>()
            .Where(x => x is { RowId: > 0, ContentRouletteRoleBonus.RowId: > 0 })
            .OrderBy(x => x.ContentRouletteRoleBonus.RowId)
            .ToArray();

    private static Config ModuleConfig = null!;
    private static ContentsRouletteRole[] LastKnownRoles = [];
    private static readonly CompSig SetContentRouletteRoleBonusSig = new("48 89 4C 24 ?? 55 41 56 48 83 EC ?? ?? ?? ?? 4C 8B F1");
    private delegate void SetContentRouletteRoleBonusDelegate(AgentContentsFinder* instance, void* data, uint bonusIndex);
    private static Hook<SetContentRouletteRoleBonusDelegate>? SetContentRouletteRoleBonusHook;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        if (LastKnownRoles.Length != ROULETTE_BONUS_ARRAY_SIZE)
        {
            LastKnownRoles = new ContentsRouletteRole[ROULETTE_BONUS_ARRAY_SIZE];
            Array.Fill(LastKnownRoles, ContentsRouletteRole.None);
        }

        SetContentRouletteRoleBonusHook ??= SetContentRouletteRoleBonusSig.GetHook<SetContentRouletteRoleBonusDelegate>(SetContentRouletteRoleBonusDetour);
        SetContentRouletteRoleBonusHook.Enable();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit() =>
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

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

        ImGui.TableSetupColumn("##Roulette", ImGuiTableColumnFlags.None, 0.3f);
        ImGui.TableSetupColumn("##Tank", ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn("##Healer", ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn("##Dps", ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn("##OnlyIncomplete", ImGuiTableColumnFlags.NoHeaderLabel, 0.1f);
        ImGui.TableSetupColumn("##RoleBonus", ImGuiTableColumnFlags.NoHeaderLabel, 0.1f);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        var headerTexts = new[]
        {
            LuminaWrapper.GetAddonText(8605),
            LuminaWrapper.GetAddonText(1082),
            LuminaWrapper.GetAddonText(1083),
            LuminaWrapper.GetAddonText(2786),
            GetLoc("AutoNotifyRouletteBonus-OnlyIncomplete")
        };

        for (var i = 0; i < headerTexts.Length; i++)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(headerTexts[i]);
            if (i == 4)
                ImGuiOm.HelpMarker(GetLoc("AutoNotifyRouletteBonus-OnlyIncompleteHelp"));
        }
        
        var roleBonusTexture = DService.Instance().Texture.GetFromGame("ui/uld/ContentsFinder_hr1.tex").GetWrapOrEmpty();
        var hasRoleBonusTexture = roleBonusTexture is { Width: > 0, Height: > 0 };
        var invTexSize = hasRoleBonusTexture
                             ? new Vector2(1f / roleBonusTexture.Width, 1f / roleBonusTexture.Height)
                             : default;

        var headerTexture = DService.Instance().Texture.GetFromGame("ui/uld/Journal_Detail_hr1.tex").GetWrapOrEmpty();
        var hasHeaderTexture = headerTexture is { Width: > 0, Height: > 0 };
        
        ImGui.TableNextColumn();
        DrawRoleBonusHeaderIcon(headerTexture, hasHeaderTexture);

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
                ImGui.TextUnformatted(roulette.Name.ToString());

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
                var bonusIndex = roulette.ContentRouletteRoleBonus.RowId;
                if (bonusIndex is > 0 and < ROULETTE_BONUS_ARRAY_SIZE)
                {
                    var currentRole = LastKnownRoles[bonusIndex];
                    if (!DrawRoleBonusCellIcon(roleBonusTexture, invTexSize, hasRoleBonusTexture, currentRole))
                        ImGui.TextUnformatted("-");
                }
                else
                {
                    ImGui.TextUnformatted("-");
                }
            }
        }
    }

    private static void SetContentRouletteRoleBonusDetour(AgentContentsFinder* instance, void* data, uint bonusIndex)
    {
        SetContentRouletteRoleBonusHook.Original(instance, data, bonusIndex);
        OnRoleBonusUpdated();
    }

    private static void OnZoneChanged(ushort zone) => 
        OnRoleBonusUpdated();

    private static void OnRoleBonusUpdated()
    {
        if (!GameState.IsLoggedIn) return;
        if (GameState.ContentFinderCondition != 0) return;
        var agent = AgentContentsFinder.Instance();
        if (agent is null) return;

        var currentRoles = agent->ContentRouletteRoleBonuses.ToArray();

        for (byte index = 1; index < ROULETTE_BONUS_ARRAY_SIZE; index++)
        {
            var currentRole = currentRoles[index];
            var lastRole = LastKnownRoles[index];

            if (currentRole == lastRole) continue;

            LastKnownRoles[index] = currentRole;

            if (!RouletteIndexToRowID.TryGetValue(index, out var rowId)) continue;
            if (!ModuleConfig.Roulettes.TryGetValue(rowId, out var config)) continue;

            if (currentRole > ContentsRouletteRole.Dps) continue;

            var shouldAlert = currentRole switch
            {
                ContentsRouletteRole.Tank => config.Tank,
                ContentsRouletteRole.Healer => config.Healer,
                ContentsRouletteRole.Dps => config.DPS,
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
            NotificationInfo($"{rouletteName} -> {roleData.Name}", GetLoc("AutoNotifyRouletteBonusTitle"));

        if (ModuleConfig.SendTTS)
            Speak($"{rouletteName} {roleData.Name}");
    }

    private static bool IsRouletteComplete(uint rowID)
    {
        if (rowID > byte.MaxValue) return false;
        var instanceContent = InstanceContent.Instance();
        return instanceContent->IsRouletteComplete((byte)rowID);
    }

    private static void DrawRoleBonusHeaderIcon(IDalamudTextureWrap texture, bool hasTexture)
    {
        if (!hasTexture)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        var invTexSize = new Vector2(1f / texture.Width, 1f / texture.Height);
        var iconPosPx = new Vector2(888f, 0f);
        var uv0 = iconPosPx * invTexSize;
        var uv1 = (iconPosPx + new Vector2(56f, 56f)) * invTexSize;
        ImGui.Image(texture.Handle, ScaledVector2(15f), uv0, uv1);
    }
    
    private static bool DrawRoleBonusCellIcon(IDalamudTextureWrap texture, Vector2 invTexSize, bool hasTexture, ContentsRouletteRole role)
    {
        if (!hasTexture) return false;
        if ((byte)role > 2) return false;

        DrawRoleBonusIcon(texture, invTexSize, (byte)role);
        return true;
    }

    private static void DrawRoleBonusIcon(IDalamudTextureWrap texture, Vector2 invTexSize, byte role)
    {
        var iconPosPx = new Vector2(40f * role, 216f);
        var uv0 = iconPosPx * invTexSize;
        var uv1 = (iconPosPx + new Vector2(40f, 40f)) * invTexSize;

        ImGui.Image(texture.Handle, ScaledVector2(15f), uv0, uv1);
    }

    private sealed record RoleData(
        BitmapFontIcon Icon,
        ushort UIColor,
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
