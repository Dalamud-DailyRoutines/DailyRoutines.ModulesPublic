using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class BlockInputWhenFishing : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BlockInputWhenFishingTitle"),
        Description = Lang.Get("BlockInputWhenFishingDescription"),
        Category    = ModuleCategory.System
    };
    
    private static readonly CompSig IsKeyDownSig =
        new("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 84 C0 0F 84");
    private delegate bool                     IsKeyDownDelegate(UIInputData* data, int id);
    private          Hook<IsKeyDownDelegate>? IsKeyDownHook;

    protected override void Init()
    {
        IsKeyDownHook                                 ??= DService.Instance().Hook.HookFromSignature<IsKeyDownDelegate>(IsKeyDownSig.Get(), IsKeyDownDetour);
        DService.Instance().Condition.ConditionChange +=  OnConditionChanged;

        if (DService.Instance().Condition[ConditionFlag.Gathering])
            IsKeyDownHook.Enable();
    }

    protected override void ConfigUI() => ImGuiOm.ConflictKeyText();

    private void OnConditionChanged(ConditionFlag flag, bool isSet)
    {
        if (flag != ConditionFlag.Gathering) return;

        if (isSet)
            IsKeyDownHook.Enable();
        else
            IsKeyDownHook.Disable();
    }

    private bool IsKeyDownDetour(UIInputData* data, int id) =>
        PluginConfig.Instance().ConflictKeyBinding.IsPressed() && IsKeyDownHook.Original(data, id);

    protected override void Uninit() =>
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
}
