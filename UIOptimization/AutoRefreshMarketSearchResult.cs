using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRefreshMarketSearchResult : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRefreshMarketSearchResultTitle"),
        Description = GetLoc("AutoRefreshMarketSearchResultDescription"),
        Category    = ModuleCategories.UIOptimization,
    };

    private static bool IsMarketStuck { get; set; }
    
    [IPCProvider("DailyRoutines.Modules.AutoRefreshMarketSearchResult.IsMarketStuck")]
    public static bool IsCurrentMarketStuck => IsMarketStuck;
    
    private static readonly CompSig ProcessRequestResultSig =
        new("48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 0F B6 EA");
    private delegate nint                                ProcessRequestResultDelegate(InfoProxyItemSearch* info, int entryCount, nint a3, nint a4);
    private static   Hook<ProcessRequestResultDelegate>? ProcessRequestResultHook;

    private static readonly CompSig     WaitMessageSig   = new("BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0 BA ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 45 33 C9");
    private static readonly MemoryPatch WaitMessagePatch = new(WaitMessageSig.Get(), [0xBA, 0xB9, 0x1A, 0x00, 0x00]);

    protected override void Init()
    {
        ProcessRequestResultHook ??= ProcessRequestResultSig.GetHook<ProcessRequestResultDelegate>(ProcessRequestResultDetour);
        ProcessRequestResultHook.Enable();

        WaitMessagePatch.Set(true);
    }

    private static nint ProcessRequestResultDetour(InfoProxyItemSearch* info, int entryCount, nint a3, nint a4)
    {
        if (entryCount == 0 && a3 > 0 && 
            GameState.ContentFinderCondition == 0 && 
            info->SearchItemId != 0 && 
            LuminaGetter.TryGetRow<Item>(info->SearchItemId, out var itemData) &&
            itemData.ItemSearchCategory.RowId > 0)
        {
            IsMarketStuck = true;

            info->RequestData();
            return nint.Zero;
        }

        IsMarketStuck = false;
        return ProcessRequestResultHook.Original(info, entryCount, a3, a4);
    }

    protected override void Uninit()
    {
        WaitMessagePatch.Dispose();
        
        base.Uninit();
    }
}
