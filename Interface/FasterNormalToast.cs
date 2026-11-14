using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Addon;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;

namespace DailyRoutines.ModulesPublic;

internal unsafe class FasterNormalToast : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FasterNormalToastTitle"),
        Description = Lang.Get("FasterNormalToastDescription"),
        Category    = ModuleCategory.Interface
    };

    public override string ModuleName => nameof(FasterNormalToast);

    private Hook<AddonWideText.Delegates.ShowMessage>  ShowMessageHook;
    private Hook<AddonWideText.Delegates.StartMessage> StartMessageHook;

    protected override void Init()
    {
        ShowMessageHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AddonWideText.MemberFunctionPointers),
            nameof(AddonWideText.ShowMessage),
            (AddonWideText.Delegates.ShowMessage)ShowMessageDetour
        );
        StartMessageHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AddonWideText.MemberFunctionPointers),
            nameof(AddonWideText.StartMessage),
            (AddonWideText.Delegates.StartMessage)StartMessageDetour
        );

        StartMessageHook.Enable();
        ShowMessageHook.Enable();
    }

    private void ShowMessageDetour(AddonWideText* addon, uint valueCount, AtkValue* values)
    {
        ShowMessageHook.Original(addon, valueCount, values);

        if (addon->IsPlaying && addon->PendingMessageCount > 0)
            AccelerateMessage(addon);
    }

    private void StartMessageDetour(AddonWideText* addon, CStringPointer text, int isSlow)
    {
        StartMessageHook.Original(addon, text, isSlow);

        if (addon->PendingMessageCount > 1)
            AccelerateMessage(addon);
    }

    private static void AccelerateMessage(AddonWideText* addon)
    {
        var node = addon->TimelineNode;
        if (node           == null ||
            node->Timeline == null ||
            node->Timeline->ActiveLabelId is FAST_FADE_LABEL or FINISHED_LABEL)
            return;

        node->Timeline->PlayAnimation(AtkTimelineJumpBehavior.LoopForever, FAST_FADE_LABEL);
    }

    #region 常量

    private const ushort FAST_FADE_LABEL = 101;
    private const ushort FINISHED_LABEL  = 102;

    #endregion
}
