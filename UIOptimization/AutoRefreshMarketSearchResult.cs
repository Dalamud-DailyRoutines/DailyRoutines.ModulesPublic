using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DailyRoutines.Modules;

public unsafe class AutoRefreshMarketSearchResult : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoRefreshMarketSearchResultTitle"),
        Description = GetLoc("AutoRefreshMarketSearchResultDescription"),
        Category = ModuleCategories.UIOptimization,
    };

    private static readonly CompSig HandlePricesSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 0F B6 EA");
    private delegate nint HandlePricesDelegate(InfoProxyItemSearch* infoProxy, void* unk1, void* unk2);
    private static Hook<HandlePricesDelegate>? HandlePricesHook;

    private static readonly CompSig WaitMessageSig =
        new("BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0 BA ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 45 33 C9");
    private static readonly MemoryPatch WaitMessagePatch = new(WaitMessageSig.Get(), [0xBA, 0xB9, 0x1A, 0x00, 0x00]);

    public override void Init()
    {
        HandlePricesHook ??= DService.Hook.HookFromSignature<HandlePricesDelegate>(HandlePricesSig.Get(), HandlePricesDetour);
        HandlePricesHook.Enable();

        WaitMessagePatch.Set(true);
        TaskHelper ??= new TaskHelper { AbortOnTimeout = true, TimeLimitMS = 5000, ShowDebug = false };
    }

    private nint HandlePricesDetour(InfoProxyItemSearch* infoProxy, void* unk1, void* unk2)
    {
        var result = HandlePricesHook.Original.Invoke(infoProxy, unk1, unk2);
        if (result != 1) TaskHelper.Enqueue(RefreshPrices);

        return result;
    }

    private static void RefreshPrices()
    {
        if (!TryGetAddonByName<AddonItemSearchResult>("ItemSearchResult", out var addonItemSearchResult)) return;
        if (!AddonItemSearchResultThrottled(addonItemSearchResult)) return;
        InfoProxyItemSearch.Instance()->RequestData();
    }

    private static bool AddonItemSearchResultThrottled(AddonItemSearchResult* addon) =>
        addon != null
        && addon->ErrorMessage != null
        && addon->ErrorMessage->AtkResNode.IsVisible()
        && addon->HitsMessage != null
        && !addon->HitsMessage->AtkResNode.IsVisible();

    public override void Uninit()
    {
        WaitMessagePatch.Dispose();
        base.Uninit();
    }
}
