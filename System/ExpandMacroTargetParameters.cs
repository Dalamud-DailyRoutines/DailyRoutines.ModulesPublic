using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class ExpandMacroTargetParameters : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ExpandMacroTargetParametersTitle"),
        Description = Lang.Get("ExpandMacroTargetParametersDescription"),
        Category    = ModuleCategory.System
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

        BattleChara* result = null;
        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var obj = agent->PartyMembers[i].Object;
            if (obj == null) continue;

            if (!obj->GetIsTargetable() ||
                obj->IsDead()           ||
                obj->Health == obj->MaxHealth)
                continue;

            if (result == null || (float)result->Health / result->MaxHealth > (float)obj->Health / obj->MaxHealth)
                result = obj;
        }
        
        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    private static nint LowHPMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        BattleChara* result = null;
        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var obj = agent->PartyMembers[i].Object;
            if (obj == null) continue;

            if (!obj->GetIsTargetable() ||
                obj->IsDead()           ||
                obj->Health == obj->MaxHealth)
                continue;

            if (result == null || (float)result->Health / result->MaxHealth > (float)obj->Health / obj->MaxHealth)
                result = obj;
        }
        
        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    private static nint LowHPEnemyHandler()
    {
        var manager = CharacterManager.Instance();
        
        BattleChara* result = null;
        foreach (var battleCharaPtr in manager->BattleCharas)
        {
            if (battleCharaPtr.IsNull) continue;

            var battleChara = battleCharaPtr.Value;
            if (!battleChara->GetIsTargetable()               ||
                battleChara->IsDead()                         ||
                battleChara->Health == battleChara->MaxHealth ||
                !CanUseActionOnEnemy((GameObject*)battleChara))
                continue;
            
            if (result == null || (float)result->Health / result->MaxHealth > (float)battleChara->Health / battleChara->MaxHealth)
                result = battleChara;
        }
        
        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    private static nint DeadMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        BattleChara* result = null;
        var resultIsTH = false;
        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var member = agent->PartyMembers[i];
            if (member.ContentId == LocalPlayerState.ContentID) continue;

            var obj = member.Object;
            if (obj == null) continue;

            if (!obj->GetIsTargetable() ||
                !obj->IsDead())
                continue;

            var isTH = LuminaGetter.GetRowOrDefault<ClassJob>(obj->ClassJob).Role is 1 or 4;
            if (result == null || (isTH && !resultIsTH))
            {
                result = obj;
                resultIsTH = isTH;
            }
        }

        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    private static nint NearMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        BattleChara* result = null;
        var minDist = float.MaxValue;
        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var member = agent->PartyMembers[i];
            if (member.ContentId == LocalPlayerState.ContentID) continue;

            var obj = member.Object;
            if (obj == null) continue;

            if (!obj->GetIsTargetable() || obj->IsDead())
                continue;

            var dist = Vector3.DistanceSquared(localPlayer->Position, obj->Position);
            if (result == null || dist < minDist)
            {
                result = obj;
                minDist = dist;
            }
        }

        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    private static nint FarMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        BattleChara* result = null;
        var maxDist = float.MinValue;
        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var member = agent->PartyMembers[i];
            if (member.ContentId == LocalPlayerState.ContentID) continue;

            var obj = member.Object;
            if (obj == null) continue;

            if (!obj->GetIsTargetable() || obj->IsDead())
                continue;

            var dist = Vector3.DistanceSquared(localPlayer->Position, obj->Position);
            if (result == null || dist > maxDist)
            {
                result = obj;
                maxDist = dist;
            }
        }

        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    private static nint NearEnemyHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        var manager = CharacterManager.Instance();

        BattleChara* result = null;
        var minDist = float.MaxValue;
        foreach (var battleCharaPtr in manager->BattleCharas)
        {
            if (battleCharaPtr.IsNull) continue;

            var battleChara = battleCharaPtr.Value;
            if (!CanUseActionOnEnemy((GameObject*)battleChara))
                continue;

            var dist = Vector3.DistanceSquared(localPlayer->Position, battleChara->Position);
            if (result == null || dist < minDist)
            {
                result = battleChara;
                minDist = dist;
            }
        }

        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    private static nint FarEnemyHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        var manager = CharacterManager.Instance();

        BattleChara* result = null;
        var maxDist = float.MinValue;
        foreach (var battleCharaPtr in manager->BattleCharas)
        {
            if (battleCharaPtr.IsNull) continue;

            var battleChara = battleCharaPtr.Value;
            if (!CanUseActionOnEnemy((GameObject*)battleChara))
                continue;

            var dist = Vector3.DistanceSquared(localPlayer->Position, battleChara->Position);
            if (result == null || dist > maxDist)
            {
                result = battleChara;
                maxDist = dist;
            }
        }

        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    private static nint DispellableMeAndMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null) return nint.Zero;

        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var obj = agent->PartyMembers[i].Object;
            if (obj == null) continue;

            var statuses = obj->GetStatusManager()->Status;
            foreach (var status in statuses)
            {
                if (Sheets.DispellableStatuses.ContainsKey(status.StatusId))
                    return (nint)obj;
            }
        }

        return nint.Zero;
    }

    private static nint DispellableMemberHandler()
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var member = agent->PartyMembers[i];
            if (member.ContentId == LocalPlayerState.ContentID) continue;

            var obj = member.Object;
            if (obj == null) continue;

            var statuses = obj->GetStatusManager()->Status;
            foreach (var status in statuses)
            {
                if (Sheets.DispellableStatuses.ContainsKey(status.StatusId))
                    return (nint)obj;
            }
        }

        return nint.Zero;
    }

    private static nint MeAndMemberStatusHandler(uint id)
    {
        var agent = AgentHUD.Instance();
        if (agent == null) return nint.Zero;

        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var obj = agent->PartyMembers[i].Object;
            if (obj == null) continue;

            var statuses = obj->GetStatusManager()->Status;
            foreach (var status in statuses)
            {
                if (status.StatusId == id)
                    return (nint)obj;
            }
        }

        return nint.Zero;
    }

    private static nint MemberStatusHandler(uint id)
    {
        var agent = AgentHUD.Instance();
        if (agent == null || agent->PartyMemberCount == 1) return nint.Zero;

        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var member = agent->PartyMembers[i];
            if (member.ContentId == LocalPlayerState.ContentID) continue;

            var obj = member.Object;
            if (obj == null) continue;

            var statuses = obj->GetStatusManager()->Status;
            foreach (var status in statuses)
            {
                if (status.StatusId == id)
                    return (nint)obj;
            }
        }

        return nint.Zero;
    }

    private static nint EnemyStatusHandler(uint id)
    {
        var manager = CharacterManager.Instance();

        foreach (var battleCharaPtr in manager->BattleCharas)
        {
            if (battleCharaPtr.IsNull) continue;

            var battleChara = battleCharaPtr.Value;
            if (!CanUseActionOnEnemy((GameObject*)battleChara))
                continue;

            var statuses = battleChara->GetStatusManager()->Status;
            foreach (var status in statuses)
            {
                if (status.StatusId == id)
                    return (nint)battleChara;
            }
        }

        return nint.Zero;
    }
    
    private static nint LowestHateEnemyHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        var manager = CharacterManager.Instance();
        var hater    = UIState.Instance()->Hater;

        BattleChara* result  = null;
        var          minHate = float.MaxValue;
        foreach (var battleCharaPtr in manager->BattleCharas)
        {
            if (battleCharaPtr.IsNull) continue;

            var battleChara = battleCharaPtr.Value;
            if (!CanUseActionOnEnemy((GameObject*)battleChara))
                continue;

            var hate = 0;
            for (var i = 0; i < hater.HaterCount; i++)
            {
                var hateInfo = hater.Haters[i];
                if (hateInfo.EntityId != battleChara->EntityId) continue;

                hate = hateInfo.Enmity;
            }
            
            if (result == null || hate < minHate)
            {
                result  = battleChara;
                minHate = hate;
            }
        }

        if (result == null)
            return nint.Zero;

        return (nint)result;
    }
    
    private static nint LowestHateEnemyInListHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        var manager = CharacterManager.Instance();
        var hater   = UIState.Instance()->Hater;

        BattleChara* result  = null;
        var          minHate = float.MaxValue;
        foreach (var battleCharaPtr in manager->BattleCharas)
        {
            if (battleCharaPtr.IsNull) continue;

            var battleChara = battleCharaPtr.Value;
            if (!CanUseActionOnEnemy((GameObject*)battleChara))
                continue;

            int? hate = null;
            for (var i = 0; i < hater.HaterCount; i++)
            {
                var hateInfo = hater.Haters[i];
                if (hateInfo.EntityId != battleChara->EntityId) continue;

                hate = hateInfo.Enmity;
            }
            
            if (hate == null)
                continue;
            
            if (result == null || hate < minHate)
            {
                result  = battleChara;
                minHate = hate.Value;
            }
        }

        if (result == null)
            return nint.Zero;

        return (nint)result;
    }

    // 真龙波和魔弹射手
    private static bool CanUseActionOnEnemy(GameObject* target) =>
        target->GetIsTargetable()             &&
        target->IsReadyToDraw()               &&
        target->YalmDistanceFromPlayerZ <= 45 &&
        (ActionManager.CanUseActionOnTarget(7428, target) || ActionManager.CanUseActionOnTarget(29415, target));
    
    #region 常量

    private static readonly FrozenDictionary<string, (string Description, Func<nint> Handler)> Arguments =
        new Dictionary<string, (string Description, Func<nint> Handler)>
        {
            ["<lowhpmeandmember>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-LowHpMeAndMember"),
                LowHPMeAndMemberHandler
            ),
            ["<lowhpmember>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-LowHpMember"),
                LowHPMemberHandler
            ),
            ["<lowhpenemy>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-LowHpEnemy"),
                LowHPEnemyHandler
            ),
            ["<deadmember>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-DeadMember"),
                DeadMemberHandler
            ),
            ["<nearmember>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-NearMember"),
                NearMemberHandler
            ),
            ["<farmember>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-FarMember"),
                FarMemberHandler
            ),
            ["<nearenemy>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-NearEnemy"),
                NearEnemyHandler
            ),
            ["<farenemy>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-FarEnemy"),
                FarEnemyHandler
            ),
            ["<dispellablemeandmember>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-DispellableMeAndMember"),
                DispellableMeAndMemberHandler
            ),
            ["<dispellablemember>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-DispellableMember"),
                DispellableMemberHandler
            ),
            ["<lowesthateenemy>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-LowestHateEnemy"),
                LowestHateEnemyHandler
            ),
            ["<lowesthateinlistenemy>"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-LowestHateInListEnemy"),
                LowestHateEnemyInListHandler
            )
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, (string Description, Func<uint, nint> Handler)> StartWithArguments =
        new Dictionary<string, (string Description, Func<uint, nint> Handler)>
        {
            ["<meandmemberstatus:"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-MeAndMemberStatus"),
                MeAndMemberStatusHandler
            ),
            ["<memberstatus:"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-MemberStatus"),
                MemberStatusHandler
            ),
            ["<enemystatus:"] = new
            (
                Lang.Get("ExpandMacroTargetParameters-Param-EnemyStatus"),
                EnemyStatusHandler
            )
        }.ToFrozenDictionary();

    #endregion
}
