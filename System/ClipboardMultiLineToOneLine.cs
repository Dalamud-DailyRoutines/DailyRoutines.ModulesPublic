using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using System.Windows.Forms;

namespace DailyRoutines.Modules;

public unsafe class ClipboardMultiLineToOneLine : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("ClipboardMultiLineToOneLineTitle"),
        Description = GetLoc("ClipboardMultiLineToOneLineDescription"),
        Category = ModuleCategories.System,
    };

    private static readonly CompSig GetClipboardDataSig =
        new("40 53 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B D9 BA");
    private delegate Utf8String* GetClipboardDataDelegate(nint a1);
    private static Hook<GetClipboardDataDelegate>? GetClipboardDataHook;

    private static readonly string[] BlacklistAddons = ["Macro"];
    private static bool IsBlocked;

    public override void Init()
    {
        
        GetClipboardDataHook ??= DService.Hook.HookFromSignature<GetClipboardDataDelegate>(
            GetClipboardDataSig.Get(), GetClipboardDataDetour);
        GetClipboardDataHook.Enable();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, BlacklistAddons, OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, BlacklistAddons, OnAddon);
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        IsBlocked = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => IsBlocked,
        };
    }

    private static Utf8String* GetClipboardDataDetour(nint a1)
    {
        if (IsBlocked || Framework.Instance()->WindowInactive) return GetClipboardDataHook.Original(a1);

        var copyModule = Framework.Instance()->GetUIClipboard();
        if (copyModule == null) return GetClipboardDataHook.Original(a1);

        var originalText = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(originalText)) return GetClipboardDataHook.Original(a1);

        var modifiedText = originalText.Replace("\r\n", " ").Replace("\n", " ").Replace("\u000D", " ")
                                       .Replace("\u000D\u000A", " ");

        if (modifiedText == originalText) return GetClipboardDataHook.Original(a1);

        return Utf8String.FromString(modifiedText);
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        base.Uninit();
    }
}
