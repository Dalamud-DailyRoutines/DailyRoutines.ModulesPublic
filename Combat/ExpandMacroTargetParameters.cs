using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class ExpandMacroTargetParameters : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ExpandMacroTargetParametersTitle"),
        Description = Lang.Get("ExpandMacroTargetParametersDescription"),
        Category    = ModuleCategory.Combat
    };

    private Hook<PronounModule.Delegates.ResolvePlaceholder>? ResolvePlaceholderHook;

    protected override void Init()
    {
        ResolvePlaceholderHook = DService.Instance().Hook.HookFromMemberFunction
        (
            typeof(PronounModule.MemberFunctionPointers),
            "ResolvePlaceholder",
            (PronounModule.Delegates.ResolvePlaceholder)ResolvePlaceholderDetour
        );
        ResolvePlaceholderHook.Enable();
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("ParametersTable", 2, ImGuiTableFlags.Borders);
        if (!table) return;

        ImGui.TableSetupColumn("参数", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("描述", ImGuiTableColumnFlags.WidthStretch, 50);

        foreach (var kvp in Arguments)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), kvp.Key);
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(kvp.Key);
                NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {kvp.Key}");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(kvp.Value.Description);
        }

        foreach (var kvp in StartWithArguments)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{kvp.Key}ID>");
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText($"{kvp.Key}ID>");
                NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {kvp.Key}");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(kvp.Value.Description);
        }
    }

    private GameObject* ResolvePlaceholderDetour
    (
        PronounModule* module,
        CStringPointer placeholder,
        byte           unknown0,
        byte           unknown1,
        bool           unknown2
    )
    {
        var orig = ResolvePlaceholderHook.Original(module, placeholder, unknown0, unknown1, unknown2);
        if (orig != null) return orig;

        var decoded = placeholder.ToString();
        if (string.IsNullOrEmpty(decoded)) return null;

        if (Arguments.TryGetValue(decoded, out var info))
            return (GameObject*)info.Handler();

        foreach (var kvp in StartWithArguments)
        {
            if (decoded.StartsWith(kvp.Key) && uint.TryParse(decoded[kvp.Key.Length..].TrimEnd('>'), out var id))
                return (GameObject*)kvp.Value.Handler(id);
        }

        return null;
    }

    private static nint LowHPMeAndMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null) return nint.Zero;

        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x => x.Object != null            &&
                                                      x.Object->GetIsTargetable() &&
                                                      !x.Object->IsDead()         &&
                                                      x.Object->Health != x.Object->MaxHealth
                                                )
                                                .OrderBy(x => (float)x.Object->Health / x.Object->MaxHealth)
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint LowHPMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x => x.ContentId != LocalPlayerState.ContentID &&
                                                      x.Object    != null                       &&
                                                      x.Object->GetIsTargetable()               &&
                                                      !x.Object->IsDead()                       &&
                                                      x.Object->Health != x.Object->MaxHealth
                                                )
                                                .OrderBy(x => (float)x.Object->Health / x.Object->MaxHealth)
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint LowHPEnemyHandler()
    {
        var enemy = DService.Instance().ObjectTable
                            .Where
                            (x => x is IBattleChara { IsTargetable: true, IsDead: false } chara &&
                                  CanUseActionOnEnemy(x.ToStruct())                             &&
                                  chara.CurrentHp != chara.MaxHp
                            )
                            .OrderBy(x => (float)x.ToBCStruct()->Health / x.ToBCStruct()->MaxHealth)
                            .FirstOrDefault();
        if (enemy == null) return nint.Zero;

        return enemy.Address;
    }

    private static nint DeadMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        // TH 优先
        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x => x.ContentId != LocalPlayerState.ContentID &&
                                                      x.Object    != null                       &&
                                                      x.Object->GetIsTargetable()               &&
                                                      x.Object->IsDead()
                                                )
                                                .OrderByDescending(x => LuminaGetter.GetRow<ClassJob>(x.Object->ClassJob)!.Value.Role is 1 or 4)
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint NearMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x => x.ContentId != LocalPlayerState.ContentID &&
                                                      x.Object    != null                       &&
                                                      x.Object->GetIsTargetable()               &&
                                                      !x.Object->IsDead()
                                                )
                                                .OrderBy(x => Vector3.DistanceSquared(localPlayer->Position, x.Object->Position))
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint FarMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x => x.ContentId != LocalPlayerState.ContentID &&
                                                      x.Object    != null                       &&
                                                      x.Object->GetIsTargetable()               &&
                                                      !x.Object->IsDead()
                                                )
                                                .OrderByDescending(x => Vector3.DistanceSquared(localPlayer->Position, x.Object->Position))
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint NearEnemyHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        var enemy = DService.Instance().ObjectTable
                            .Where(x => CanUseActionOnEnemy(x.ToStruct()))
                            .OrderBy(x => Vector3.DistanceSquared(localPlayer->Position, x.Position))
                            .FirstOrDefault();
        if (enemy == null) return nint.Zero;

        return enemy.Address;
    }

    private static nint FarEnemyHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        var enemy = DService.Instance().ObjectTable
                            .Where(x => CanUseActionOnEnemy(x.ToStruct()))
                            .OrderByDescending(x => Vector3.DistanceSquared(localPlayer->Position, x.Position))
                            .FirstOrDefault();
        if (enemy == null) return nint.Zero;

        return enemy.Address;
    }

    private static nint DispellableMeAndMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null) return nint.Zero;

        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x =>
                                                    {
                                                        if (x.Object == null) return false;

                                                        var statuses = x.Object->GetStatusManager()->Status;

                                                        foreach (var status in statuses)
                                                        {
                                                            if (Sheets.DispellableStatuses.ContainsKey(status.StatusId))
                                                                return true;
                                                        }

                                                        return false;
                                                    }
                                                )
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint DispellableMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x =>
                                                    {
                                                        if (x.ContentId == LocalPlayerState.ContentID || x.Object == null) return false;

                                                        var statuses = x.Object->GetStatusManager()->Status;

                                                        foreach (var status in statuses)
                                                        {
                                                            if (Sheets.DispellableStatuses.ContainsKey(status.StatusId))
                                                                return true;
                                                        }

                                                        return false;
                                                    }
                                                )
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint MeAndMemberStatusHandler(uint id)
    {
        var agent = AgentHUD.Instance();
        if (agent == null) return nint.Zero;

        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x =>
                                                    {
                                                        if (x.Object == null) return false;

                                                        var statuses = x.Object->GetStatusManager()->Status;

                                                        foreach (var status in statuses)
                                                        {
                                                            if (status.StatusId == id)
                                                                return true;
                                                        }

                                                        return false;
                                                    }
                                                )
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint MemberStatusHandler(uint id)
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        var hudPartyMember = agent->PartyMembers.ToArray()
                                                .Where
                                                (x =>
                                                    {
                                                        if (x.ContentId == LocalPlayerState.ContentID || x.Object == null) return false;

                                                        var statuses = x.Object->GetStatusManager()->Status;

                                                        foreach (var status in statuses)
                                                        {
                                                            if (status.StatusId == id)
                                                                return true;
                                                        }

                                                        return false;
                                                    }
                                                )
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;

        return (nint)hudPartyMember.Object;
    }

    private static nint EnemyStatusHandler(uint id)
    {
        var enemy = DService.Instance().ObjectTable
                            .Where
                            (x =>
                                {
                                    if (!CanUseActionOnEnemy(x.ToStruct())) return false;

                                    var bc = x.ToBCStruct();
                                    if (bc == null) return false;

                                    var statuses = bc->GetStatusManager()->Status;

                                    foreach (var status in statuses)
                                    {
                                        if (status.StatusId == id)
                                            return true;
                                    }

                                    return false;
                                }
                            )
                            .FirstOrDefault();
        if (enemy == null) return nint.Zero;

        return enemy.Address;
    }

    // 真龙波和魔弹射手
    private static bool CanUseActionOnEnemy(GameObject* target) =>
        target->GetIsTargetable()             &&
        target->IsReadyToDraw()               &&
        target->YalmDistanceFromPlayerX <= 45 &&
        target->YalmDistanceFromPlayerZ <= 45 &&
        (ActionManager.CanUseActionOnTarget(7428, target) || ActionManager.CanUseActionOnTarget(29415, target));
    
    #region 常量

    private static readonly FrozenDictionary<string, (string Description, Func<nint> Handler)> Arguments =
        new Dictionary<string, (string Description, Func<nint> Handler)>
        {
            ["<lowhpmeandmember>"]       = new(Lang.Get("ExpandMacroTargetParameters-Param-LowHpMeAndMember"), LowHPMeAndMemberHandler),
            ["<lowhpmember>"]            = new(Lang.Get("ExpandMacroTargetParameters-Param-LowHpMember"), LowHPMemberHandler),
            ["<lowhpenemy>"]             = new(Lang.Get("ExpandMacroTargetParameters-Param-LowHpEnemy"), LowHPEnemyHandler),
            ["<deadmember>"]             = new(Lang.Get("ExpandMacroTargetParameters-Param-DeadMember"), DeadMemberHandler),
            ["<nearmember>"]             = new(Lang.Get("ExpandMacroTargetParameters-Param-NearMember"), NearMemberHandler),
            ["<farmember>"]              = new(Lang.Get("ExpandMacroTargetParameters-Param-FarMember"), FarMemberHandler),
            ["<nearenemy>"]              = new(Lang.Get("ExpandMacroTargetParameters-Param-NearEnemy"), NearEnemyHandler),
            ["<farenemy>"]               = new(Lang.Get("ExpandMacroTargetParameters-Param-FarEnemy"), FarEnemyHandler),
            ["<dispellablemeandmember>"] = new(Lang.Get("ExpandMacroTargetParameters-Param-DispellableMeAndMember"), DispellableMeAndMemberHandler),
            ["<dispellablemember>"]      = new(Lang.Get("ExpandMacroTargetParameters-Param-DispellableMember"), DispellableMemberHandler)
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, (string Description, Func<uint, nint> Handler)> StartWithArguments =
        new Dictionary<string, (string Description, Func<uint, nint> Handler)>
        {
            ["<meandmemberstatus:"] = new(Lang.Get("ExpandMacroTargetParameters-Param-MeAndMemberStatus"), MeAndMemberStatusHandler),
            ["<memberstatus:"]      = new(Lang.Get("ExpandMacroTargetParameters-Param-MemberStatus"), MemberStatusHandler),
            ["<enemystatus:"]       = new(Lang.Get("ExpandMacroTargetParameters-Param-EnemyStatus"), EnemyStatusHandler)
        }.ToFrozenDictionary();

    #endregion
}
