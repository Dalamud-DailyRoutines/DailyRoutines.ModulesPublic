using System.Globalization;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoChangeKeyboardLayout : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoChangeKeyboardLayoutTitle"),
        Description = Lang.Get("AutoChangeKeyboardLayoutDescription"),
        Category    = ModuleCategory.System,
        Author      = ["JiaXX"]
    };
    
    private static readonly CompSig SetTextInputTargetSig =
        new("4C 8B DC 55 53 57 41 54 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 9D ?? ?? ?? ??");
    private delegate void SetTextInputTargetDelegate
    (
        AtkComponentTextInput* component,
        AtkEventType           eventType,
        int                    eventParam,
        AtkEvent*              atkEvent,
        AtkEventData*          atkEventData
    );
    private Hook<SetTextInputTargetDelegate>? SetTextInputTargetHook;

    private Config config = null!;

    private Dictionary<ushort, KeyboardLayoutInfo>? cachedLayouts;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        var currentLayoutHandle = InputMethodController.CurrentLayout;
        var currentLangID       = (ushort)(currentLayoutHandle.ToInt64() & 0xFFFF);

        if (config.FocusLayoutLangID == 0)
        {
            config.FocusLayoutLangID = currentLangID;
            config.Save(this);
        }

        if (config.UnfocusLayoutLangID == 0)
        {
            config.UnfocusLayoutLangID = currentLangID;
            config.Save(this);
        }

        SetTextInputTargetHook ??= SetTextInputTargetSig.GetHook<SetTextInputTargetDelegate>(ChangeKeyboardLayout);
        SetTextInputTargetHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (Throttler.Shared.Throttle("AutoChangeKeyboardLayout-GetLayouts", 1_000))
            cachedLayouts = InputMethodController.GetAllKeyboardLayouts();

        if (cachedLayouts == null) return;

        // 聚焦时的布局选择
        ImGui.TextUnformatted(Lang.Get("Focused"));

        using (var focusCombo = ImRaii.Combo("##FocusLayout", cachedLayouts.GetValueOrDefault(config.FocusLayoutLangID).Name ?? Lang.Get("Unknown")))
        {
            if (focusCombo)
            {
                foreach (var (langID, layout) in cachedLayouts)
                {
                    var isSelected = config.FocusLayoutLangID == langID;

                    if (ImGui.Selectable(layout.Name, isSelected))
                    {
                        config.FocusLayoutLangID = langID;
                        config.Save(this);
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
            }
        }

        ImGui.Spacing();

        // 失焦时的布局选择
        ImGui.TextUnformatted(Lang.Get("Unfocused"));

        using (var unfocusCombo = ImRaii.Combo("##UnfocusLayout", cachedLayouts.GetValueOrDefault(config.UnfocusLayoutLangID).Name ?? Lang.Get("Unknown")))
        {
            if (unfocusCombo)
            {
                foreach (var (langID, layout) in cachedLayouts)
                {
                    var isSelected = config.UnfocusLayoutLangID == langID;

                    if (ImGui.Selectable(layout.Name, isSelected))
                    {
                        config.UnfocusLayoutLangID = langID;
                        config.Save(this);
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
            }
        }

        ImGui.NewLine();

        var currentLangID = (ushort)(InputMethodController.CurrentLayout.ToInt64() & 0xFFFF);
        ImGui.TextUnformatted($"{Lang.Get("Current")}\n\t\t{cachedLayouts.GetValueOrDefault(currentLangID).Name ?? Lang.Get("Unknown")}");
    }

    private void ChangeKeyboardLayout
    (
        AtkComponentTextInput* component,
        AtkEventType           eventType,
        int                    eventParam,
        AtkEvent*              atkEvent,
        AtkEventData*          atkEventData
    )
    {
        SetTextInputTargetHook!.Original(component, eventType, eventParam, atkEvent, atkEventData);

        switch (eventType)
        {
            case AtkEventType.FocusStart: // 聚焦
                DService.Instance().Framework.RunOnTick(() => CheckSlashAndSwitchLayout(component), TimeSpan.FromMilliseconds(50));
                break;
            case AtkEventType.FocusStop: // 失焦
                var unfocusLayout = InputMethodController.FindKeyboardLayout(config.UnfocusLayoutLangID);
                if (unfocusLayout != nint.Zero)
                    InputMethodController.SwitchToLayout(unfocusLayout);
                break;
        }
    }

    private void CheckSlashAndSwitchLayout(AtkComponentTextInput* textInputEventInterface)
    {
        if (textInputEventInterface == null) return;

        var textNode = textInputEventInterface->AtkTextNode;
        if (textNode == null) return;

        // 指令
        var nodeText = textNode->NodeText.ToString();
        if (nodeText.StartsWith('/')) return;

        var focusLayout = InputMethodController.FindKeyboardLayout(config.FocusLayoutLangID);
        if (focusLayout != nint.Zero)
            InputMethodController.SwitchToLayout(focusLayout);
    }

    private static class InputMethodController
    {
        private static Dictionary<ushort, KeyboardLayoutInfo>? allLayouts;

        public static nint CurrentLayout => GetKeyboardLayout(0);

        [DllImport("user32.dll")]
        private static extern void ActivateKeyboardLayout(nint hkl, uint Flags);

        [DllImport("user32.dll")]
        private static extern nint GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int nBuff, nint[] lpList);

        [DllImport("user32.dll")]
        private static extern nint LoadKeyboardLayout(string pwszKLID, uint Flags);

        public static Dictionary<ushort, KeyboardLayoutInfo> GetAllKeyboardLayouts()
        {
            if (allLayouts != null) return allLayouts;

            allLayouts = new Dictionary<ushort, KeyboardLayoutInfo>();

            var layoutCount = GetKeyboardLayoutList(0, null);
            if (layoutCount == 0)
                return allLayouts;

            var layouts     = new nint[layoutCount];
            var actualCount = GetKeyboardLayoutList(layoutCount, layouts);
            if (actualCount == 0)
                return allLayouts;

            foreach (var layout in layouts)
            {
                var langID     = (ushort)(layout.ToInt64() & 0xFFFF);
                var name       = GetLayoutDisplayName(langID);
                var layoutInfo = new KeyboardLayoutInfo { Handle = layout, Name = name, LangID = langID };
                allLayouts[langID] = layoutInfo;
            }

            return allLayouts;
        }

        private static string GetLayoutDisplayName(ushort langID)
        {
            try
            {
                var culture = new CultureInfo(langID);
                return culture.DisplayName;
            }
            catch
            {
                return string.Format($"0x{langID:X4}");
            }
        }

        public static void SwitchToLayout(nint layoutHandle)
        {
            try
            {
                if (CurrentLayout == layoutHandle) return;

                ActivateKeyboardLayout(layoutHandle, 0);
            }
            catch
            {
                // ignored
            }
        }

        public static nint FindKeyboardLayout(ushort langID)
        {
            var layoutCount = GetKeyboardLayoutList(0, null);
            if (layoutCount == 0) return nint.Zero;

            var layouts     = new nint[layoutCount];
            var actualCount = GetKeyboardLayoutList(layoutCount, layouts);
            if (actualCount == 0) return nint.Zero;

            foreach (var layout in layouts)
            {
                var layoutLangID = (ushort)(layout.ToInt64() & 0xFFFF);
                if (layoutLangID == langID)
                    return layout;
            }

            var klid = $"{langID:X8}";
            return LoadKeyboardLayout(klid, 0x00000001);
        }
    }

    private class Config : ModuleConfig
    {
        public ushort FocusLayoutLangID;   // 聚焦时的布局语言ID
        public ushort UnfocusLayoutLangID; // 失焦时的布局语言ID
    }

    private struct KeyboardLayoutInfo
    {
        public nint   Handle;
        public string Name;
        public ushort LangID;
    }
}
