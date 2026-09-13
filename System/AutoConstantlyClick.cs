using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoConstantlyClick : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoConstantlyClickTitle"),
        Description = Lang.Get("AutoConstantlyClickDescription"),
        Category    = ModuleCategory.System,
        Author      = ["AtmoOmen", "KirisameVanilla"]
    };

    private static readonly CompSig CheckHotbarClickedSig = 
        new("E8 ?? ?? ?? ?? 48 8B 4F ?? 48 8B 01 FF 50 ?? 48 8B C8 E8 ?? ?? ?? ?? 84 C0 74");
    private delegate void CheckHotbarClickedDelegate
    (
        nint a1,
        byte a2
    );
    private static Hook<CheckHotbarClickedDelegate>? CheckHotbarClickedHook;

    private Config config = null!;

    private readonly HeldInfo[] inputIDInfos = new HeldInfo[MAX_KEY + 1];

    private int  runningTimersCount;
    private bool isHandlingHotbarClick;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        for (var i = 0; i <= MAX_KEY; i++)
            inputIDInfos[i] = new HeldInfo();

        CheckHotbarClickedHook ??= CheckHotbarClickedSig.GetHook<CheckHotbarClickedDelegate>(CheckHotbarClickedDetour);

        InputIDManager.Instance().RegPrePressed(OnPrePressed);

        UpdateHookState();
    }

    protected override void Uninit() =>
        InputIDManager.Instance().UnregPrePressed(OnPrePressed);

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.SliderInt($"{Lang.Get("Interval")}（ms）##Throttle Time", ref config.RepeatInterval, 100, 1000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.NewLine();

        var changed = false;

        changed |= ImGui.Checkbox(Lang.Get("AutoConstantlyClick-MouseMode"), ref config.MouseMode);
        changed |= ImGui.Checkbox(Lang.Get("AutoConstantlyClick-GamepadMode"), ref config.GamepadMode);

        if (changed)
        {
            config.Save(this);
            UpdateHookState();
        }
    }

    private void UpdateHookState()
    {
        if (config.MouseMode || config.GamepadMode)
            CheckHotbarClickedHook.Enable();
        else
            CheckHotbarClickedHook.Disable();
    }

    private void OnPrePressed
    (
        ref bool?   overrideResult,
        ref InputId key
    )
    {
        if (!isHandlingHotbarClick) return;
        if (key is not (>= InputId.HOTBAR_UP and <= InputId.HOTBAR_CONTENTS_ACT_R)) return;

        var info = inputIDInfos[(int)key];

        var isClicked = InputIDManager.Instance().IsInputIDPressed(key);
        var isPressed = InputIDManager.Instance().IsInputIDDown(key);
        overrideResult = info.GetIsReady(this) ?
                             isPressed :
                             isClicked;

        if (overrideResult.Value)
            info.RestartLastPress(this);
        else if (isPressed != info.LastFrameHeld)
        {
            if (isPressed && runningTimersCount > 0)
                info.RestartLastPress(this);
            else
                info.ResetLastPress(this);
        }

        info.LastFrameHeld    = isPressed;
        info.LastFramePressed = isClicked;
    }

    private void CheckHotbarClickedDetour
    (
        nint a1,
        byte a2
    )
    {
        isHandlingHotbarClick = true;

        try
        {
            CheckHotbarClickedHook.Original(a1, a2);
        }
        finally
        {
            isHandlingHotbarClick = false;
        }
    }

    private class HeldInfo
    {
        public SimpleTimer LastPress        { get; } = new();
        public bool        LastFramePressed { get; set; }
        public bool        LastFrameHeld    { get; set; }

        public bool GetIsReady
        (
            AutoConstantlyClick module
        ) =>
            LastPress.IsRunning && LastPress.ElapsedMilliseconds >= module.config.RepeatInterval;

        public void RestartLastPress
        (
            AutoConstantlyClick module
        )
        {
            if (!LastPress.IsRunning)
                Interlocked.Increment(ref module.runningTimersCount);
            LastPress.Restart();
        }

        public void ResetLastPress
        (
            AutoConstantlyClick module
        )
        {
            if (LastPress.IsRunning)
                Interlocked.Decrement(ref module.runningTimersCount);
            LastPress.Reset();
        }
    }

    private class SimpleTimer
    {
        private long startTime;

        public bool IsRunning { get; private set; }

        public long ElapsedMilliseconds => IsRunning ?
                                               Environment.TickCount64 - startTime :
                                               0;

        public void Restart()
        {
            startTime = Environment.TickCount64;
            IsRunning = true;
        }

        public void Reset()
        {
            startTime = 0;
            IsRunning = false;
        }
    }

    private class Config : ModuleConfig
    {
        public bool GamepadMode;
        public bool MouseMode      = true;
        public int  RepeatInterval = 200;
    }

    #region 常量

    private const int MAX_KEY = 512;

    #endregion
}
