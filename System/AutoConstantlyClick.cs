using System.Collections.Frozen;
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

    private static readonly CompSig               GamepadPollSig = new("40 55 53 57 41 57 48 8D AC 24 58 FC FF FF");
    private delegate        int                   ControllerPoll(nint controllerInput);
    private static          Hook<ControllerPoll>? GamepadPollHook;

    private static readonly CompSig CheckHotbarClickedSig = new("E8 ?? ?? ?? ?? 48 8B 4F ?? 48 8B 01 FF 50 ?? 48 8B C8 E8 ?? ?? ?? ?? 84 C0 74");
    private delegate        void    CheckHotbarClickedDelegate(nint a1, byte a2);
    private static          Hook<CheckHotbarClickedDelegate>? CheckHotbarClickedHook;

    private Config config = null!;

    private readonly HeldInfo[] inputIDInfos = new HeldInfo[MAX_KEY + 1];
    
    private long throttleTime = Environment.TickCount64;
    private int  runningTimersCount;
    private bool isHandlingHotbarClick;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        for (var i = 0; i <= MAX_KEY; i++)
            inputIDInfos[i] = new HeldInfo();

        CheckHotbarClickedHook ??= CheckHotbarClickedSig.GetHook<CheckHotbarClickedDelegate>(CheckHotbarClickedDetour);
        GamepadPollHook        ??= GamepadPollSig.GetHook<ControllerPoll>(GamepadPollDetour);

        InputIDManager.Instance().RegPrePressed(OnPrePressed);

        if (config.MouseMode)
            CheckHotbarClickedHook.Enable();
        if (config.GamepadMode)
            GamepadPollHook.Enable();
    }
    
    protected override void Uninit() =>
        InputIDManager.Instance().UnregPrePressed(OnPrePressed);
    
    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Lang.Get("Interval")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.SliderInt("(ms)##Throttle Time", ref config.RepeatInterval, 100, 1000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.Spacing();

        if (ImGui.Checkbox(Lang.Get("AutoConstantlyClick-MouseMode"), ref config.MouseMode))
        {
            config.Save(this);
            if (config.MouseMode)
                CheckHotbarClickedHook.Enable();
            else
                CheckHotbarClickedHook.Disable();
        }

        if (ImGui.Checkbox(Lang.Get("AutoConstantlyClick-GamepadMode"), ref config.GamepadMode))
        {
            config.Save(this);
            if (config.GamepadMode)
                GamepadPollHook.Enable();
            else
                GamepadPollHook.Disable();
        }

        if (config.GamepadMode)
        {
            ImGui.SetNextItemWidth(80f * GlobalUIScale);
            using var combo = ImRaii.Combo
            (
                $"{Lang.Get("AutoConstantlyClick-GamepadTriggers")}##GlobalConflictHotkeyGamepad",
                config.GamepadModeTriggerButtons.ToString()
            );

            if (combo)
            {
                foreach (var button in Triggers)
                {
                    if (ImGui.Selectable(button.ToString(), config.GamepadModeTriggerButtons.HasFlag(button)))
                    {
                        if (config.GamepadModeTriggerButtons.HasFlag(button))
                            config.GamepadModeTriggerButtons &= ~button;
                        else
                            config.GamepadModeTriggerButtons |= button;
                        config.Save(this);
                    }
                }
            }
        }
    }
    
    private int GamepadPollDetour(nint gamepadInput)
    {
        var input = (PadDevice*)gamepadInput;

        if (DService.Instance().Gamepad.Raw(config.GamepadModeTriggerButtons) == 1)
        {
            foreach (var btn in Enum.GetValues<GamepadButtons>())
            {
                if (DService.Instance().Gamepad.Raw(btn) == 1)
                {
                    if (Environment.TickCount64 >= throttleTime)
                    {
                        throttleTime                    =  Environment.TickCount64 + config.RepeatInterval;
                        input->GamepadInputData.Buttons -= (ushort)btn;
                    }
                }
            }
        }

        return GamepadPollHook.Original((nint)input);
    }

    private void OnPrePressed(ref bool? overrideResult, ref InputId key)
    {
        if (!isHandlingHotbarClick) return;
        if (key is not (>= InputId.HOTBAR_UP and <= InputId.HOTBAR_CONTENTS_ACT_R)) return;

        var info = inputIDInfos[(int)key];

        var isClicked = InputIDManager.Instance().IsInputIDPressed(key);
        var isPressed = InputIDManager.Instance().IsInputIDDown(key);
        overrideResult = info.GetIsReady(this) ? isPressed : isClicked;

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

    private void CheckHotbarClickedDetour(nint a1, byte a2)
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

        public bool GetIsReady(AutoConstantlyClick module) => 
            LastPress.IsRunning && LastPress.ElapsedMilliseconds >= module.config.RepeatInterval;

        public void RestartLastPress(AutoConstantlyClick module)
        {
            if (!LastPress.IsRunning)
                Interlocked.Increment(ref module.runningTimersCount);
            LastPress.Restart();
        }

        public void ResetLastPress(AutoConstantlyClick module)
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
    
    #region 常量

    private const int MAX_KEY = 512;

    private static readonly FrozenSet<GamepadButtons> Triggers = [GamepadButtons.L2, GamepadButtons.R2];

    #endregion
}
