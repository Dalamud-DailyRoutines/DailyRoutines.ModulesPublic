using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.Modules;

public unsafe class AutoWoof : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoWoofTitle"),
        Description = GetLoc("AutoWoofDescription"),
        Category = ModuleCategories.General,
        Author = ["逆光"]
    };

    public override void Init()
    {
        FrameworkManager.Register(true, OnUpdate);
    }

    private static void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (!Throttler.Throttle("AutoWoof-OnUpdate", 1_000)) return;

        if (DService.ClientState.LocalPlayer is not { } localPlayer) return;
        if (!DService.Condition[ConditionFlag.Mounted] || localPlayer.CurrentMount?.RowId != 294) return;
        if (ActionManager.Instance()->GetActionStatus(ActionType.Action, 29463) != 0) return;

        UseActionManager.UseAction(ActionType.Action, 29463);
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
    }
}
