using System;
using System.Threading;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoConstantlyClick : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoConstantlyClickTitle"),
        Description = GetLoc("AutoConstantlyClickDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["AtmoOmen", "KirisameVanilla"],
    };

    private const int MaxKey = 512;
    
    private static readonly GamepadButtons[] Triggers = [GamepadButtons.L2, GamepadButtons.R2];

    private delegate byte IDKeyDelegate(void* data, InputId key);

    private static readonly CompSig              IsIDKeyPressedSig = new("48 89 5C 24 ?? 56 41 56 41 57 48 83 EC ?? 48 63 C2");
    private static          Hook<IDKeyDelegate>? IsIDKeyPressedHook;

    private static readonly CompSig        IsInputIDDownSig = new("E8 ?? ?? ?? ?? 33 DB 41 8B D5");
    private static          IDKeyDelegate? IsInputIDDown;

    private static readonly CompSig GamepadPollSig = new("40 55 53 57 41 57 48 8D AC 24 58 FC FF FF");
    private static Hook<ControllerPoll>? GamepadPollHook;
    private delegate int ControllerPoll(nint controllerInput);

    private static readonly CompSig CheckHotbarClickedSig = new("E8 ?? ?? ?? ?? 48 8B 4F ?? 48 8B 01 FF 50 ?? 48 8B C8 E8 ?? ?? ?? ?? 84 C0 74");
    private delegate void CheckHotbarClickedDelegate(nint a1, byte a2);
    private static Hook<CheckHotbarClickedDelegate>? CheckHotbarClickedHook;

    private static Config ModuleConfig = null!;
    
    private static readonly HeldInfo[] InputIDInfos = new HeldInfo[MaxKey + 1];
    private static          long       ThrottleTime = Environment.TickCount64;
    private static          int        RunningTimersCount;


    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        for (var i = 0; i <= MaxKey; i++)
            InputIDInfos[i] = new HeldInfo();

        IsInputIDDown = IsInputIDDownSig.GetDelegate<IDKeyDelegate>();

        IsIDKeyPressedHook ??= IsIDKeyPressedSig.GetHook<IDKeyDelegate>(IsIDKeyPressedDetour);

        CheckHotbarClickedHook ??= CheckHotbarClickedSig.GetHook<CheckHotbarClickedDelegate>(CheckHotbarClickedDetour);
        GamepadPollHook        ??= GamepadPollSig.GetHook<ControllerPoll>(GamepadPollDetour);

        if (ModuleConfig.MouseMode) 
            CheckHotbarClickedHook.Enable();
        if (ModuleConfig.GamepadMode) 
            GamepadPollHook.Enable();
    }

    protected override void ConfigUI()
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
            if (ModuleConfig.MouseMode) 
                CheckHotbarClickedHook.Enable();
            else 
                CheckHotbarClickedHook.Disable();
        }

        if (ImGui.Checkbox(GetLoc("AutoConstantlyClick-GamepadMode"), ref ModuleConfig.GamepadMode))
        {
            ModuleConfig.Save(this);
            if (ModuleConfig.GamepadMode) 
                GamepadPollHook.Enable();
            else 
                GamepadPollHook.Disable();
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

    private int GamepadPollDetour(nint gamepadInput)
    {
        var input = (PadDevice*)gamepadInput;
        if (DService.Gamepad.Raw(ModuleConfig.GamepadModeTriggerButtons) == 1)
        {
            foreach (var btn in Enum.GetValues<GamepadButtons>())
            {
                if (DService.Gamepad.Raw(btn) == 1)
                {
                    if (Environment.TickCount64 >= ThrottleTime)
                    {
                        ThrottleTime = Environment.TickCount64 + ModuleConfig.RepeatInterval;
                        input->GamepadInputData.Buttons -= (ushort)btn;
                    }
                }
            }
        }

        return GamepadPollHook.Original((nint)input);
    }

    private static byte IsIDKeyPressedDetour(void* data, InputId key)
    {
        if (key is not (>= InputId.HOTBAR_UP and <= InputId.HOTBAR_CONTENTS_ACT_R)) return 0;

        var info = InputIDInfos[(int)key];

        var isClicked = IsIDKeyPressedHook.Original(data, key) == 1;
        var isPressed = IsInputIDDown(data, key)               == 1;
        var orig      = info.IsReady ? isPressed : isClicked;

        if (orig)
            info.RestartLastPress();
        else if (isPressed != info.LastFrameHeld)
        {
            if (isPressed && RunningTimersCount > 0)
                info.RestartLastPress();
            else
                info.ResetLastPress();
        }

        info.LastFrameHeld = isPressed;
        info.LastFramePressed = isClicked;
        return (byte)(orig ? 1 : 0);
    }

    private static void CheckHotbarClickedDetour(nint a1, byte a2)
    {
        IsIDKeyPressedHook.Enable();
        CheckHotbarClickedHook.Original(a1, a2);
        IsIDKeyPressedHook.Disable();
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
