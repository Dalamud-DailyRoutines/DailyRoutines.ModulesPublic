using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class ExpandMacroTargetParameters : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ExpandMacroTargetParametersTitle"),
        Description = GetLoc("ExpandMacroTargetParametersDescription"),
        Category    = ModuleCategories.Combat
    };

    private static readonly CompSig                           ResolvePlaceholderSig = new("E8 ?? ?? ?? ?? 33 ED 4C 8B F8");
    private delegate        GameObject*                       ResolvePlaceholderDelegate(PronounModule* module, byte* str, byte a3, byte a4);
    private static          Hook<ResolvePlaceholderDelegate>? ResolvePlaceholderHook;

    protected override void Init()
    {
        ResolvePlaceholderHook ??= ResolvePlaceholderSig.GetHook<ResolvePlaceholderDelegate>(ResolvePlaceholderDetour);
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
                NotificationSuccess($"{GetLoc("CopiedToClipboard")}: {kvp.Key}");
            }
            
            ImGui.TableNextColumn();
            ImGui.Text(kvp.Value.Description);
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
                NotificationSuccess($"{GetLoc("CopiedToClipboard")}: {kvp.Key}");
            }
            
            ImGui.TableNextColumn();
            ImGui.Text(kvp.Value.Description);
        }
    }

    private static GameObject* ResolvePlaceholderDetour(PronounModule* module, byte* str, byte a3, byte a4)
    {
        var orig = ResolvePlaceholderHook.Original(module, str, a3, a4);
        if (orig != null) return orig;
        
        var decoded = Marshal.PtrToStringUTF8((nint)str);
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
                                                .Where(x => x.Object         != null && x.Object->GetIsTargetable() && !x.Object->IsDead() &&
                                                            x.Object->Health != x.Object->MaxHealth)
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
                                                .Where(x => x.ContentId != LocalPlayerState.ContentID && x.Object != null && 
                                                            x.Object->GetIsTargetable() && !x.Object->IsDead() &&
                                                            x.Object->Health != x.Object->MaxHealth)
                                                .OrderBy(x => (float)x.Object->Health / x.Object->MaxHealth)
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;
        
        return (nint)hudPartyMember.Object;
    }
    
    private static nint LowHPEnemyHandler()
    {
        var enemy = DService.ObjectTable
                            .Where(x => x is IBattleChara { IsTargetable: true, IsDead: false } chara &&
                                        CanUseActionOnEnemy(x.ToStruct())                             &&
                                        chara.CurrentHp != chara.MaxHp)
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
                                                .Where(x => x.ContentId != LocalPlayerState.ContentID && x.Object != null && 
                                                            x.Object->GetIsTargetable()               && x.Object->IsDead())
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
                                                .Where(x => x.ContentId != LocalPlayerState.ContentID && x.Object != null && 
                                                            x.Object->GetIsTargetable()                        && !x.Object->IsDead())
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
                                                .Where(x => x.ContentId != LocalPlayerState.ContentID && x.Object != null && 
                                                            x.Object->GetIsTargetable()                        && !x.Object->IsDead())
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

        var enemy = DService.ObjectTable
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

        var enemy = DService.ObjectTable
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
                                                .Where(x =>
                                                {
                                                    if (x.Object == null) return false;

                                                    var statuses = x.Object->GetStatusManager()->Status;
                                                    foreach (var status in statuses)
                                                    {
                                                        if (PresetSheet.DispellableStatuses.ContainsKey(status.StatusId))
                                                            return true;
                                                    }
                                                    
                                                    return false;
                                                })
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
                                                .Where(x =>
                                                {
                                                    if (x.ContentId == LocalPlayerState.ContentID || x.Object == null) return false;

                                                    var statuses = x.Object->GetStatusManager()->Status;
                                                    foreach (var status in statuses)
                                                    {
                                                        if (PresetSheet.DispellableStatuses.ContainsKey(status.StatusId))
                                                            return true;
                                                    }
                                                    
                                                    return false;
                                                })
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
                                                .Where(x =>
                                                {
                                                    if (x.Object == null) return false;

                                                    var statuses = x.Object->GetStatusManager()->Status;
                                                    foreach (var status in statuses)
                                                    {
                                                        if (status.StatusId == id)
                                                            return true;
                                                    }
                                                    
                                                    return false;
                                                })
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
                                                .Where(x =>
                                                {
                                                    if (x.ContentId == LocalPlayerState.ContentID || x.Object == null) return false;

                                                    var statuses = x.Object->GetStatusManager()->Status;
                                                    foreach (var status in statuses)
                                                    {
                                                        if (status.StatusId == id)
                                                            return true;
                                                    }
                                                    
                                                    return false;
                                                })
                                                .FirstOrDefault();
        if (hudPartyMember.Object == null)
            return nint.Zero;
        
        return (nint)hudPartyMember.Object;
    }
    
    private static nint EnemyStatusHandler(uint id)
    {
        var enemy = DService.ObjectTable
                            .Where(x =>
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
                            })
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
    
    private static readonly Dictionary<string, (string Description, Func<nint> Handler)> Arguments = new()
    {
        ["<lowhpmeandmember>"]       = new(GetLoc("ExpandMacroTargetParameters-Param-LowHpMeAndMember"), LowHPMeAndMemberHandler),
        ["<lowhpmember>"]            = new(GetLoc("ExpandMacroTargetParameters-Param-LowHpMember"), LowHPMemberHandler),
        ["<lowhpenemy>"]             = new(GetLoc("ExpandMacroTargetParameters-Param-LowHpEnemy"), LowHPEnemyHandler),
        ["<deadmember>"]             = new(GetLoc("ExpandMacroTargetParameters-Param-DeadMember"), DeadMemberHandler),
        ["<nearmember>"]             = new(GetLoc("ExpandMacroTargetParameters-Param-NearMember"), NearMemberHandler),
        ["<farmember>"]              = new(GetLoc("ExpandMacroTargetParameters-Param-FarMember"), FarMemberHandler),
        ["<nearenemy>"]              = new(GetLoc("ExpandMacroTargetParameters-Param-NearEnemy"), NearEnemyHandler),
        ["<farenemy>"]               = new(GetLoc("ExpandMacroTargetParameters-Param-FarEnemy"), FarEnemyHandler),
        ["<dispellablemeandmember>"] = new(GetLoc("ExpandMacroTargetParameters-Param-DispellableMeAndMember"), DispellableMeAndMemberHandler),
        ["<dispellablemember>"]      = new(GetLoc("ExpandMacroTargetParameters-Param-DispellableMember"), DispellableMemberHandler),
    };

    private static readonly Dictionary<string, (string Description, Func<uint, nint> Handler)> StartWithArguments = new()
    {
        ["<meandmemberstatus:"] = new(GetLoc("ExpandMacroTargetParameters-Param-MeAndMemberStatus"), MeAndMemberStatusHandler),
        ["<memberstatus:"]      = new(GetLoc("ExpandMacroTargetParameters-Param-MemberStatus"), MemberStatusHandler),
        ["<enemystatus:"]       = new(GetLoc("ExpandMacroTargetParameters-Param-EnemyStatus"), EnemyStatusHandler),
    };
}
