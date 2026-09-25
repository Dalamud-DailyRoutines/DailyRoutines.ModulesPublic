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
    private Hook<CheckHotbarClickedDelegate>? CheckHotbarClickedHook;

    private static readonly CompSig CheckCrossHotbarClickedSig = 
        new("89 54 24 ?? 48 89 4C 24 ?? 56 41 55");
    private delegate void CheckCrossHotbarClickedDelegate
    (
        nint a1,
        int  a2
    );
    private Hook<CheckCrossHotbarClickedDelegate>? CheckCrossHotbarClickedHook;

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
        CheckCrossHotbarClickedHook ??= CheckCrossHotbarClickedSig.GetHook<CheckCrossHotbarClickedDelegate>(CheckCrossHotbarClickedDetour);

        var inputIDManager = InputIDManager.Instance();
        inputIDManager.RegPrePressed(OnPrePressed);
        inputIDManager.RegPreHeld(OnPrePressed);

        UpdateHookState();
    }

    protected override void Uninit()
    {
        var inputIDManager = InputIDManager.Instance();
        inputIDManager.UnregPrePressed(OnPrePressed);
        inputIDManager.UnregPreHeld(OnPrePressed);
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.SliderInt($"{Lang.Get("Interval")}（ms）##Throttle Time", ref config.RepeatInterval, 100, 1000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.SliderInt($"{Lang.Get("AutoConstantlyClick-WaitTime")}（ms）##WaitTime", ref config.WaitTime, 0, 1000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoConstantlyClick-WaitTime-Help"));

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
        {
            CheckHotbarClickedHook.Enable();
            CheckCrossHotbarClickedHook.Enable();
        }
        else
        {
            CheckHotbarClickedHook.Disable();
            CheckCrossHotbarClickedHook.Disable();
        }
    }

    private void OnPrePressed
    (
        ref bool?   overrideResult,
        ref InputId key
    )
    {
        if (!isHandlingHotbarClick) return;
        if (key is not (>= InputId.HOTBAR_UP and <= InputId.HOTBAR_CONTENTS_ACT_R) and
            not (>= InputId.HOT_PAD_CONTENT and <= InputId.HOT_PAD_TOPAGE8)) return;

        var info = inputIDInfos[(int)key];

        var isClicked = InputIDManager.Instance().IsInputIDPressed(key);
        var isPressed = InputIDManager.Instance().IsInputIDDown(key);

        if (!info.IsWaiting && isPressed && !info.LastFrameHeld)
        {
            if (config.WaitTime > 0)
                info.WaitLastPress(this);
            else
                info.RestartLastPress(this);
        }

        overrideResult = info.IsWaiting || !info.GetIsReady(this) ?
                             isClicked :
                             isPressed;

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

    private void CheckCrossHotbarClickedDetour
    (
        nint a1,
        int  a2
    )
    {
        isHandlingHotbarClick = true;

        try
        {
            CheckCrossHotbarClickedHook.Original(a1, a2);
        }
        finally
        {
            isHandlingHotbarClick = false;
        }
    }

    private class HeldInfo
    {
        public SimpleTimer LastPress        { get; } = new();
        public SimpleTimer WaitPress        { get; } = new();
        public bool        LastFramePressed { get; set; }
        public bool        LastFrameHeld    { get; set; }

        public bool IsWaiting => WaitPress.IsRunning;

        public bool GetIsReady
        (
            AutoConstantlyClick module
        ) =>
            LastPress.IsRunning && LastPress.ElapsedMilliseconds >= module.config.RepeatInterval;

        public void WaitLastPress
        (
            AutoConstantlyClick module
        )
        {
            if (!WaitPress.IsRunning)
                Interlocked.Increment(ref module.runningTimersCount);
            WaitPress.Restart();
        }

        public void RestartLastPress
        (
            AutoConstantlyClick module
        )
        {
            if (!LastPress.IsRunning)
                Interlocked.Increment(ref module.runningTimersCount);
            LastPress.Restart();

            if (WaitPress.IsRunning)
                Interlocked.Decrement(ref module.runningTimersCount);
            WaitPress.Reset();
        }

        public void ResetLastPress
        (
            AutoConstantlyClick module
        )
        {
            if (LastPress.IsRunning)
                Interlocked.Decrement(ref module.runningTimersCount);
            LastPress.Reset();

            if (WaitPress.IsRunning)
                Interlocked.Decrement(ref module.runningTimersCount);
            WaitPress.Reset();
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
        public int  WaitTime;
    }

    #region 常量

    private const int MAX_KEY = 512;

    #endregion
}
