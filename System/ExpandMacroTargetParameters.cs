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
    private delegate float? BattleCharaMetric(BattleChara* chara);

    private delegate bool BattleCharaPredicate(BattleChara* chara);

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

    private static nint LowHPMeAndMemberHandler() =>
        FindBestPartyMember
        (
            true,
            false,
            obj =>
            {
                if (!obj->GetIsTargetable() || obj->IsDead() || obj->Health == obj->MaxHealth)
                    return null;

                return (float)obj->Health / obj->MaxHealth;
            }
        );

    private static nint LowHPMemberHandler() =>
        FindBestPartyMember
        (
            false,
            true,
            obj =>
            {
                if (!obj->GetIsTargetable() || obj->IsDead() || obj->Health == obj->MaxHealth)
                    return null;

                return (float)obj->Health / obj->MaxHealth;
            }
        );

    private static nint LowHPEnemyHandler() =>
        FindBestEnemy
        (enemy =>
            {
                if (enemy->IsDead() || enemy->Health == enemy->MaxHealth)
                    return null;

                return (float)enemy->Health / enemy->MaxHealth;
            }
        );

    private static nint DeadMemberHandler() =>
        FindBestPartyMember
        (
            false,
            true,
            obj =>
            {
                if (!obj->GetIsTargetable() || !obj->IsDead())
                    return null;

                var isTH = LuminaGetter.GetRowOrDefault<ClassJob>(obj->ClassJob).Role is 1 or 4;
                return isTH ?
                           0f :
                           1f;
            }
        );

    private static nint NearMemberHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        return FindBestPartyMember
        (
            false,
            true,
            obj => Vector3.DistanceSquared(localPlayer->Position, obj->Position)
        );
    }

    private static nint FarMemberHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        return FindBestPartyMember
        (
            false,
            true,
            obj => -Vector3.DistanceSquared(localPlayer->Position, obj->Position)
        );
    }

    private static nint NearEnemyHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        return FindBestEnemy(enemy => Vector3.DistanceSquared(localPlayer->Position, enemy->Position));
    }

    private static nint FarEnemyHandler()
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return nint.Zero;

        return FindBestEnemy(enemy => -Vector3.DistanceSquared(localPlayer->Position, enemy->Position));
    }

    private static nint DispellableMeAndMemberHandler() =>
        FindFirstPartyMember(true, false, HasDispellableStatus);

    private static nint DispellableMemberHandler() =>
        FindFirstPartyMember(false, true, HasDispellableStatus);

    private static nint MeAndMemberStatusHandler(uint id) =>
        FindFirstPartyMember(true, false, obj => HasStatus(obj, id));

    private static nint MemberStatusHandler(uint id) =>
        FindFirstPartyMember(false, true, obj => HasStatus(obj, id));

    private static nint EnemyStatusHandler(uint id)
    {
        var manager = CharacterManager.Instance();

        foreach (var battleCharaPtr in manager->BattleCharas)
        {
            if (battleCharaPtr.IsNull) continue;

            var battleChara = battleCharaPtr.Value;
            if (!CanUseActionOnEnemy((GameObject*)battleChara))
                continue;

            if (HasStatus(battleChara, id))
                return (nint)battleChara;
        }

        return nint.Zero;
    }

    private static nint LowestHateEnemyHandler()
    {
        var hater = UIState.Instance()->Hater;

        return FindBestEnemy
        (enemy =>
            {
                for (var i = 0; i < hater.HaterCount; i++)
                {
                    var hateInfo = hater.Haters[i];
                    if (hateInfo.EntityId == enemy->EntityId)
                        return (float)hateInfo.Enmity;
                }

                return 0f;
            }
        );
    }

    private static nint LowestHateEnemyInListHandler()
    {
        var hater = UIState.Instance()->Hater;

        return FindBestEnemy
        (enemy =>
            {
                for (var i = 0; i < hater.HaterCount; i++)
                {
                    var hateInfo = hater.Haters[i];
                    if (hateInfo.EntityId == enemy->EntityId)
                        return hateInfo.Enmity;
                }

                return null;
            }
        );
    }

    private static nint FindBestPartyMember(bool includeSelf, bool requireMultiple, BattleCharaMetric metric)
    {
        var agent = AgentHUD.Instance();
        if (agent == null) return nint.Zero;
        if (requireMultiple && agent->PartyMemberCount == 1) return nint.Zero;

        BattleChara* result    = null;
        var          bestValue = float.MaxValue;

        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var member = agent->PartyMembers[i];
            if (!includeSelf && member.ContentId == LocalPlayerState.ContentID) continue;

            var obj = member.Object;
            if (obj == null) continue;

            var value = metric(obj);
            if (value == null) continue;

            if (result == null || value.Value < bestValue)
            {
                result    = obj;
                bestValue = value.Value;
            }
        }

        return (nint)result;
    }

    private static nint FindFirstPartyMember(bool includeSelf, bool requireMultiple, BattleCharaPredicate predicate)
    {
        var agent = AgentHUD.Instance();
        if (agent == null) return nint.Zero;
        if (requireMultiple && agent->PartyMemberCount == 1) return nint.Zero;

        for (var i = 0; i < agent->PartyMemberCount; i++)
        {
            var member = agent->PartyMembers[i];
            if (!includeSelf && member.ContentId == LocalPlayerState.ContentID) continue;

            var obj = member.Object;
            if (obj == null) continue;

            if (predicate(obj))
                return (nint)obj;
        }

        return nint.Zero;
    }

    private static nint FindBestEnemy(BattleCharaMetric metric)
    {
        var manager = CharacterManager.Instance();

        BattleChara* result    = null;
        var          bestValue = float.MaxValue;

        foreach (var battleCharaPtr in manager->BattleCharas)
        {
            if (battleCharaPtr.IsNull) continue;

            var battleChara = battleCharaPtr.Value;
            if (!CanUseActionOnEnemy((GameObject*)battleChara)) continue;

            var value = metric(battleChara);
            if (value == null) continue;

            if (result == null || value.Value < bestValue)
            {
                result    = battleChara;
                bestValue = value.Value;
            }
        }

        return (nint)result;
    }

    private static bool HasStatus(BattleChara* obj, uint id)
    {
        var statuses = obj->GetStatusManager()->Status;

        foreach (var status in statuses)
        {
            if (status.StatusId == id)
                return true;
        }

        return false;
    }

    private static bool HasDispellableStatus(BattleChara* obj)
    {
        var statuses = obj->GetStatusManager()->Status;

        foreach (var status in statuses)
        {
            if (Sheets.DispellableStatuses.ContainsKey(status.StatusId))
                return true;
        }

        return false;
    }

    // 真龙波和魔弹射手
    private static bool CanUseActionOnEnemy(GameObject* target) =>
        target->GetIsTargetable()  &&
        target->IsReadyToDraw()    &&
        target->NextDistance <= 45 &&
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
