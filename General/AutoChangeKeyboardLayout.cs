using System;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;

namespace DailyRoutines.ModulesPublic;

public class AutoChangeKeyboardLayout : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoChangeKeyboardLayoutTitle"),
        Description = GetLoc("AutoChangeKeyboardLayoutDescription"),
        Category    = ModuleCategories.General,
        Author      = ["JiaXX"]
    };
    
    private static Hook<SetTextInputTargetDelegate>? SetTextInputTargetHook;
    private delegate nint SetTextInputTargetDelegate(nint raptureAtkModule, nint textInputEventInterface);
    private static readonly CompSig SetTextInputTargetSig = new("4C 8B DC 55 53 57 41 54 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 9D ?? ?? ?? ??");

    protected override void Init()
    {
        SetTextInputTargetHook ??= SetTextInputTargetSig.GetHook<SetTextInputTargetDelegate>(ChangeKeyboardLayout);
        SetTextInputTargetHook.Enable();
    }
    
    private static nint ChangeKeyboardLayout(nint raptureAtkModule, nint textInputEventInterface)
    {
        var result = SetTextInputTargetHook!.Original(raptureAtkModule, textInputEventInterface);

        switch (textInputEventInterface)
        {
            case 19:
                InputMethodController.SwitchToEnglish();
                break;
            case 18:
                InputMethodController.SwitchToChinese();
                break;
        }

        return result;
    }

    private static class InputMethodController
    {
        [DllImport("user32.dll")]
        private static extern void ActivateKeyboardLayout(nint hkl, uint Flags);

        [DllImport("user32.dll")]
        private static extern nint GetKeyboardLayout(uint idThread);

        public static nint currentLayout => GetKeyboardLayout(0);
        
        public static readonly nint 
            englishLayout = new(0x4090409),
            chineseLayout = new(0x8040804);

        public static void SwitchToEnglish()
        {
            try
            {
                if (currentLayout == englishLayout) return;
                ActivateKeyboardLayout(englishLayout, 0);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        public static void SwitchToChinese()
        {
            try
            {
                if (currentLayout == chineseLayout) return;
                ActivateKeyboardLayout(chineseLayout, 0);
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }
}
