using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.Expansion.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;
using WarpType = FFXIVClientStructs.FFXIV.Client.Game.UI.WarpType;

namespace DailyRoutines.ModulesPublic;

public unsafe class FasterTerritoryTransport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FasterTerritoryTransportTitle"),
        Description = Lang.Get("FasterTerritoryTransportDescription"),
        Category    = ModuleCategory.System,
        ModulesPair = ["NoUIFade"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        NeedAuth = true
    };

    private Hook<WarpInfo.Delegates.HandlePrepareZoningPacket>? WarpInfoPrepareZoningHook;
    private Hook<WarpInfo.Delegates.Update>?                    WarpInfoUpdateHook;

    protected override void Init()
    {
        WarpInfoPrepareZoningHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(WarpInfo.MemberFunctionPointers),
            nameof(WarpInfo.MemberFunctionPointers.HandlePrepareZoningPacket),
            (WarpInfo.Delegates.HandlePrepareZoningPacket)WarpInfoPrepareZoningDetour
        );
        WarpInfoPrepareZoningHook.Enable();

        WarpInfoUpdateHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(WarpInfo.MemberFunctionPointers),
            nameof(WarpInfo.MemberFunctionPointers.Update),
            (WarpInfo.Delegates.Update)WarpInfoUpdateDetour
        );
        WarpInfoUpdateHook.Enable();
    }

    private void WarpInfoPrepareZoningDetour
    (
        WarpInfo*            instance,
        PrepareZoningPacket* packet,
        byte                 replayFlag
    )
    {
        WarpInfoPrepareZoningHook.Original(instance, packet, replayFlag);

        if (InvalidWarpTypes.Contains(instance->WarpType))
            return;

        ForceCompleteWarp(instance);
    }

    private void WarpInfoUpdateDetour
    (
        WarpInfo* instance
    )
    {
        if (instance->State != WarpState.Idle &&
            !InvalidWarpTypes.Contains(instance->WarpType))
            instance->AllowEarlyCompletion = 1;

        WarpInfoUpdateHook.Original(instance);
    }

    private static void ForceCompleteWarp
    (
        WarpInfo* instance
    )
    {
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.NotifyTerritoryReady);
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.NotifyWarpComplete, 0, (uint)instance->WarpType);

        instance->CompleteWarp(0, 0);
        instance->CompletionDelay      = 0;
        instance->DelayCompletion      = 0;
        instance->AllowEarlyCompletion = 1;
    }

    #region 常量

    private static readonly WarpType[] InvalidWarpTypes =
    [
        WarpType.None,
        WarpType.Resurrection,
        WarpType.Revive,
        WarpType.EnterInstanceContent,
        WarpType.LeaveInstanceContent
    ];

    #endregion
}
