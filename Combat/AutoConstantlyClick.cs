using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Threading;

namespace DailyRoutines.Modules;

public class AutoConstantlyClick : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoConstantlyClickTitle"),
        Description = GetLoc("AutoConstantlyClickDescription"),
        Category = ModuleCategories.Combat,
        Author = ["AtmoOmen", "KirisameVanilla"],
    };

    private const int MaxKey = 512;
    private static readonly HeldInfo[] InputIDInfos = new HeldInfo[MaxKey + 1];
    private static int runningTimersCount;

    private delegate bool IDKeyDelegate(nint data, int key);

    private static readonly CompSig IsIDKeyClickedSig = new("48 89 5C 24 ?? 56 41 56 41 57 48 83 EC ?? 48 63 C2");
    private static Hook<IDKeyDelegate>? IsIDKeyClickedHook;

    private static readonly CompSig IsIDKeyPressedSig = new("E8 ?? ?? ?? ?? 84 C0 75 ?? BA ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 4C 8B 05 ?? ?? ?? ?? 48 8D 4C 24 ?? 0F 29 BC 24");
    private static IDKeyDelegate? IsIDKeyPressed;

    private static readonly CompSig GamepadPollSig = new("40 55 53 57 41 54 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 44 0F 29 B4 24");
    private static Hook<ControllerPoll>? GamepadPollHook;
    private delegate int ControllerPoll(IntPtr controllerInput);

    private static readonly CompSig CheckHotbarClickedSig = new("48 89 4C 24 08 53 41 55 41 57");
    private delegate void CheckHotbarClickedDelegate(nint a1, byte a2);
    private static Hook<CheckHotbarClickedDelegate>? CheckHotbarClickedHook;

    private                 long             ThrottleTime { get; set; } = Environment.TickCount64;
    private static          Config           ModuleConfig = null!;
    private static readonly GamepadButtons[] Triggers     = [GamepadButtons.L2, GamepadButtons.R2];
    
    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        for (var i = 0; i <= MaxKey; i++)
            InputIDInfos[i] = new HeldInfo();

        IsIDKeyPressed = IsIDKeyPressedSig.GetDelegate<IDKeyDelegate>();

        IsIDKeyClickedHook ??= IsIDKeyClickedSig.GetHook<IDKeyDelegate>(IsIDKeyClickedDetour);

        CheckHotbarClickedHook ??= CheckHotbarClickedSig.GetHook<CheckHotbarClickedDelegate>(CheckHotbarClickedDetour);
        GamepadPollHook ??= GamepadPollSig.GetHook<ControllerPoll>(GamepadPollDetour);

        if (ModuleConfig.MouseMode) CheckHotbarClickedHook.Enable();
        if (ModuleConfig.GamepadMode) GamepadPollHook.Enable();
    }

    public override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("Interval")}:");
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.SliderInt("(ms)##Throttle Time", ref ModuleConfig.RepeatInterval, 100, 1000);
        if (ImGui.IsItemDeactivatedAfterEdit()) 
            ModuleConfig.Save(this);
        
        ImGui.Spacing();
        
        if (ImGui.Checkbox(GetLoc("AutoConstantlyClick-MouseMode"), ref ModuleConfig.MouseMode))
        {
            ModuleConfig.Save(this);
            if (ModuleConfig.MouseMode) CheckHotbarClickedHook.Enable();
            else CheckHotbarClickedHook.Disable();
        }

        if (ImGui.Checkbox(GetLoc("AutoConstantlyClick-GamepadMode"), ref ModuleConfig.GamepadMode))
        {
            ModuleConfig.Save(this);
            if (ModuleConfig.GamepadMode) GamepadPollHook.Enable();
            else GamepadPollHook.Disable();
        }

        if (ModuleConfig.GamepadMode)
        {
            ImGui.SetNextItemWidth(80f * GlobalFontScale);
            using var combo = ImRaii.Combo($"{GetLoc("AutoConstantlyClick-GamepadTriggers")}##GlobalConflictHotkeyGamepad",
                                           ModuleConfig.GamepadModeTriggerButtons.ToString());
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

    private unsafe int GamepadPollDetour(IntPtr gamepadInput)
    {
        var input = (GamepadInput*)gamepadInput;
        if (DService.Gamepad.Raw(ModuleConfig.GamepadModeTriggerButtons) == 1)
        {
            foreach (var btn in Enum.GetValues<GamepadButtons>())
            {
                if (DService.Gamepad.Raw(btn) == 1)
                {
                    if (Environment.TickCount64 >= ThrottleTime)
                    {
                        ThrottleTime = Environment.TickCount64 + ModuleConfig.RepeatInterval;
                        input->ButtonsRaw -= (ushort)btn;
                    }
                }
            }
        }

        return GamepadPollHook.Original((IntPtr)input);
    }

    private static bool IsIDKeyClickedDetour(nint data, int key)
    {
        if (key is not (>= 45 and <= 190)) return false;

        var info = InputIDInfos[key];

        var isClicked = IsIDKeyClickedHook.Original(data, key);
        var isPressed = IsIDKeyPressed(data, key);
        var orig = info.IsReady ? isPressed : isClicked;

        if (orig)
        {
            info.RestartLastPress();
        }
        else if (isPressed != info.LastFrameHeld)
        {
            if (isPressed && runningTimersCount > 0)
                info.RestartLastPress();
            else
                info.ResetLastPress();
        }

        info.LastFrameHeld = isPressed;
        info.LastFramePressed = isClicked;
        return orig;
    }

    private static void CheckHotbarClickedDetour(nint a1, byte a2)
    {
        IsIDKeyClickedHook.Enable();
        CheckHotbarClickedHook.Original(a1, a2);
        IsIDKeyClickedHook.Disable();
    }

    private class HeldInfo
    {
        public SimpleTimer LastPress        { get; } = new();
        public bool        LastFramePressed { get; set; }
        public bool        LastFrameHeld    { get; set; }

        public bool IsReady => LastPress.IsRunning && LastPress.ElapsedMilliseconds >= ModuleConfig.RepeatInterval;

        public void RestartLastPress()
        {
            if (!LastPress.IsRunning)
                Interlocked.Increment(ref runningTimersCount);
            LastPress.Restart();
        }

        public void ResetLastPress()
        {
            if (LastPress.IsRunning)
                Interlocked.Decrement(ref runningTimersCount);
            LastPress.Reset();
        }
    }

    private class SimpleTimer
    {
        private long startTime;

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

        public bool IsRunning { get; private set; }

        public long ElapsedMilliseconds => IsRunning ? Environment.TickCount64 - startTime : 0;
    }
    
    private class Config : ModuleConfiguration
    {
        public bool           MouseMode = true;
        public bool           GamepadMode;
        public GamepadButtons GamepadModeTriggerButtons = GamepadButtons.L2 | GamepadButtons.R2;
        public int            RepeatInterval = 200;
    }
}
