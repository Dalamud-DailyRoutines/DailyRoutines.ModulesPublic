using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using ContentRoulette = Lumina.Excel.Sheets.ContentRoulette;
using InstanceContent = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifyRouletteBonus : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyRouletteBonusTitle"),
        Description = Lang.Get("AutoNotifyRouletteBonusDescription"),
        Category    = ModuleCategory.Notice,
        Author      = ["BoxingBunny"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig SetContentRouletteRoleBonusSig = new("48 89 4C 24 ?? 55 41 56 48 83 EC ?? ?? ?? ?? 4C 8B F1");
    private delegate        void    SetContentRouletteRoleBonusDelegate(AgentContentsFinder* instance, void* data, uint bonusIndex);
    private                 Hook<SetContentRouletteRoleBonusDelegate>? SetContentRouletteRoleBonusHook;
    
    private Config config = null!;

    private ContentsRouletteRole[] lastKnownRoles = [];

    private readonly Dictionary<uint, DalamudLinkPayload> rouletteLinkPayloads   = [];
    private readonly Dictionary<uint, byte>               rouletteLinkPayloadIDs = [];

    protected override void Init()
    {
        config =   Config.Load(this) ?? new();
        TaskHelper   ??= new();

        if (lastKnownRoles.Length != ROULETTE_BONUS_ARRAY_SIZE)
        {
            lastKnownRoles = new ContentsRouletteRole[ROULETTE_BONUS_ARRAY_SIZE];
            Array.Fill(lastKnownRoles, ContentsRouletteRole.None);
        }

        SetContentRouletteRoleBonusHook ??= SetContentRouletteRoleBonusSig.GetHook<SetContentRouletteRoleBonusDelegate>(SetContentRouletteRoleBonusDetour);
        SetContentRouletteRoleBonusHook.Enable();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        foreach (var payload in rouletteLinkPayloads.Values)
            LinkPayloadManager.Instance().Unreg(payload.CommandId);
        rouletteLinkPayloads.Clear();
        rouletteLinkPayloadIDs.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
            config.Save(this);

        ImGui.NewLine();

        using var table = ImRaii.Table
        (
            "RouletteConfigTable",
            6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp
        );
        if (!table) return;

        ImGui.TableSetupColumn("##Roulette",       ImGuiTableColumnFlags.None,          0.3f);
        ImGui.TableSetupColumn("##Tank",           ImGuiTableColumnFlags.None,          0.1f);
        ImGui.TableSetupColumn("##Healer",         ImGuiTableColumnFlags.None,          0.1f);
        ImGui.TableSetupColumn("##Dps",            ImGuiTableColumnFlags.None,          0.1f);
        ImGui.TableSetupColumn("##OnlyIncomplete", ImGuiTableColumnFlags.NoHeaderLabel, 0.1f);
        ImGui.TableSetupColumn("##RoleBonus",      ImGuiTableColumnFlags.NoHeaderLabel, 0.1f);

        GetTextureInfo("ui/uld/ContentsFinder_hr1.tex", out var roleBonusTexture, out var roleBonusInvTexSize);
        GetTextureInfo("ui/uld/Journal_Detail_hr1.tex", out var headerTexture,    out var headerInvTexSize);

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
        ImGui.TextUnformatted(Lang.Get("OnlyIncomplete"));
        ImGuiOm.HelpMarker(Lang.Get("AutoNotifyRouletteBonus-OnlyIncompleteHelp"));

        ImGui.TableNextColumn();
        DrawRoleBonusHeaderIcon(headerTexture, headerInvTexSize);

        foreach (var roulette in CachedRoulettes)
        {
            var rowID = roulette.RowId;

            if (!this.config.Roulettes.TryGetValue(rowID, out var rouletteConfig))
            {
                rouletteConfig                       = new RouletteConfig();
                this.config.Roulettes[rowID] = rouletteConfig;
                this.config.Save(this);
            }

            var isEnabled = rouletteConfig.Tank || rouletteConfig.Healer || rouletteConfig.DPS;

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
                        0 => rouletteConfig.Tank,
                        1 => rouletteConfig.Healer,
                        2 => rouletteConfig.DPS
                    };

                    if (ImGui.Checkbox($"##Role_{rowID}_{role}", ref check))
                    {
                        switch (role)
                        {
                            case 0: rouletteConfig.Tank   = check; break;
                            case 1: rouletteConfig.Healer = check; break;
                            case 2: rouletteConfig.DPS    = check; break;
                        }

                        this.config.Save(this);
                    }
                }

                ImGui.TableNextColumn();
                if (ImGui.Checkbox($"##Incomplete_{rowID}", ref rouletteConfig.OnlyIncomplete))
                    this.config.Save(this);

                ImGui.TableNextColumn();
                var bonusIndex = roulette.ContentRouletteRoleBonus.RowId;

                if (bonusIndex is > 0 and < ROULETTE_BONUS_ARRAY_SIZE)
                {
                    var currentRole = lastKnownRoles[bonusIndex];
                    DrawRoleBonusCellIcon(roleBonusTexture, roleBonusInvTexSize, currentRole);
                }
                else
                    ImGui.TextUnformatted("-");
            }
        }
    }

    private void SetContentRouletteRoleBonusDetour(AgentContentsFinder* instance, void* data, uint bonusIndex)
    {
        SetContentRouletteRoleBonusHook.Original(instance, data, bonusIndex);
        OnRoleBonusUpdated();
    }

    private void OnZoneChanged(uint u)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(() => UIModule.IsScreenReady() && !DService.Instance().Condition.IsBetweenAreas);
        TaskHelper.Enqueue
        (() =>
            {
                var agent = AgentContentsFinder.Instance();
                if (agent is null) return false;

                agent->Refresh();
                return true;
            }
        );
    }

    private void OnClickRouletteLinkPayload(uint id, SeString _)
    {
        if (!rouletteLinkPayloadIDs.TryGetValue(id, out var rouletteRowID)) return;
        if (GameState.ContentFinderCondition != 0) return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null) return;

        agent->OpenRouletteDuty(rouletteRowID);
    }

    private void OnRoleBonusUpdated()
    {
        if (!GameState.IsLoggedIn) return;
        if (GameState.ContentFinderCondition != 0) return;
        var agent = AgentContentsFinder.Instance();
        if (agent is null) return;

        var currentRoles = agent->ContentRouletteRoleBonuses.ToArray();
        var bonuses      = new List<(uint RowID, ContentsRouletteRole Role)>();

        for (byte index = 1; index < ROULETTE_BONUS_ARRAY_SIZE; index++)
        {
            var currentRole = currentRoles[index];
            var lastRole    = lastKnownRoles[index];

            if (currentRole == lastRole) continue;

            lastKnownRoles[index] = currentRole;
            if (lastRole == ContentsRouletteRole.None) continue;
            if (!RouletteIndexToRowID.TryGetValue(index, out var rowID)) continue;
            if (!this.config.Roulettes.TryGetValue(rowID, out var rouletteConfig)) continue;
            if (currentRole > ContentsRouletteRole.Dps) continue;

            var shouldAlert = currentRole switch
            {
                ContentsRouletteRole.Tank   => rouletteConfig.Tank,
                ContentsRouletteRole.Healer => rouletteConfig.Healer,
                ContentsRouletteRole.Dps    => rouletteConfig.DPS
            };

            if (!shouldAlert) continue;
            if (rouletteConfig.OnlyIncomplete && IsRouletteComplete(rowID)) continue;

            bonuses.Add((rowID, currentRole));
        }

        NotifyRoleBonuses(bonuses);
    }

    private void NotifyRoleBonuses(List<(uint RowID, ContentsRouletteRole Role)> bonuses)
    {
        if (bonuses.Count == 0) return;

        Dictionary<ContentsRouletteRole, List<(uint RowID, string RouletteName)>> groupedBonuses = [];

        foreach (var bonus in bonuses)
        {
            if (!LuminaGetter.TryGetRow<ContentRoulette>(bonus.RowID, out var roulette))
                continue;
            if (bonus.Role > ContentsRouletteRole.Dps) continue;

            if (!groupedBonuses.TryGetValue(bonus.Role, out var grouped))
            {
                grouped                    = [];
                groupedBonuses[bonus.Role] = grouped;
            }

            if (grouped.Any(x => x.RowID == bonus.RowID)) continue;
            grouped.Add((bonus.RowID, roulette.Name.ToString()));
        }

        if (groupedBonuses.Count == 0) return;

        if (config.SendChat)
        {
            var chatBuilder = new SeStringBuilder();
            chatBuilder.AddText(Lang.Get("AutoNotifyRouletteBonusTitle"));

            foreach (var (role, grouped) in groupedBonuses)
            {
                chatBuilder.Add(NewLinePayload.Payload);
                chatBuilder.AddText(GetRoleBonusText(role));
                if (RoleIcons.TryGetValue(role, out var roleIcon))
                    chatBuilder.AddText(" ").AddIcon(roleIcon);

                chatBuilder.Add(NewLinePayload.Payload)
                           .AddText($"{Lang.Get("Operation")}: ");

                foreach (var (rowID, rouletteName) in grouped)
                {
                    chatBuilder.Add(RawPayload.LinkTerminator)
                               .Add(GetRouletteLinkPayload(rowID))
                               .AddText("[")
                               .AddUiForeground(rouletteName, 35)
                               .AddText("]")
                               .Add(RawPayload.LinkTerminator);
                }
            }

            NotifyHelper.Instance().Chat(chatBuilder.Build());
        }

        var notificationLines = new List<string>();
        foreach (var (role, grouped) in groupedBonuses)
            notificationLines.Add($"{GetRoleBonusText(role)} {string.Concat(grouped.Select(x => $"[{x.RouletteName}]"))}");

        if (notificationLines.Count == 0) return;

        var notificationString = string.Join('\n', notificationLines);

        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(notificationString, Lang.Get("AutoNotifyRouletteBonusTitle"));

        if (config.SendTTS)
            NotifyHelper.Speak(notificationString);
    }

    private static bool IsRouletteComplete(uint rowID)
    {
        if (rowID > byte.MaxValue) return false;
        var instanceContent = InstanceContent.Instance();
        return instanceContent->IsRouletteComplete((byte)rowID);
    }

    private static string GetRoleBonusText(ContentsRouletteRole role) =>
        LuminaWrapper.GetAddonText
        (
            role switch
            {
                ContentsRouletteRole.Tank   => 10997,
                ContentsRouletteRole.Healer => 10998,
                ContentsRouletteRole.Dps    => 10999
            }
        );

    private DalamudLinkPayload GetRouletteLinkPayload(uint rowID)
    {
        if (rouletteLinkPayloads.TryGetValue(rowID, out var payload)) return payload;

        var linkPayload = LinkPayloadManager.Instance().Reg(OnClickRouletteLinkPayload, out var id);
        rouletteLinkPayloads[rowID] = linkPayload;
        rouletteLinkPayloadIDs[id]  = (byte)rowID;
        return linkPayload;
    }

    private static void DrawRoleBonusHeaderIcon(IDalamudTextureWrap? texture, Vector2 invTexSize)
    {
        if (texture == null)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        DrawIcon(texture, invTexSize, new(888f, 0f), new(56f, 56f));
    }

    private static void DrawRoleBonusCellIcon(IDalamudTextureWrap? texture, Vector2 invTexSize, ContentsRouletteRole role)
    {
        if (texture == null || (byte)role > 2)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        DrawIcon(texture, invTexSize, new(40f * (byte)role, 216f), new(40f, 40f));
    }

    private static void DrawIcon(IDalamudTextureWrap texture, Vector2 invTexSize, Vector2 iconPosPx, Vector2 sizePx)
    {
        var uv0 = iconPosPx            * invTexSize;
        var uv1 = (iconPosPx + sizePx) * invTexSize;
        ImGui.Image(texture.Handle, new(ImGui.GetTextLineHeightWithSpacing()), uv0, uv1);
    }

    private static void GetTextureInfo(string path, out IDalamudTextureWrap? texture, out Vector2 invTexSize)
    {
        texture = DService.Instance().Texture.GetFromGame(path).GetWrapOrDefault();

        if (texture == null)
        {
            invTexSize = default;
            return;
        }

        invTexSize = new(1f / texture.Width, 1f / texture.Height);
    }
    
    private class Config : ModuleConfig
    {
        public Dictionary<uint, RouletteConfig> Roulettes = [];
        public bool                             SendChat;
        public bool                             SendNotification;
        public bool                             SendTTS;
    }

    private class RouletteConfig
    {
        public bool DPS;
        public bool Healer;
        public bool OnlyIncomplete;
        public bool Tank;
    }
    
    #region 常量

    private const int ROULETTE_BONUS_ARRAY_SIZE = 11;
    
    private static readonly FrozenDictionary<byte, uint> RouletteIndexToRowID = new Dictionary<byte, uint>
    {
        [1] = 1, [2] = 2, [3] = 3, [4] = 4, [5]   = 5,
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

    #endregion
}
