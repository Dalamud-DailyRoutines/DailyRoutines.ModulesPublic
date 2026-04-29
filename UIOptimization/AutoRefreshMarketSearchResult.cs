using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRefreshMarketSearchResult : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRefreshMarketSearchResultTitle"),
        Description = Lang.Get("AutoRefreshMarketSearchResultDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Hook<InfoProxyItemSearch.Delegates.ProcessRequestResult>? ProcessRequestResultHook;

    private static readonly CompSig     WaitMessageSig   = new("BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0 BA ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 45 33 C9");
    private readonly        MemoryPatch waitMessagePatch = new(WaitMessageSig.Get(), [0xBA, 0xB9, 0x1A, 0x00, 0x00]);

    private bool isMarketStuck;

    protected override void Init()
    {
        ProcessRequestResultHook = DService.Instance().Hook.HookFromMemberFunction
        (
            typeof(InfoProxyItemSearch.MemberFunctionPointers),
            "ProcessRequestResult",
            (InfoProxyItemSearch.Delegates.ProcessRequestResult)ProcessRequestResultDetour
        );
        ProcessRequestResultHook.Enable();

        waitMessagePatch.Set(true);
    }
    
    protected override void Uninit() =>
        waitMessagePatch.Dispose();

    // TODO: 需要验证参数情况
    private void ProcessRequestResultDetour(InfoProxyItemSearch* info, byte a2, int a3)
    {
        if (a2                       == 0                              &&
            a3                               > 0                               &&
            GameState.ContentFinderCondition == 0                              &&
            info->SearchItemId               != 0                              &&
            LuminaGetter.TryGetRow<Item>(info->SearchItemId, out var itemData) &&
            itemData.ItemSearchCategory.RowId > 0)
        {
            isMarketStuck = true;

            info->RequestData();
            return;
        }

        isMarketStuck = false;
        ProcessRequestResultHook.Original(info, a2, a3);
    }
    
    #region IPC

    [IPCProvider("DailyRoutines.Modules.AutoRefreshMarketSearchResult.IsMarketStuck")]
    private bool IsCurrentMarketStuck => isMarketStuck;

    #endregion
}
