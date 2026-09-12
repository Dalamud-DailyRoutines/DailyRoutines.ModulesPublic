using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using KamiToolKit.Timelines;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class OptimizedQuickPanel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedQuickPanelTitle"),
        Description = Lang.Get("OptimizedQuickPanelDescription", QuickPanelLine.Command, QuickPanelLine.Alias),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig AddonControlReceiveEventSig = new("40 53 56 41 56 48 81 EC ?? ?? ?? ?? 48 8B F1");
    private delegate void AddonControlReceiveEventDelegate
    (
        AtkAddonControl* control,
        ushort           eventType,
        int              eventParam,
        AtkEvent*        atkEvent,
        AtkEventData*    atkEventData
    );
    private Hook<AddonControlReceiveEventDelegate>? AddonControlReceiveEventHook;
    
    private static readonly CompSig IsMoveHandleNodeSig = new("48 3B 91 ?? ?? ?? ?? 74 ?? 45 33 C0");
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsMoveHandleNodeDelegate
    (
        AtkUnitBase* addon,
        AtkResNode*  node
    );
    private Hook<IsMoveHandleNodeDelegate>? IsMoveHandleNodeHook;

    private static readonly CompSig StartDraggingAddonSig = new("40 53 48 83 EC 20 80 A2 A1 01 00 00 EF");
    private delegate void StartDraggingAddonDelegate
    (
        AtkUnitManager* manager,
        AtkUnitBase*    addon
    );
    private Hook<StartDraggingAddonDelegate>? StartDraggingAddonHook;

    private static readonly CompSig QuickPanelReceiveEventSig = 
        new("40 55 53 56 57 41 56 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 48 8B 7D");
    private delegate void QuickPanelReceiveEventDelegate
    (
        AtkUnitBase*  addon,
        ushort        eventType,
        int           eventParam,
        AtkEvent*     atkEvent,
        AtkEventData* atkEventData
    );
    private Hook<QuickPanelReceiveEventDelegate>? QuickPanelReceiveEventHook;
    
    private Config config = null!;

    private CheckboxNode? lockCheckBoxNode;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);

        StartDraggingAddonHook = StartDraggingAddonSig.GetHook<StartDraggingAddonDelegate>(StartDraggingAddonDetour);
        StartDraggingAddonHook.Enable();

        AddonControlReceiveEventHook = AddonControlReceiveEventSig.GetHook<AddonControlReceiveEventDelegate>(AddonControlReceiveEventDetour);
        AddonControlReceiveEventHook.Enable();

        IsMoveHandleNodeHook = IsMoveHandleNodeSig.GetHook<IsMoveHandleNodeDelegate>(IsMoveHandleNodeDetour);
        IsMoveHandleNodeHook.Enable();

        QuickPanelReceiveEventHook = QuickPanelReceiveEventSig.GetHook<QuickPanelReceiveEventDelegate>(QuickPanelReceiveEventDetour);
        QuickPanelReceiveEventHook.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw, "QuickPanel", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw, "QuickPanel", OnAddon);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreUpdate,   "QuickPanel", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "QuickPanel", OnAddon);
    }

    protected override void Uninit()
    {
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

        lockCheckBoxNode?.Dispose();
        lockCheckBoxNode = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted
            (
                $"{QuickPanelLine.Command} <{Lang.Get("OptimizedQuickPanel-CommandArgs")} / close> → {Lang.Get("OptimizedQuickPanel-CommandArgs-Help")} / {LuminaWrapper.GetAddonText(2366)}"
            );
            ImGui.TextUnformatted
            (
                $"{QuickPanelLine.Alias} <{Lang.Get("OptimizedQuickPanel-CommandArgs")} / close> → {Lang.Get("OptimizedQuickPanel-CommandArgs-Help")} / {LuminaWrapper.GetAddonText(2366)}"
            );
        }
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                lockCheckBoxNode = null;
                config.Save(this);
                break;
            
            case AddonEvent.PreUpdate:
                UpdateAddonFlags();
                UpdateSlotLockState();
                break;

            case AddonEvent.PostDraw:
                if (QuickPanel == null) return;
                
                if (lockCheckBoxNode == null)
                {
                    lockCheckBoxNode = new()
                    {
                        Position = new(8, 34),
                        TextTooltip = LuminaWrapper.GetAddonText
                        (
                            config.IsLock ?
                                3061U :
                                3060
                        ),
                        Size      = new(20, 24),
                        IsChecked = config.IsLock
                    };

                    lockCheckBoxNode.OnClick = x =>
                    {
                        config.IsLock = x;
                        config.Save(this);

                        lockCheckBoxNode.TextTooltip = LuminaWrapper.GetAddonText
                        (
                            config.IsLock ?
                                3061U :
                                3060
                        );
                        lockCheckBoxNode.ShowTooltip();
                        UpdateAddonFlags();
                        UpdateSlotLockState();
                    };

                    lockCheckBoxNode.BoxBackground.IsVisible = false;
                    lockCheckBoxNode.BoxForeground.IsVisible = false;
                    lockCheckBoxNode.Label.IsVisible         = false;

                    var lockImageNode = new SimpleImageNode
                    {
                        Size        = new(20, 24),
                        TexturePath = "ui/uld/ActionBar_hr1.tex"
                    };
                    lockImageNode.AddPart
                    (
                        new Part
                        {
                            Size               = new(20, 24),
                            TexturePath        = "ui/uld/ActionBar_hr1.tex",
                            TextureCoordinates = new(48, 0),
                            Id                 = 1
                        },
                        new Part
                        {
                            Size               = new(20, 24),
                            TexturePath        = "ui/uld/ActionBar_hr1.tex",
                            TextureCoordinates = new(68, 0),
                            Id                 = 2
                        },
                        new Part
                        {
                            Size               = new(20, 24),
                            TexturePath        = "ui/uld/ActionBar_hr1.tex",
                            TextureCoordinates = new(88, 0),
                            Id                 = 3
                        }
                    );
                    lockImageNode.AttachNode(lockCheckBoxNode);

                    lockImageNode.AddTimeline
                    (
                        new TimelineBuilder()
                            .BeginFrameSet(1, 10)
                            .AddFrame(1, addColor: new Vector3(0, 0, 0), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(1, partId: 1)
                            .EndFrameSet()
                            .BeginFrameSet(11, 20)
                            .AddFrame(11, addColor: new Vector3(0,  0,  0),  multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(13, addColor: new Vector3(40, 40, 40), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(11, partId: 1)
                            .AddFrame(13, partId: 1)
                            .EndFrameSet()
                            .BeginFrameSet(21, 30)
                            .AddFrame(21, addColor: new Vector3(60, 60, 60), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(21, partId: 2)
                            .EndFrameSet()
                            .BeginFrameSet(31, 40)
                            .AddFrame(31, addColor: new Vector3(0, 0, 0), multiplyColor: new Vector3(50, 50, 50))
                            .AddFrame(31, partId: 1)
                            .EndFrameSet()
                            .BeginFrameSet(41, 50)
                            .AddFrame(41, addColor: new Vector3(60, 60, 60), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(43, addColor: new Vector3(0,  0,  0),  multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(41, partId: 1)
                            .AddFrame(43, partId: 1)
                            .EndFrameSet()
                            .BeginFrameSet(51, 60)
                            .AddFrame(51, addColor: new Vector3(40, 40, 40), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(53, addColor: new Vector3(0,  0,  0),  multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(51, partId: 1)
                            .AddFrame(53, partId: 1)
                            .EndFrameSet()
                            .BeginFrameSet(61, 70)
                            .AddFrame(61, addColor: new Vector3(0, 0, 0), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(61, partId: 3)
                            .EndFrameSet()
                            .BeginFrameSet(71, 80)
                            .AddFrame(71, addColor: new Vector3(0,  0,  0),  multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(73, addColor: new Vector3(40, 40, 40), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(71, partId: 3)
                            .AddFrame(73, partId: 3)
                            .EndFrameSet()
                            .BeginFrameSet(81, 90)
                            .AddFrame(81, addColor: new Vector3(60, 60, 60), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(81, partId: 2)
                            .EndFrameSet()
                            .BeginFrameSet(91, 100)
                            .AddFrame(91, addColor: new Vector3(0, 0, 0), multiplyColor: new Vector3(50, 50, 50))
                            .AddFrame(91, partId: 3)
                            .EndFrameSet()
                            .BeginFrameSet(101, 110)
                            .AddFrame(101, addColor: new Vector3(60, 60, 60), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(103, addColor: new Vector3(0,  0,  0),  multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(101, partId: 3)
                            .AddFrame(103, partId: 3)
                            .EndFrameSet()
                            .BeginFrameSet(111, 120)
                            .AddFrame(111, addColor: new Vector3(40, 40, 40), multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(113, addColor: new Vector3(0,  0,  0),  multiplyColor: new Vector3(100, 100, 100))
                            .AddFrame(111, partId: 3)
                            .AddFrame(113, partId: 3)
                            .EndFrameSet()
                            .Build()
                    );

                    lockCheckBoxNode.AttachNode(QuickPanel);
                }

                break;
        }
    }

    // 让快捷面板支持打开面板参数
    private static void OnPreExecuteCommandInner
    (
        ref bool             isPrevented,
        ref ReadOnlySeString message
    )
    {
        var messageText = message.ToString();
        if (!messageText.StartsWith('/')) return;
        if (messageText.Split(' ') is not { Length: 2 } parsedCommand ||
            (parsedCommand[0] != QuickPanelLine.Command.ToString() && parsedCommand[0] != QuickPanelLine.Alias.ToString()))
            return;

        if (parsedCommand[1].Equals("close"))
        {
            AgentQuickPanel.Instance()->Hide();
            isPrevented = true;
            return;
        }

        if (!int.TryParse(parsedCommand[1], out var index) || index is not (> 0 and < 5))
            return;

        AgentQuickPanel.Instance()->OpenPanel((uint)(index - 1), showFirstTimeHelp: false);
        isPrevented = true;
    }

    private void StartDraggingAddonDetour
    (
        AtkUnitManager* manager,
        AtkUnitBase*    addon
    )
    {
        if (addon == QuickPanel && config.IsLock) return;

        StartDraggingAddonHook.Original(manager, addon);
    }

    private void AddonControlReceiveEventDetour
    (
        AtkAddonControl* control,
        ushort           eventType,
        int              eventParam,
        AtkEvent*        atkEvent,
        AtkEventData*    atkEventData
    )
    {
        if (eventType == ATK_EVENT_TYPE_MOUSE_MOVE && control->ParentAddon == QuickPanel && config.IsLock) return;

        AddonControlReceiveEventHook.Original(control, eventType, eventParam, atkEvent, atkEventData);
    }
    
    private bool IsMoveHandleNodeDetour
    (
        AtkUnitBase* addon,
        AtkResNode*  node
    )
    {
        if (addon == QuickPanel && config.IsLock) return false;

        return IsMoveHandleNodeHook.Original(addon, node);
    }

    private void QuickPanelReceiveEventDetour
    (
        AtkUnitBase*  addon,
        ushort        eventType,
        int           eventParam,
        AtkEvent*     atkEvent,
        AtkEventData* atkEventData
    )
    {
        if (eventType == (ushort)AtkEventType.DragDropClick && atkEventData->DragDropData.MouseButtonId != 0 && config.IsLock) return;

        QuickPanelReceiveEventHook.Original(addon, eventType, eventParam, atkEvent, atkEventData);
    }

    private void UpdateAddonFlags()
    {
        if (QuickPanel == null) return;

        QuickPanel->DisableFocusability = config.IsLock;

        if (config.IsLock)
            QuickPanel->Flags1B4 |= (uint)UiFlags.ActionBars;
        else
            QuickPanel->Flags1B4 &= ~(uint)UiFlags.ActionBars;
    }

    private void UpdateSlotLockState()
    {
        if (QuickPanel == null) return;

        var slot = (byte*)QuickPanel + QUICK_PANEL_SLOT_ARRAY_OFFSET;
        for (var i = 0; i < QUICK_PANEL_SLOT_ARRAY_COUNT; i++, slot += QUICK_PANEL_SLOT_SIZE)
        {
            var dragDrop = *(AtkComponentDragDrop**)slot;
            if (dragDrop == null) continue;

            if (config.IsLock)
                dragDrop->Flags |= DragDropFlag.Locked;
            else
                dragDrop->Flags &= ~DragDropFlag.Locked;
        }
    }

    private class Config : ModuleConfig
    {
        public bool IsLock = true;
    }

    #region 常量

    private const ushort ATK_EVENT_TYPE_MOUSE_MOVE = 5;

    private const int QUICK_PANEL_SLOT_ARRAY_OFFSET = 576;
    private const int QUICK_PANEL_SLOT_ARRAY_COUNT  = 25;
    private const int QUICK_PANEL_SLOT_SIZE         = 48;

    private static readonly TextCommand QuickPanelLine = LuminaGetter.GetRowOrDefault<TextCommand>(50);

    #endregion
}
