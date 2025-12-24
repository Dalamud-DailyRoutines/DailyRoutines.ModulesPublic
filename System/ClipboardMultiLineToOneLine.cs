using System.Linq;
using System.Windows.Forms;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public unsafe class ClipboardMultiLineToOneLine : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ClipboardMultiLineToOneLineTitle"),
        Description = GetLoc("ClipboardMultiLineToOneLineDescription"),
        Category    = ModuleCategories.System
    };

    private static readonly CompSig GetClipboardDataSig = new("40 53 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F1 BA");
    private delegate        Utf8String* GetClipboardDataDelegate(ClipBoard* clipBoard);
    private static          Hook<GetClipboardDataDelegate>? GetClipboardDataHook;

    private static readonly string[] BlacklistAddons = ["Macro"];

    protected override void Init()
    {
        GetClipboardDataHook ??= GetClipboardDataSig.GetHook<GetClipboardDataDelegate>(GetClipboardDataDetour);
        GetClipboardDataHook.Enable();
    }

    private static Utf8String* GetClipboardDataDetour(ClipBoard* clipBoard)
    {
        if (Framework.Instance()->WindowInactive || IsAnyBlacklistAddonFocused()) return InvokeOriginal();
        
        var clipboardText = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(clipboardText)) 
            return InvokeOriginal();

        var modifiedText = clipboardText.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        if (modifiedText == clipboardText) 
            return InvokeOriginal();

        var dest = &clipBoard->SystemClipboardText;

        clipBoard->SystemClipboardText.Clear();
        clipBoard->SystemClipboardText.SetString(modifiedText);
        
        return dest;
        
        Utf8String* InvokeOriginal() => 
            GetClipboardDataHook.Original(clipBoard);
    }

    private static bool IsAnyBlacklistAddonFocused()
        => RaptureAtkModule.Instance()->RaptureAtkUnitManager.FocusedUnitsList.Entries
                                                             .ToArray()
                                                             .Where(x => x.Value != null)
                                                             .Select(x => x.Value->NameString)
                                                             .Where(x => !string.IsNullOrWhiteSpace(x))
                                                             .ContainsAny(BlacklistAddons);
}
