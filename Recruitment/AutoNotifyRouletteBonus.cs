using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
        Category    = ModuleCategory.Recruitment,
        Author      = ["BoxingBunny"]
    };

    private static readonly CompSig SetContentRouletteRoleBonusSig =
        new("48 89 4C 24 ?? 55 41 56 48 83 EC ?? ?? ?? ?? 4C 8B F1");
    private delegate void SetContentRouletteRoleBonusDelegate
    (
        AgentContentsFinder* instance,
        byte*                roleIDs,
        uint                 roleCount
    );
    private Hook<SetContentRouletteRoleBonusDelegate>? SetContentRouletteRoleBonusHook;

    private static readonly CompSig UpdateRouletteCompletionSig = new("0F B6 11 4C 8D 05 ?? ?? ?? ?? 8B 41");
    private delegate uint UpdateRouletteCompletionDelegate
    (
        void* packet
    );
    private Hook<UpdateRouletteCompletionDelegate>? UpdateRouletteCompletionHook;

    private Config config = null!;

    private readonly ContentsRouletteRole[]                 currentRoles  = new ContentsRouletteRole[ROULETTE_BONUS_ARRAY_SIZE];
    private readonly Dictionary<uint, ContentsRouletteRole> notifiedRoles = [];

    private bool hasRoleData;

    private readonly Dictionary<uint, DalamudLinkPayload> rouletteLinkPayloads   = [];
    private readonly Dictionary<uint, byte>               rouletteLinkPayloadIDs = [];

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        TaskHelper ??= new()
        {
            RetryIntervalMS = 250,
            TimeoutMS       = 0
        };

        Array.Fill(currentRoles, ContentsRouletteRole.None);
        notifiedRoles.Clear();
        hasRoleData = false;

        var configChanged = false;

        foreach (var roulette in CachedRoulettes)
        {
            configChanged |= config.Roulettes.TryAdd(roulette.RowId, new());
        }

        if (configChanged)
            config.Save(this);

        SetContentRouletteRoleBonusHook ??= SetContentRouletteRoleBonusSig.GetHook<SetContentRouletteRoleBonusDelegate>(SetContentRouletteRoleBonusDetour);
        SetContentRouletteRoleBonusHook.Enable();

        UpdateRouletteCompletionHook ??= UpdateRouletteCompletionSig.GetHook<UpdateRouletteCompletionDelegate>(UpdateRouletteCompletionDetour);
        UpdateRouletteCompletionHook.Enable();

        IClientState.Instance().Login            += QueueEvaluation;
        IClientState.Instance().Logout           += OnLogout;
        IClientState.Instance().TerritoryChanged += OnZoneChanged;

        if (GameState.IsLoggedIn)
            QueueEvaluation();
    }

    protected override void Uninit()
    {
        IClientState.Instance().Login            -= QueueEvaluation;
        IClientState.Instance().Logout           -= OnLogout;
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;

        TaskHelper.Abort();
        hasRoleData = false;
        Array.Fill(currentRoles, ContentsRouletteRole.None);
        notifiedRoles.Clear();

        foreach (var payload in rouletteLinkPayloads.Values)
            LinkPayloadManager.Instance().Unreg(payload.CommandId);

        rouletteLinkPayloads.Clear();
        rouletteLinkPayloadIDs.Clear();
    }

    #region 界面

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table
        (
            "RouletteConfigTable",
            6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp
        );
        if (!table)
            return;

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
        if (ITextureProvider.Instance().TryGetFromGameIcon(new(62581), out var tankIcon))
            ImGui.Image(tankIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
        else
            ImGui.TextUnformatted("-");

        ImGui.TableNextColumn();
        if (ITextureProvider.Instance().TryGetFromGameIcon(new(62582), out var healerIcon))
            ImGui.Image(healerIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
        else
            ImGui.TextUnformatted("-");

        ImGui.TableNextColumn();
        if (ITextureProvider.Instance().TryGetFromGameIcon(new(62583), out var dpsIcon))
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

            var rouletteConfig = config.Roulettes[rowID];

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
                            case 0:
                                rouletteConfig.Tank = check;
                                break;
                            case 1:
                                rouletteConfig.Healer = check;
                                break;
                            case 2:
                                rouletteConfig.DPS = check;
                                break;
                        }

                        config.Save(this);
                        QueueEvaluation();
                    }
                }

                ImGui.TableNextColumn();

                if (ImGui.Checkbox($"##Incomplete_{rowID}", ref rouletteConfig.OnlyIncomplete))
                {
                    config.Save(this);
                    QueueEvaluation();
                }

                ImGui.TableNextColumn();
                var bonusIndex = roulette.ContentRouletteRoleBonus.RowId;

                if (hasRoleData &&
                    bonusIndex < currentRoles.Length)
                {
                    var currentRole = currentRoles[bonusIndex];
                    DrawRoleBonusCellIcon(roleBonusTexture, roleBonusInvTexSize, currentRole);
                }
                else
                    ImGui.TextUnformatted("-");
            }
        }
    }

    private static void DrawRoleBonusHeaderIcon
    (
        IDalamudTextureWrap? texture,
        Vector2              invTexSize
    )
    {
        if (texture == null)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        DrawIcon(texture, invTexSize, new(888f, 0f), new(56f, 56f));
    }

    private static void DrawRoleBonusCellIcon
    (
        IDalamudTextureWrap? texture,
        Vector2              invTexSize,
        ContentsRouletteRole role
    )
    {
        if (texture    == null ||
            (byte)role > 2)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        DrawIcon(texture, invTexSize, new(40f * (byte)role, 216f), new(40f, 40f));
    }

    private static void DrawIcon
    (
        IDalamudTextureWrap texture,
        Vector2             invTexSize,
        Vector2             iconPosPx,
        Vector2             sizePx
    )
    {
        var uv0 = iconPosPx            * invTexSize;
        var uv1 = (iconPosPx + sizePx) * invTexSize;
        ImGui.Image(texture.Handle, new(ImGui.GetTextLineHeightWithSpacing()), uv0, uv1);
    }

    #endregion

    #region 事件

    private void SetContentRouletteRoleBonusDetour
    (
        AgentContentsFinder* instance,
        byte*                roleIDs,
        uint                 roleCount
    )
    {
        SetContentRouletteRoleBonusHook.Original(instance, roleIDs, roleCount);

        var roles = instance->ContentRouletteRoleBonuses;
        var count = (int)Math.Min(roleCount, (uint)roles.Length);
        if (count == 0)
            return;

        roles[..count].CopyTo(currentRoles);
        hasRoleData = true;
        QueueEvaluation();
    }

    private uint UpdateRouletteCompletionDetour
    (
        void* packet
    )
    {
        var result = UpdateRouletteCompletionHook.Original(packet);
        QueueEvaluation();
        return result;
    }

    private void OnLogout
    (
        int type,
        int code
    )
    {
        TaskHelper.Abort();
        hasRoleData = false;
        Array.Fill(currentRoles, ContentsRouletteRole.None);
        notifiedRoles.Clear();
    }

    private void OnZoneChanged
    (
        uint territory
    ) => QueueEvaluation();

    private void QueueEvaluation()
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(EvaluateBonuses);
    }

    private void OnClickRouletteLinkPayload
    (
        uint     id,
        SeString _
    )
    {
        if (!rouletteLinkPayloadIDs.TryGetValue(id, out var rouletteRowID))
            return;
        if (GameState.ContentFinderCondition != 0)
            return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null)
            return;

        agent->OpenRouletteDuty(rouletteRowID);
    }

    private bool EvaluateBonuses()
    {
        if (!GameState.IsLoggedIn             ||
            !PlayerState.Instance()->IsLoaded ||
            !UIModule.IsScreenReady()         ||
            ICondition.Instance().IsBetweenAreas)
            return false;

        if (!hasRoleData)
        {
            var agent = AgentContentsFinder.Instance();
            if (agent == null)
                return false;

            agent->ContentRouletteRoleBonuses.CopyTo(currentRoles);
            hasRoleData = true;
        }

        var instanceContent = InstanceContent.Instance();
        var bonuses         = new List<(uint RowID, ContentsRouletteRole Role)>();

        foreach (var roulette in CachedRoulettes)
        {
            var rowID          = roulette.RowId;
            var bonusIndex     = roulette.ContentRouletteRoleBonus.RowId;
            var currentRole    = currentRoles[bonusIndex];
            var rouletteConfig = config.Roulettes[rowID];
            var shouldAlert = currentRole switch
            {
                ContentsRouletteRole.Tank   => rouletteConfig.Tank,
                ContentsRouletteRole.Healer => rouletteConfig.Healer,
                ContentsRouletteRole.Dps    => rouletteConfig.DPS,
                _                           => false
            };

            if (!shouldAlert ||
                (rouletteConfig.OnlyIncomplete && !instanceContent->IsRouletteIncomplete((byte)rowID)))
            {
                notifiedRoles.Remove(rowID);
                continue;
            }

            if (!notifiedRoles.TryGetValue(rowID, out var notifiedRole) ||
                notifiedRole != currentRole)
                bonuses.Add((rowID, currentRole));
        }

        if (GameState.ContentFinderCondition != 0)
            return true;

        bonuses.Sort
        ((left, right) =>
            {
                var roleOrder = left.Role.CompareTo(right.Role);
                return roleOrder != 0 ?
                           roleOrder :
                           left.RowID.CompareTo(right.RowID);
            }
        );
        NotifyRoleBonuses(bonuses);

        foreach (var (rowID, role) in bonuses)
            notifiedRoles[rowID] = role;

        return true;
    }

    #endregion

    private void NotifyRoleBonuses
    (
        List<(uint RowID, ContentsRouletteRole Role)> bonuses
    )
    {
        if (bonuses.Count == 0)
            return;

        using var rented = new RentedSeStringBuilder();
        rented.Append
        (
            Lang.Get
            (
                "AutoNotifyRouletteBonus-Notification-ChatTitle",
                new Dictionary<string, object>
                {
                    ["count"] = bonuses.Count
                }
            )
        );

        foreach (var (rowID, role) in bonuses)
        {
            rented.AppendNewLine();

            rented.AppendDalamudSeString(GetRouletteLinkPayload(rowID))
                  .Append("  \ue06f ")
                  .Append(LuminaWrapper.GetContentRouletteName(rowID))
                  .Append("（")
                  .AppendIcon(role.GetIcon())
                  .Append(role.GetName())
                  .Append("）")
                  .PopLink();
        }

        NotifyHelper.Instance().Chat(rented.ToReadOnlySeString());

        NotifyHelper.Instance().TrayInfo
        (
            Lang.Get
            (
                "AutoNotifyRouletteBonus-Notification-TrayContent",
                new Dictionary<string, object>
                {
                    ["count"] = bonuses.Count
                }
            ),
            Lang.Get("AutoNotifyRouletteBonus-Notification-TrayTitle")
        );
    }

    private DalamudLinkPayload GetRouletteLinkPayload
    (
        uint rowID
    )
    {
        if (rouletteLinkPayloads.TryGetValue(rowID, out var payload))
            return payload;

        var linkPayload = LinkPayloadManager.Instance().Reg(OnClickRouletteLinkPayload, out var id);
        rouletteLinkPayloads[rowID] = linkPayload;
        rouletteLinkPayloadIDs[id]  = (byte)rowID;
        return linkPayload;
    }

    private static void GetTextureInfo
    (
        string                   path,
        out IDalamudTextureWrap? texture,
        out Vector2              invTexSize
    )
    {
        texture = ITextureProvider.Instance().GetFromGame(path).GetWrapOrDefault();

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

    private static readonly ContentRoulette[] CachedRoulettes =
    [
        .. LuminaGetter.Get<ContentRoulette>()
                       .Where(x => x is { RowId: > 0 and <= byte.MaxValue, ContentRouletteRoleBonus.RowId: > 0 and < ROULETTE_BONUS_ARRAY_SIZE })
                       .OrderBy(x => x.ContentRouletteRoleBonus.RowId)
    ];

    #endregion
}
