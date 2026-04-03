using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.GamePad;
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
    
    private const int MAX_KEY = 512;

    private static readonly GamepadButtons[] Triggers = [GamepadButtons.L2, GamepadButtons.R2];

    private static readonly CompSig               GamepadPollSig = new("40 55 53 57 41 57 48 8D AC 24 58 FC FF FF");
    private static          Hook<ControllerPoll>? GamepadPollHook;

    private static readonly CompSig CheckHotbarClickedSig = new("E8 ?? ?? ?? ?? 48 8B 4F ?? 48 8B 01 FF 50 ?? 48 8B C8 E8 ?? ?? ?? ?? 84 C0 74");
    private static          Hook<CheckHotbarClickedDelegate>? CheckHotbarClickedHook;

    private static Config ModuleConfig = null!;

    private static readonly HeldInfo[] InputIDInfos = new HeldInfo[MAX_KEY + 1];
    private static          long       ThrottleTime = Environment.TickCount64;
    private static          int        RunningTimersCount;
    private static          bool       IsHandlingHotbarClick;
    
    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        for (var i = 0; i <= MAX_KEY; i++)
            InputIDInfos[i] = new HeldInfo();

        CheckHotbarClickedHook ??= CheckHotbarClickedSig.GetHook<CheckHotbarClickedDelegate>(CheckHotbarClickedDetour);
        GamepadPollHook        ??= GamepadPollSig.GetHook<ControllerPoll>(GamepadPollDetour);

        InputIDManager.Instance().RegPrePressed(OnPrePressed);

        if (ModuleConfig.MouseMode)
            CheckHotbarClickedHook.Enable();
        if (ModuleConfig.GamepadMode)
            GamepadPollHook.Enable();
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Lang.Get("Interval")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.SliderInt("(ms)##Throttle Time", ref ModuleConfig.RepeatInterval, 100, 1000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            ModuleConfig.Save(this);

        ImGui.Spacing();

        if (ImGui.Checkbox(Lang.Get("AutoConstantlyClick-MouseMode"), ref ModuleConfig.MouseMode))
        {
            ModuleConfig.Save(this);
            if (ModuleConfig.MouseMode)
                CheckHotbarClickedHook.Enable();
            else
                CheckHotbarClickedHook.Disable();
        }

        if (ImGui.Checkbox(Lang.Get("AutoConstantlyClick-GamepadMode"), ref ModuleConfig.GamepadMode))
        {
            ModuleConfig.Save(this);
            if (ModuleConfig.GamepadMode)
                GamepadPollHook.Enable();
            else
                GamepadPollHook.Disable();
        }

        if (ModuleConfig.GamepadMode)
        {
            ImGui.SetNextItemWidth(80f * GlobalUIScale);
            using var combo = ImRaii.Combo
            (
                $"{Lang.Get("AutoConstantlyClick-GamepadTriggers")}##GlobalConflictHotkeyGamepad",
                ModuleConfig.GamepadModeTriggerButtons.ToString()
            );

            if (combo)
            {
                foreach (var button in Triggers)
                {
                    if (ImGui.Selectable(button.ToString(), ModuleConfig.GamepadModeTriggerButtons.HasFlag(button)))
                    {
                        if (ModuleConfig.GamepadModeTriggerButtons.HasFlag(button))
                            ModuleConfig.GamepadModeTriggerButtons &= ~button;
                        else
                            ModuleConfig.GamepadModeTriggerButtons |= button;
                        ModuleConfig.Save(this);
                    }
                }
            }
        }
    }

    protected override void Uninit() =>
        InputIDManager.Instance().UnregPrePressed(OnPrePressed);

    private static int GamepadPollDetour(nint gamepadInput)
    {
        var input = (PadDevice*)gamepadInput;

        if (DService.Instance().Gamepad.Raw(ModuleConfig.GamepadModeTriggerButtons) == 1)
        {
            foreach (var btn in Enum.GetValues<GamepadButtons>())
            {
                if (DService.Instance().Gamepad.Raw(btn) == 1)
                {
                    if (Environment.TickCount64 >= ThrottleTime)
                    {
                        ThrottleTime                    =  Environment.TickCount64 + ModuleConfig.RepeatInterval;
                        input->GamepadInputData.Buttons -= (ushort)btn;
                    }
                }
            }
        }

        return GamepadPollHook.Original((nint)input);
    }

    private static void OnPrePressed(ref bool? overrideResult, ref InputId key)
    {
        if (!IsHandlingHotbarClick) return;
        if (key is not (>= InputId.HOTBAR_UP and <= InputId.HOTBAR_CONTENTS_ACT_R)) return;

        var info = InputIDInfos[(int)key];

        var isClicked = InputIDManager.Instance().IsInputIDPressed(key);
        var isPressed = InputIDManager.Instance().IsInputIDDown(key);
        overrideResult = info.IsReady ? isPressed : isClicked;

        if (overrideResult.Value)
            info.RestartLastPress();
        else if (isPressed != info.LastFrameHeld)
        {
            if (isPressed && RunningTimersCount > 0)
                info.RestartLastPress();
            else
                info.ResetLastPress();
        }

        info.LastFrameHeld    = isPressed;
        info.LastFramePressed = isClicked;
    }

    private static void CheckHotbarClickedDetour(nint a1, byte a2)
    {
        IsHandlingHotbarClick = true;
        try
        {
            CheckHotbarClickedHook.Original(a1, a2);
        }
        finally
        {
            IsHandlingHotbarClick = false;
        }
    }

    private delegate int ControllerPoll(nint controllerInput);

    private delegate void CheckHotbarClickedDelegate(nint a1, byte a2);

    private class HeldInfo
    {
        public SimpleTimer LastPress        { get; } = new();
        public bool        LastFramePressed { get; set; }
        public bool        LastFrameHeld    { get; set; }

        public bool IsReady => LastPress.IsRunning && LastPress.ElapsedMilliseconds >= ModuleConfig.RepeatInterval;

        public void RestartLastPress()
        {
            if (!LastPress.IsRunning)
                Interlocked.Increment(ref RunningTimersCount);
            LastPress.Restart();
        }

        public void ResetLastPress()
        {
            if (LastPress.IsRunning)
                Interlocked.Decrement(ref RunningTimersCount);
            LastPress.Reset();
        }
    }

    private class SimpleTimer
    {
        private long startTime;

        public bool IsRunning { get; private set; }

        public long ElapsedMilliseconds => IsRunning ? Environment.TickCount64 - startTime : 0;

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
        public bool           GamepadMode;
        public GamepadButtons GamepadModeTriggerButtons = GamepadButtons.L2 | GamepadButtons.R2;
        public bool           MouseMode                 = true;
        public int            RepeatInterval            = 200;
    }
}
