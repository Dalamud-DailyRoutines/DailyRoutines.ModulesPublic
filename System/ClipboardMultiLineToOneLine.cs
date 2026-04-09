using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class ClipboardMultiLineToOneLine : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ClipboardMultiLineToOneLineTitle"),
        Description = Lang.Get("ClipboardMultiLineToOneLineDescription"),
        Category    = ModuleCategory.System
    };
    
    private static readonly CompSig     GetClipboardDataSig = new("40 53 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F1 BA");
    private delegate        Utf8String* GetClipboardDataDelegate(ClipBoard* clipBoard);
    private                 Hook<GetClipboardDataDelegate>? GetClipboardDataHook;
    
    protected override void Init()
    {
        GetClipboardDataHook ??= GetClipboardDataSig.GetHook<GetClipboardDataDelegate>(GetClipboardDataDetour);
        GetClipboardDataHook.Enable();
    }

    private Utf8String* GetClipboardDataDetour(ClipBoard* clipBoard)
    {
        if (Framework.Instance()->WindowInactive                          ||
            !AtkComponentTextInput.TryGetActive(out var component, out _))
            return InvokeOriginal();

        var clipboardText = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(clipboardText))
            return InvokeOriginal();
        
        var modifiedText = clipboardText;
        
        if (component->ComponentTextData.MaxLine > 1 ||
            component->ComponentTextData.Flags2.IsSet(TextInputFlags2.MultiLine))
            modifiedText = clipboardText.Replace("\r\n", "\r").Replace("\n", "\r");
        else
            modifiedText = clipboardText.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

        if (modifiedText == clipboardText)
            return InvokeOriginal();

        var dest = &clipBoard->SystemClipboardText;

        clipBoard->SystemClipboardText.Clear();
        clipBoard->SystemClipboardText.SetString(modifiedText);

        return dest;

        Utf8String* InvokeOriginal() =>
            GetClipboardDataHook.Original(clipBoard);
    }
}
