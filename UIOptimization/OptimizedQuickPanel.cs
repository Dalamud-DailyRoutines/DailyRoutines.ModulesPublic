using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;
using KamiToolKit.Timelines;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AgentShowDelegate = OmenTools.Interop.Game.Models.Native.AgentShowDelegate;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedQuickPanel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedQuickPanelTitle"),
        Description = Lang.Get("OptimizedQuickPanelDescription", QuickPanelLine.Command, QuickPanelLine.Alias),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private delegate void ToggleUIDelegate(UIModule* module, UiFlags flags, bool enable, bool unknown = true);
    private Hook<ToggleUIDelegate>? ToggleUIHook;

    private Hook<AgentShowDelegate>? AgentQuickPanelShowHook;

    private Config config = null!;
    
    private CheckboxNode? lockCheckBoxNode;

    private bool isLastQuickPanelEnabled;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);

        AgentQuickPanelShowHook = DService.Instance().Hook.HookFromAddress<AgentShowDelegate>
        (
            AgentQuickPanel.Instance()->VirtualTable->GetVFuncByName("Show"),
            AgentQuickPanelShowDetour
        );
        AgentQuickPanelShowHook.Enable();

        ToggleUIHook = DService.Instance().Hook.HookFromAddress<ToggleUIDelegate>
        (
            UIModule.Instance()->VirtualTable->GetVFuncByName("ToggleUi"),
            ToggleUIDetour
        );
        ToggleUIHook.Enable();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "QuickPanel", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "QuickPanel", OnAddon);

        UpdateAddonFlags();
    }

    protected override void Uninit()
    {
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
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

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                lockCheckBoxNode?.Dispose();
                lockCheckBoxNode = null;

                if (config != null)
                    config.Save(this);
                break;

            case AddonEvent.PostDraw:
                if (QuickPanel == null) return;

                if (config.IsLock && config.LastPosition != Vector2.Zero)
                    QuickPanel->SetPosition((short)config.LastPosition.X, (short)config.LastPosition.Y);
                config.LastPosition = new(QuickPanel->RootNode->GetXFloat(), QuickPanel->RootNode->GetYFloat());

                // 正常比较高帧率状态下应该是没问题的
                if (config.IsLock                                   &&
                    UIInputData.Instance()->IsInputIdPressed(InputId.ESC) &&
                    AtkStage.Instance()->GetFocus() == null               &&
                    SystemMenu                      == null)
                    AgentHUD.Instance()->HandleMainCommandOperation(MainCommandOperation.OpenSystemMenu, 0);

                if (lockCheckBoxNode == null)
                {
                    lockCheckBoxNode = new()
                    {
                        Position    = new(8, 34),
                        TextTooltip = LuminaWrapper.GetAddonText(config.IsLock ? 3061U : 3060),
                        Size        = new(20, 24),
                        IsChecked   = config.IsLock
                    };

                    lockCheckBoxNode.OnClick = x =>
                    {
                        config.IsLock = x;
                        config.Save(this);

                        lockCheckBoxNode.TextTooltip = LuminaWrapper.GetAddonText(config.IsLock ? 3061U : 3060);
                        lockCheckBoxNode.ShowTooltip();
                        UpdateAddonFlags();
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

    // 给 Addon 上 Flag 处理锁定
    private void AgentQuickPanelShowDetour(AgentInterface* agent)
    {
        AgentQuickPanelShowHook.Original(agent);
        UpdateAddonFlags();
    }

    // 让快捷面板支持打开面板参数
    private static void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        var messageText = message.ToString();
        if (!messageText.StartsWith('/')) return;
        if (messageText.Split(' ') is not { Length: 2 } parsedCommand ||
            parsedCommand[0] != QuickPanelLine.Command.ToString() && parsedCommand[0] != QuickPanelLine.Alias.ToString())
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

    // 随着 ActionBar 隐藏一并隐藏, 和 ActionBar 逻辑保持一致
    private void ToggleUIDetour(UIModule* module, UiFlags flags, bool enable, bool unknown)
    {
        ToggleUIHook.Original(module, flags, enable, unknown);

        if (flags.IsSetAny(UiFlags.ActionBars))
        {
            // 隐藏
            if (!enable)
            {
                isLastQuickPanelEnabled = QuickPanel != null;
                AgentQuickPanel.Instance()->Hide();
            }
            else
            {
                if (isLastQuickPanelEnabled && QuickPanel == null)
                    AgentQuickPanel.Instance()->OpenPanel(AgentQuickPanel.Instance()->ActivePanel);

                isLastQuickPanelEnabled = false;
            }
        }
    }

    private void UpdateAddonFlags()
    {
        if (QuickPanel == null) return;

        // 禁止 ESC 键关闭
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A1, 0x4, config.IsLock);

        // 禁止聚焦
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A0, 0x80, config.IsLock);

        // 禁止自动聚焦
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A1, 0x40, config.IsLock);

        // 禁止右键菜单
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A3, 0x1, config.IsLock);

        // 禁止交互
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A3, 0x40, !config.IsLock);
    }
    
    private class Config : ModuleConfig
    {
        public bool    IsLock = true;
        public Vector2 LastPosition;
    }
    
    #region 常量

    private static readonly TextCommand QuickPanelLine = LuminaGetter.GetRowOrDefault<TextCommand>(50);

    #endregion
}
