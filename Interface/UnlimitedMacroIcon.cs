using System.Buffers.Text;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class UnlimitedMacroIcon : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnlimitedMacroIconTitle"),
        Description = Lang.Get("UnlimitedMacroIconDescription", COMMAND),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<RaptureMacroModule.Delegates.TryResolveMacroIcon>?          TryResolveMacroIconHook;
    private Hook<RaptureHotbarModule.HotbarSlot.Delegates.GetIconIdForSlot>? GetIconIDForSlotHook;
    private Hook<AtkComponentInputBase.CallbackDelegate>?                    InputCallbackHook;

    private readonly Dictionary<uint, uint> macroIcons = [];

    private uint? editingMacroID;
    private uint? editingIconID;

    protected override void Init()
    {
        TryResolveMacroIconHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(RaptureMacroModule.MemberFunctionPointers),
            nameof(RaptureMacroModule.MemberFunctionPointers.TryResolveMacroIcon),
            (RaptureMacroModule.Delegates.TryResolveMacroIcon)TryResolveMacroIconDetour
        );
        TryResolveMacroIconHook.Enable();

        GetIconIDForSlotHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(RaptureHotbarModule.HotbarSlot.MemberFunctionPointers),
            nameof(RaptureHotbarModule.HotbarSlot.MemberFunctionPointers.GetIconIdForSlot),
            (RaptureHotbarModule.HotbarSlot.Delegates.GetIconIdForSlot)GetIconIDForSlotDetour
        );
        GetIconIDForSlotHook.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "Macro", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "Macro", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "Macro", OnAddon);
        GameState.Instance().Login += OnLogin;

        CommandManager.Instance().AddSubCommand(COMMAND, new((_, _) => { }) { ShowInHelp = false });

        OnLogin();
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        GameState.Instance().Login -= OnLogin;
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        InputCallbackHook?.Disable();
        TryResolveMacroIconHook?.Disable();
        GetIconIDForSlotHook?.Disable();

        editingMacroID = null;
        editingIconID  = null;
        macroIcons.Clear();

        RefreshIcons();
    }

    private void OnLogin()
    {
        editingMacroID = null;
        editingIconID  = null;
        macroIcons.Clear();

        RefreshIcons();

        if (!Macro->IsAddonAndNodesReady())
            return;

        BindInputCallback((AddonMacro*)Macro);
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (type == AddonEvent.PreFinalize)
        {
            editingMacroID = null;
            editingIconID  = null;
            return;
        }

        var addon = (AddonMacro*)args.Addon.Address;
        if (type == AddonEvent.PostSetup)
        {
            BindInputCallback(addon);
            return;
        }

        if (args is not AddonRefreshArgs { AtkValueCount: >= 17 } refreshArgs)
            return;

        var values = (AtkValue*)refreshArgs.AtkValues;
        if (values == null ||
            values[11].Type is not (AtkValueType.String or AtkValueType.ManagedString))
            return;

        editingMacroID = null;
        editingIconID  = null;

        var input = (AtkComponentTextInput*)addon->GetComponentByNodeId(119);
        if (input != null)
            UpdateEditorIcon(addon, input->EvaluatedString.AsSpan());
    }

    private void BindInputCallback
    (
        AddonMacro* addon
    )
    {
        if (InputCallbackHook != null)
            return;

        var input = (AtkComponentTextInput*)addon->GetComponentByNodeId(119);
        if (input           == null ||
            input->Callback == null)
            return;

        InputCallbackHook = IGameInteropProvider.Instance().HookFromAddress
        (
            (nint)input->Callback,
            (AtkComponentInputBase.CallbackDelegate)InputCallbackDetour
        );
        InputCallbackHook.Enable();
    }

    private InputCallbackResult InputCallbackDetour
    (
        AtkUnitBase*      addon,
        InputCallbackType type,
        CStringPointer    rawString,
        CStringPointer    evaluatedString,
        int               eventKind
    )
    {
        var result = InputCallbackHook.Original(addon, type, rawString, evaluatedString, eventKind);

        if (type != InputCallbackType.TextChanged)
        {
            editingMacroID = null;
            editingIconID  = null;
            return result;
        }

        UpdateEditorIcon((AddonMacro*)addon, evaluatedString.AsSpan());
        return result;
    }

    private void UpdateEditorIcon
    (
        AddonMacro*        addon,
        ReadOnlySpan<byte> text
    )
    {
        var agent = AgentMacro.Instance();
        if (agent == null || 
            agent->SelectedMacroSet >= MACRO_SET_COUNT ||
            agent->SelectedMacroIndex >= MACROS_PER_SET)
            return;

        var macroID = (agent->SelectedMacroSet << 8) | agent->SelectedMacroIndex;
        uint? iconID = TryParseIconID(text, out var parsedIconID) ?
                           parsedIconID :
                           null;
        editingMacroID = macroID;
        editingIconID  = iconID;

        uint? previousIconID = macroIcons.TryGetValue(macroID, out var cachedIconID) ?
                                   cachedIconID :
                                   null;
        if (previousIconID == iconID)
            return;

        RefreshMacroIcon(addon, agent->SelectedMacroSet, agent->SelectedMacroIndex);

        var hotbarModule = RaptureHotbarModule.Instance();
        if (hotbarModule != null)
            hotbarModule->ReloadMacroSlots((byte)agent->SelectedMacroSet, (byte)agent->SelectedMacroIndex);
    }

    private bool TryResolveMacroIconDetour
    (
        RaptureMacroModule*                 module,
        UIModule*                           uiModule,
        RaptureHotbarModule.HotbarSlotType* outType,
        uint*                               outRowID,
        int                                 setID,
        uint                                macroID,
        uint*                               outItemID
    )
    {
        if ((uint)setID >= MACRO_SET_COUNT || 
            macroID >= MACROS_PER_SET)
            return TryResolveMacroIconHook.Original(module, uiModule, outType, outRowID, setID, macroID, outItemID);

        var   key    = ((uint)setID << 8) | macroID;
        uint? iconID = null;

        if (editingMacroID == key)
            iconID = editingIconID;
        else
        {
            var macro = module->GetMacro((uint)setID, macroID);

            if (macro != null)
            {
                foreach (ref var line in macro->Lines)
                    if (TryParseIconID(line.AsSpan(), out var parsedIconID))
                    {
                        iconID = parsedIconID;
                        break;
                    }
            }
        }

        if (iconID == null)
        {
            macroIcons.Remove(key);
            return TryResolveMacroIconHook.Original(module, uiModule, outType, outRowID, setID, macroID, outItemID);
        }

        macroIcons[key] = iconID.Value;
        *outType        = RaptureHotbarModule.HotbarSlotType.Macro;
        *outRowID       = key;
        if (outItemID != null)
            *outItemID = iconID.Value;
        return true;
    }

    private int GetIconIDForSlotDetour
    (
        RaptureHotbarModule.HotbarSlot*    slot,
        RaptureHotbarModule.HotbarSlotType slotType,
        uint                               actionID
    ) =>
        slotType == RaptureHotbarModule.HotbarSlotType.Macro && macroIcons.TryGetValue(actionID, out var iconID) ?
            (int)iconID :
            GetIconIDForSlotHook.Original(slot, slotType, actionID);

    private static void RefreshIcons()
    {
        var hotbarModule = RaptureHotbarModule.Instance();
        if (hotbarModule != null)
            hotbarModule->ReloadAllMacroSlots();

        var agent = AgentMacro.Instance();
        if (agent == null || 
            !Macro->IsAddonAndNodesReady())
            return;

        for (uint i = 0; i < MACROS_PER_SET; i++)
            RefreshMacroIcon((AddonMacro*)Macro, agent->SelectedMacroSet, i);
    }

    private static void RefreshMacroIcon
    (
        AddonMacro* addon,
        uint        setID,
        uint        macroID
    )
    {
        var module       = RaptureMacroModule.Instance();
        var uiModule     = UIModule.Instance();
        var hotbarModule = RaptureHotbarModule.Instance();
        if (module       == null            ||
            uiModule     == null            ||
            hotbarModule == null            ||
            setID        >= MACRO_SET_COUNT ||
            macroID      >= MACROS_PER_SET)
            return;

        var macro = module->GetMacro(setID, macroID);
        if (macro == null)
            return;

        var iconID = macro->IconId == 0 ?
                         uint.MaxValue :
                         macro->IconId;
        var  slotType       = RaptureHotbarModule.HotbarSlotType.Empty;
        uint rowID          = 0;
        uint resolvedIconID = 0;
        if (module->TryResolveMacroIcon(uiModule, &slotType, &rowID, (int)setID, macroID, &resolvedIconID))
            iconID = resolvedIconID != 0 ?
                         resolvedIconID :
                         (uint)hotbarModule->GetSlotById(0, 0)->GetIconIdForSlot(slotType, rowID);

        var dragDrop = addon->DragDropComponent[(int)macroID].Value;

        if (dragDrop != null)
        {
            if ((uint)dragDrop->GetIconId() != iconID)
                dragDrop->LoadIcon(iconID);

            if (dragDrop->AtkComponentIcon != null)
                dragDrop->AtkComponentIcon->OwnerNode->ToggleVisibility(iconID != uint.MaxValue);
        }

        var agent = AgentMacro.Instance();
        if (agent == null || agent->SelectedMacroSet != setID || agent->SelectedMacroIndex != macroID)
            return;

        var preview = (AtkComponentIcon*)addon->GetComponentByNodeId(112);
        if (preview == null || preview->IconId == iconID)
            return;

        if (iconID == uint.MaxValue)
            preview->UnloadIcon();
        else
            preview->LoadIcon(iconID);
    }

    private static bool TryParseIconID
    (
        ReadOnlySpan<byte> text,
        out uint           iconID
    )
    {
        iconID = 0;

        for (var i = 0; i < MAX_MACRO_LINES && !text.IsEmpty; i++)
        {
            var lineEnd = text.IndexOfAny((byte)'\r', (byte)'\n');
            var line = lineEnd < 0 ?
                           text :
                           text[..lineEnd];
            var carriageReturn = lineEnd >= 0 && text[lineEnd] == '\r';
            text = lineEnd < 0 ?
                       [] :
                       text[(lineEnd + 1)..];
            if (carriageReturn && !text.IsEmpty && text[0] == '\n')
                text = text[1..];

            line = line.Trim(" \t\v\f"u8);
            var separator = line.IndexOfAny(" \t\v\f"u8);
            if (separator < 0 || !Ascii.EqualsIgnoreCase(line[..separator], "/pdr"u8))
                continue;

            line      = line[(separator + 1)..].TrimStart(" \t\v\f"u8);
            separator = line.IndexOfAny(" \t\v\f"u8);
            if (separator < 0 || !Ascii.EqualsIgnoreCase(line[..separator], "micon"u8))
                continue;

            line = line[(separator + 1)..].Trim(" \t\v\f"u8);
            if (!line.IsEmpty                                           &&
                line[0] is >= (byte)'0' and <= (byte)'9'                &&
                Utf8Parser.TryParse(line, out iconID, out var consumed) &&
                consumed == line.Length                                 &&
                iconID is > 0 and < int.MaxValue)
                return true;
        }

        return false;
    }

    #region 常量

    private const string COMMAND         = "micon";
    private const int    MACRO_SET_COUNT = 2;
    private const int    MACROS_PER_SET  = 100;
    private const int    MAX_MACRO_LINES = 15;

    #endregion
}
