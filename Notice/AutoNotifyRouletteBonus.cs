using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using ContentRoulette = Lumina.Excel.Sheets.ContentRoulette;
using InstanceContent = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifyRouletteBonus : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyRouletteBonusTitle"),
        Description = GetLoc("AutoNotifyRouletteBonusDescription"),
        Category    = ModuleCategories.Notice,
        Author      = ["BoxingBunny"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private const int ROULETTE_BONUS_ARRAY_SIZE = 11;

    private static readonly FrozenDictionary<byte, uint> RouletteIndexToRowID = new Dictionary<byte, uint>
    {
        [1] = 1, [2] = 2, [3] = 3, [4] = 4, [5] = 5,
        [6] = 6, [7] = 8, [8] = 9, [9] = 15, [10] = 17
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<ContentsRouletteRole, BitmapFontIcon> RoleIcons = new Dictionary<ContentsRouletteRole, BitmapFontIcon>
    {
        [ContentsRouletteRole.Tank]   = BitmapFontIcon.Tank,
        [ContentsRouletteRole.Healer] = BitmapFontIcon.Healer,
        [ContentsRouletteRole.Dps]    = BitmapFontIcon.DPS
    }.ToFrozenDictionary();

    private static readonly ContentRoulette[] CachedRoulettes =
        LuminaGetter.Get<ContentRoulette>()
            .Where(x => x is { RowId: > 0, ContentRouletteRoleBonus.RowId: > 0 })
            .OrderBy(x => x.ContentRouletteRoleBonus.RowId)
            .ToArray();

    private static Config ModuleConfig = null!;
    private static ContentsRouletteRole[] LastKnownRoles = [];

    private static readonly Dictionary<uint, DalamudLinkPayload> RouletteLinkPayloads = [];
    private static readonly Dictionary<uint, byte> RouletteLinkPayloadIds = [];

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

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        foreach (var payload in RouletteLinkPayloads.Values)
            LinkPayloadManager.Instance().Unreg(payload.CommandId);
        RouletteLinkPayloads.Clear();
        RouletteLinkPayloadIds.Clear();
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

        ImGui.TableSetupColumn("##Roulette", ImGuiTableColumnFlags.None, 0.3f);
        ImGui.TableSetupColumn("##Tank", ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn("##Healer", ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn("##Dps", ImGuiTableColumnFlags.None, 0.1f);
        ImGui.TableSetupColumn("##OnlyIncomplete", ImGuiTableColumnFlags.NoHeaderLabel, 0.1f);
        ImGui.TableSetupColumn("##RoleBonus", ImGuiTableColumnFlags.NoHeaderLabel, 0.1f);

        var hasRoleBonusTexture = TryGetTextureInfo("ui/uld/ContentsFinder_hr1.tex", out var roleBonusTexture, out var roleBonusInvTexSize);
        var hasHeaderTexture = TryGetTextureInfo("ui/uld/Journal_Detail_hr1.tex", out var headerTexture, out var headerInvTexSize);
        
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(8605));

        ImGui.TableNextColumn();
        if (DService.Instance().Texture.TryGetFromGameIcon(new(62581), out var tankIcon))
            ImGui.Image(tankIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
        else
            ImGui.TextUnformatted("-");

        ImGui.TableNextColumn();
        if (DService.Instance().Texture.TryGetFromGameIcon(new(62582), out var healerIcon))
            ImGui.Image(healerIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
        else
            ImGui.TextUnformatted("-");

        ImGui.TableNextColumn();
        if (DService.Instance().Texture.TryGetFromGameIcon(new(62583), out var dpsIcon))
            ImGui.Image(dpsIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
        else
            ImGui.TextUnformatted("-");
        
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLoc("AutoNotifyRouletteBonus-OnlyIncomplete"));
        ImGuiOm.HelpMarker(GetLoc("AutoNotifyRouletteBonus-OnlyIncompleteHelp"));

        ImGui.TableNextColumn();
        DrawRoleBonusHeaderIcon(headerTexture, headerInvTexSize, hasHeaderTexture);

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
                    if (!DrawRoleBonusCellIcon(roleBonusTexture, roleBonusInvTexSize, hasRoleBonusTexture, currentRole))
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
                ContentsRouletteRole.Tank   => config.Tank,
                ContentsRouletteRole.Healer => config.Healer,
                ContentsRouletteRole.Dps    => config.DPS,
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

        if (!RoleIcons.TryGetValue(role, out var roleIcon))
            return;

        if (ModuleConfig.SendChat)
        {
            var message = new SeStringBuilder();
            var roleBonusText = GetRoleBonusText(role);
            var rouletteLinkPayload = GetRouletteLinkPayload(rowID);

            message.AddText($"{roleBonusText} ")
                   .AddIcon(roleIcon)
                   .Add(NewLinePayload.Payload)
                   .AddText($"{GetLoc("Operation")}: ")
                   .Add(RawPayload.LinkTerminator)
                   .Add(rouletteLinkPayload)
                   .AddText("[")
                   .AddUiForeground(rouletteName, 35)
                   .AddText("]")
                   .Add(RawPayload.LinkTerminator);

            Chat(message.Build());
        }

        var roleName = GetRoleName(role);
        if (ModuleConfig.SendNotification)
            NotificationInfo($"{rouletteName} -> {roleName}", GetLoc("AutoNotifyRouletteBonusTitle"));

        if (ModuleConfig.SendTTS)
            Speak($"{rouletteName} {roleName}");
    }

    private static bool IsRouletteComplete(uint rowID)
    {
        if (rowID > byte.MaxValue) return false;
        var instanceContent = InstanceContent.Instance();
        return instanceContent->IsRouletteComplete((byte)rowID);
    }
    
    private static string GetRoleBonusText(ContentsRouletteRole role) =>
        LuminaWrapper.GetAddonText(role switch
        {
            ContentsRouletteRole.Tank   => 10997,
            ContentsRouletteRole.Healer => 10998,
            ContentsRouletteRole.Dps    => 10999
        });

    private static string GetRoleName(ContentsRouletteRole role) =>
        LuminaWrapper.GetAddonText(role switch
        {
            ContentsRouletteRole.Tank   => 1082,
            ContentsRouletteRole.Healer => 1083,
            ContentsRouletteRole.Dps    => 2786
        });

    private static DalamudLinkPayload GetRouletteLinkPayload(uint rowID)
    {
        if (RouletteLinkPayloads.TryGetValue(rowID, out var payload)) return payload;

        var linkPayload = LinkPayloadManager.Instance().Reg(OnClickRouletteLinkPayload, out var id);
        RouletteLinkPayloads[rowID] = linkPayload;
        RouletteLinkPayloadIds[id] = (byte)rowID;
        return linkPayload;
    }

    private static void OnClickRouletteLinkPayload(uint id, SeString _)
    {
        if (!RouletteLinkPayloadIds.TryGetValue(id, out var rouletteRowID)) return;
        if (GameState.ContentFinderCondition != 0) return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null) return;

        agent->OpenRouletteDuty(rouletteRowID);
        
    }

    private static void DrawRoleBonusHeaderIcon(IDalamudTextureWrap texture, Vector2 invTexSize, bool hasTexture)
    {
        if (!hasTexture)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        DrawIcon(texture, invTexSize, new Vector2(888f, 0f), new Vector2(56f, 56f));
    }
    
    private static bool DrawRoleBonusCellIcon(IDalamudTextureWrap texture, Vector2 invTexSize, bool hasTexture, ContentsRouletteRole role)
    {
        if (!hasTexture) return false;
        if ((byte)role > 2) return false;

        DrawIcon(texture, invTexSize, new Vector2(40f * (byte)role, 216f), new Vector2(40f, 40f));
        return true;
    }

    private static void DrawIcon(IDalamudTextureWrap texture, Vector2 invTexSize, Vector2 iconPosPx, Vector2 sizePx)
    {
        var uv0 = iconPosPx * invTexSize;
        var uv1 = (iconPosPx + sizePx) * invTexSize;
        ImGui.Image(texture.Handle, new(ImGui.GetTextLineHeightWithSpacing()), uv0, uv1);
    }

    private static bool TryGetTextureInfo(string path, out IDalamudTextureWrap texture, out Vector2 invTexSize)
    {
        texture = DService.Instance().Texture.GetFromGame(path).GetWrapOrEmpty();
        var hasTexture = texture is { Width: > 0, Height: > 0 };
        invTexSize = hasTexture ? new Vector2(1f / texture.Width, 1f / texture.Height) : default;
        return hasTexture;
    }

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
