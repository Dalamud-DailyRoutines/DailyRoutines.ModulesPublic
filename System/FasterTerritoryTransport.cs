using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class FasterTerritoryTransport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("FasterTerritoryTransportTitle"),
        Description         = Lang.Get("FasterTerritoryTransportDescription"),
        Category            = ModuleCategory.System,
        ModulesPrerequisite = ["NoUIFade"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private          Config            config             = null!;
    private readonly Throttler<string> transportThrottler = new();

    // 城内以太之晶传送
    private static readonly CompSig TeleportToAetheryteSig = new("E8 ?? ?? ?? ?? 32 C0 48 8B 74 24 ?? 48 83 C4 ?? 5F C3 48 8D 4E ?? E8 ?? ?? ?? ?? 48 8B 4F");
    private unsafe delegate void    TeleportToAetheryteDelegate(AgentTelepotTown* agent, byte index);
    private                 Hook<TeleportToAetheryteDelegate>? TeleportToAetheryteHook;

    private static readonly CompSig IsConditionAbleToSetSig = new
        ("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 54 41 56 41 57 48 83 EC ?? 33 DB 41 8B E9 48 39 1D");
    private delegate bool                                IsConditionAbleToSetDelegate(nint conditionAddress, ConditionFlag flag, int a3, int a4);
    private          Hook<IsConditionAbleToSetDelegate>? IsConditionAbleToSetHook;

    protected override unsafe void Init()
    {
        config = Config.Load(this) ?? new();

        ExecuteCommandManager.Instance().RegPost(OnPostUseCommand);
        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseActionLocation);

        TeleportToAetheryteHook ??= TeleportToAetheryteSig.GetHook<TeleportToAetheryteDelegate>(TeleportToAetheryteDetour);
        TeleportToAetheryteHook.Enable();

        IsConditionAbleToSetHook ??= IsConditionAbleToSetSig.GetHook<IsConditionAbleToSetDelegate>(IsConditionAbleToSetDetour);
        IsConditionAbleToSetHook.Enable();

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
    }
    
    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;

        ExecuteCommandManager.Instance().Unreg(OnPostUseCommand);
        UseActionManager.Instance().Unreg(OnPostUseActionLocation);

        transportThrottler.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("FasterTerritoryTransport-OnlyLocal"), ref config.OnlyLocal))
            config.Save(this);

        ImGuiOm.HelpMarker(Lang.Get("FasterTerritoryTransport-OnlyLocalHelp"), 20f * GlobalUIScale);
    }

    private bool IsConditionAbleToSetDetour(nint conditionaddress, ConditionFlag flag, int a3, int a4)
    {
        var ret = IsConditionAbleToSetHook.Original(conditionaddress, flag, a3, a4);

        if (!DService.Instance().Condition[ConditionFlag.BetweenAreas] && !DService.Instance().Condition[ConditionFlag.Occupied33])
            return ret;

        if (BlockedFlags.Contains((uint)flag)) return true;

        return ret;
    }

    private unsafe void TeleportToAetheryteDetour(AgentTelepotTown* agent, byte index)
    {
        if (agent == null) return;

        TeleportToAetheryteHook.Original(agent, index);
        transportThrottler.Throttle("Block", 10_000);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.BetweenAreas) return;

        if (!config.OnlyLocal && !DService.Instance().ClientState.IsPvPExcludingDen && transportThrottler.Check("Block"))
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.TerritoryTransport);

        if (!value)
            transportThrottler.Clear();
    }

    private void OnPostUseActionLocation
    (
        bool       result,
        ActionType actionType,
        uint       actionID,
        ulong      targetID,
        Vector3    location,
        uint       a4,
        byte       a7
    )
    {
        if (!result) return;

        // 返回
        var isNeedToThrottle = actionType == ActionType.GeneralAction && actionID == 8;

        if (isNeedToThrottle)
            transportThrottler.Throttle("Block", 10_000);
    }

    private void OnPostUseCommand(ExecuteCommandFlag command, uint param1, uint param2, uint param3, uint param4)
    {
        var isNeedToThrottle = ValidFlags.Contains(command);

        if (isNeedToThrottle)
            transportThrottler.Throttle("Block", 10_000);
    }
    
    private class Config : ModuleConfig
    {
        public bool OnlyLocal = true;
    }
    
    #region 常量
    
    private static readonly FrozenSet<ExecuteCommandFlag> ValidFlags =
    [
        ExecuteCommandFlag.Revive,
        ExecuteCommandFlag.Teleport,
        ExecuteCommandFlag.TeleportToFriendHouse,
        ExecuteCommandFlag.AcceptTeleportOffer,
        ExecuteCommandFlag.InstantReturn,
        ExecuteCommandFlag.ReturnIfNotLalafell
    ];

    private static readonly FrozenSet<uint> BlockedFlags =
    [
        96,
        97,
        98
    ];

    #endregion
}
