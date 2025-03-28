using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Linq;
using System.Windows.Forms;

namespace DailyRoutines.Modules;

public unsafe class ClipboardMultiLineToOneLine : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("ClipboardMultiLineToOneLineTitle"),
        Description = GetLoc("ClipboardMultiLineToOneLineDescription"),
        Category    = ModuleCategories.System,
    };

    private static readonly CompSig GetClipboardDataSig =
        new("40 53 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B D9 BA");
    private delegate Utf8String* GetClipboardDataDelegate(nint a1);
    private static Hook<GetClipboardDataDelegate>? GetClipboardDataHook;

    private static readonly string[] BlacklistAddons = ["Macro"];

    public override void Init()
    {
        GetClipboardDataHook ??= GetClipboardDataSig.GetHook<GetClipboardDataDelegate>(GetClipboardDataDetour);
        GetClipboardDataHook.Enable();
    }

    private static Utf8String* GetClipboardDataDetour(nint a1)
    {
        if (Framework.Instance()->WindowInactive || IsAnyBlacklistAddonFocused()) return InvokeOriginal();
        
        var clipboardText = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(clipboardText)) return InvokeOriginal();

        var modifiedText = clipboardText.Replace("\r\n", " ").Replace("\n", " ").Replace("\u000D", " ")
                                       .Replace("\u000D\u000A", " ");
        if (modifiedText == clipboardText) return InvokeOriginal();

        var dest = (Utf8String*)(a1 + 8);
        if (dest == null) return InvokeOriginal();

        var sour = Utf8String.FromString(modifiedText);
        
        dest->Clear();
        dest->Copy(sour);
        
        sour->Dtor(true);
        
        return dest;
        
        Utf8String* InvokeOriginal() => GetClipboardDataHook.Original(a1);
    }

    private static bool IsAnyBlacklistAddonFocused()
        => RaptureAtkModule.Instance()->RaptureAtkUnitManager.FocusedUnitsList.Entries
                                                             .ToArray()
                                                             .Where(x => x.Value != null)
                                                             .Select(x => x.Value->NameString)
                                                             .Where(x => !string.IsNullOrWhiteSpace(x))
                                                             .ContainsAny(BlacklistAddons);
}
