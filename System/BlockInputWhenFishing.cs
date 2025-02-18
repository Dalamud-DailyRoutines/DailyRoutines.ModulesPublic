using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.Modules;

public unsafe class BlockInputWhenFishing : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("BlockInputWhenFishingTitle"),
        Description = GetLoc("BlockInputWhenFishingDescription"),
        Category = ModuleCategories.System,
    };

    private static readonly CompSig IsKeyDownSig =
        new("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 84 C0 0F 84");
    private delegate bool IsKeyDownDelegate(UIInputData* data, int id);
    private static Hook<IsKeyDownDelegate>? IsKeyDownHook;

    public override void Init()
    {
        IsKeyDownHook ??= DService.Hook.HookFromSignature<IsKeyDownDelegate>(IsKeyDownSig.Get(), IsKeyDownDetour);
        DService.Condition.ConditionChange += OnConditionChanged;

        if (DService.Condition[ConditionFlag.Gathering]) IsKeyDownHook.Enable();
    }

    public override void ConfigUI()
    {
        ConflictKeyText();
    }

    private static void OnConditionChanged(ConditionFlag flag, bool isSet)
    {
        if (flag != ConditionFlag.Gathering) return;

        if (isSet) IsKeyDownHook.Enable();
        else IsKeyDownHook.Disable();
    }

    private static bool IsKeyDownDetour(UIInputData* data, int id) 
        => IsConflictKeyPressed() && IsKeyDownHook.Original(data, id);

    public override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;

        base.Uninit();
    }
}
