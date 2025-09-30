using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRefocus : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRefocusTitle"),
        Description = GetLoc("AutoRefocusDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static readonly CompSig                                 SetFocusTargetByObjectIDSig = new("E8 ?? ?? ?? ?? BA 0C 00 00 00 48 8D 0D");
    private delegate        void                                    SetFocusTargetByObjectIDDelegate(TargetSystem* targetSystem, ulong objectID);
    private static          Hook<SetFocusTargetByObjectIDDelegate>? SetFocusTargetByObjectIDHook;

    private static ulong FocusTarget;
    private static bool  IsNeedToRefocus;

    protected override void Init()
    {
        SetFocusTargetByObjectIDHook ??= SetFocusTargetByObjectIDSig.GetHook<SetFocusTargetByObjectIDDelegate>(SetFocusTargetByObjectIDDetour);
        SetFocusTargetByObjectIDHook.Enable();

        if (BoundByDuty) 
            OnZoneChange(DService.ClientState.TerritoryType);
        DService.ClientState.TerritoryChanged += OnZoneChange;
        
        FrameworkManager.Register(OnUpdate, throttleMS: 1000);
    }

    private static void OnZoneChange(ushort territory)
    {
        FocusTarget = 0;
        IsNeedToRefocus = GameState.ContentFinderCondition > 0;
    }

    private static void OnUpdate(IFramework framework)
    {
        if (!IsNeedToRefocus || FocusTarget == 0 || FocusTarget == 0xE000_0000) return;

        if (DService.Targets.FocusTarget == null)
            SetFocusTargetByObjectIDHook.Original(TargetSystem.Instance(), FocusTarget);
    }

    private static void SetFocusTargetByObjectIDDetour(TargetSystem* targetSystem, ulong objectID)
    {
        FocusTarget = objectID;
        SetFocusTargetByObjectIDHook.Original(targetSystem, objectID);
    }

    protected override void Uninit() => 
        DService.ClientState.TerritoryChanged -= OnZoneChange;
}
