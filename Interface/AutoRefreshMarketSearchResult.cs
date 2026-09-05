using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoRefreshMarketSearchResult : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRefreshMarketSearchResultTitle"),
        Description = Lang.Get("AutoRefreshMarketSearchResultDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig     WaitMessageSig   = new("BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0 BA ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 45 33 C9");
    private                 MemoryPatch waitMessagePatch = null!;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 2000 };
        
        waitMessagePatch = new(WaitMessageSig.Get(), [0xBA, 0xB9, 0x1A, 0x00, 0x00]);
        waitMessagePatch.Enable();
        
        GameState.Instance().MarketListingsStuck += OnMarketListingsStuck;
    }

    protected override void Uninit() =>
        GameState.Instance().MarketListingsStuck -= OnMarketListingsStuck;

    private void OnMarketListingsStuck
    (
        int errorCode
    )
    {
        TaskHelper.Abort();
        TaskHelper.DelayNext(500, 1000);
        TaskHelper.Enqueue(() => InfoProxyItemSearch.Instance()->RequestData());
    }
}
