using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoReplaceActionLowLevel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoReplaceActionLowLevelTitle"),
        Description = Lang.Get("AutoReplaceActionLowLevelDescription"),
        Category    = ModuleCategory.Action
    };
    
    private static readonly CompSig IsActionReplaceableSig =
        new("40 53 48 83 EC ?? 8B D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B 10 48 8B C8 FF 92 ?? ?? ?? ?? 8B D3");
    private delegate bool                              IsActionReplaceableDelegate(uint actionID);
    private          Hook<IsActionReplaceableDelegate> IsActionReplaceableHook;

    private static readonly CompSig                           GetAdjustedActionIDSig = new("E8 ?? ?? ?? ?? 89 03 8B 03");
    private delegate        uint                              GetAdjustedActionIDDelegate(ActionManager* manager, uint actionID);
    private                 Hook<GetAdjustedActionIDDelegate> GetAdjustedActionIDHook;

    private static readonly CompSig GetIconIDForSlotSig = new("E8 ?? ?? ?? ?? 85 C0 89 83 ?? ?? ?? ?? 0F 94 C0");
    private delegate        uint    GetIconIDForSlotDelegate(RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint actionID);
    private                 Hook<GetIconIDForSlotDelegate> GetIconIDForSlotHook;

    protected override void Init()
    {
        IsActionReplaceableHook ??= IsActionReplaceableSig.GetHook<IsActionReplaceableDelegate>(IsActionReplaceableDetour);
        IsActionReplaceableHook.Enable();

        GetAdjustedActionIDHook ??= GetAdjustedActionIDSig.GetHook<GetAdjustedActionIDDelegate>(GetAdjustedActionIDDetour);
        GetAdjustedActionIDHook.Enable();

        GetIconIDForSlotHook ??= GetIconIDForSlotSig.GetHook<GetIconIDForSlotDelegate>(GetIconIDForSlotDetour);
        GetIconIDForSlotHook.Enable();
    }

    protected override void ConfigUI()
    {
        using var table = ImRaii.Table("ActionReplacementDisplayTable", 3, ImGuiTableFlags.None, new(ImGui.GetContentRegionAvail().X / 2, 0));
        if (!table) return;

        // 让它们在视觉上看起来更平均
        ImGui.TableSetupColumn("技能1", ImGuiTableColumnFlags.None, 40);
        ImGui.TableSetupColumn("箭头",  ImGuiTableColumnFlags.None, 10);
        ImGui.TableSetupColumn("技能2", ImGuiTableColumnFlags.None, 40);

        foreach (var (action0, action1) in ActionReplacements)
        {
            var action0Data = LuminaGetter.GetRow<Action>(action0);
            var action1Data = LuminaGetter.GetRow<Action>(action1);
            if (action0Data == null || action1Data == null) continue;

            var action0Icon = DService.Instance().Texture.GetFromGameIcon(new(action0Data.Value.Icon)).GetWrapOrDefault();
            var action1Icon = DService.Instance().Texture.GetFromGameIcon(new(action1Data.Value.Icon)).GetWrapOrDefault();
            if (action0Icon == null || action1Icon == null) continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGuiOm.TextImage(action0Data.Value.Name.ToString(), action0Icon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("→");

            ImGui.TableNextColumn();
            ImGuiOm.TextImage(action1Data.Value.Name.ToString(), action1Icon.Handle, new(ImGui.GetTextLineHeightWithSpacing()));
        }
    }

    private uint GetAdjustedActionIDDetour(ActionManager* manager, uint actionID) =>
        !TryGetReplacement(actionID, out var adjustedActionID)
            ? GetAdjustedActionIDHook.Original(manager, actionID)
            : adjustedActionID;
    
    private uint GetIconIDForSlotDetour(RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint actionID)
    {
        if (type != RaptureHotbarModule.HotbarSlotType.Action)
            return GetIconIDForSlotHook.Original(slot, type, actionID);

        return !TryGetReplacement(actionID, out var adjustedActionID)
                   ? GetIconIDForSlotHook.Original(slot, type, actionID)
                   : LuminaGetter.TryGetRow<Action>(adjustedActionID, out var row)
                       ? row.Icon
                       : 0u;
    }

    private bool IsActionReplaceableDetour(uint actionID) =>
        ActionReplacements.ContainsKey(actionID) || IsActionReplaceableHook.Original(actionID);
    
    private static bool TryGetReplacement(uint actionID, out uint adjustedActionID)
    {
        while (true)
        {
            adjustedActionID = 0;
            if (ActionManager.IsActionUnlocked(actionID)) return false;
            if (!ActionReplacements.TryGetValue(actionID, out var info)) return false;

            if (ActionManager.IsActionUnlocked(info))
            {
                adjustedActionID = info;
                return true;
            }

            actionID = info;
        }
    }

    #region 常量

    // 原技能 ID - 替换后技能 ID (递归替换)
    private static readonly FrozenDictionary<uint, uint> ActionReplacements = new Dictionary<uint, uint>()
    {
        // 狂喜之心 - 医济
        [16534] = 133,
        // 医济 - 医治
        [133] = 124,
        // 安慰之心 - 救疗
        [16531] = 135,
        // 救疗 - 治疗
        [135] = 120,
        // 鼓舞激励之策 - 医术
        [185] = 190,
        // 福星 - 吉星
        [3610] = 3594,
        // 阳星相位 - 阳星
        [3601] = 3600,
        // 异言 - 悖论
        [16507] = 7422,
        // 必杀剑·闪影 - 必杀剑·红莲
        [16481] = 7496
    }.ToFrozenDictionary();

    #endregion
}
