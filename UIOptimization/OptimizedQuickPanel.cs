using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Classes.Timelines;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedQuickPanel : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedQuickPanelTitle"),
        Description = GetLoc("OptimizedQuickPanelDescription", QuickPanelLine.Command, QuickPanelLine.Alias),
        Category    = ModuleCategories.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly TextCommand QuickPanelLine = LuminaGetter.GetRowOrDefault<TextCommand>(50);

    private static Config ModuleConfig = null!;

    private delegate void                    ToggleUIDelegate(UIModule* module, UIModule.UiFlags flags, bool enable, bool unknown = true);
    private static   Hook<ToggleUIDelegate>? ToggleUIHook;
    
    private static Hook<AgentShowDelegate>? AgentQuickPanelShowHook;

    private static bool IsLastQuickPanelEnabled;

    private static CheckboxNode? LockCheckBoxNode;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        ChatManager.RegPreExecuteCommandInner(OnPreExecuteCommandInner);
        
        AgentQuickPanelShowHook = DService.Hook.HookFromAddress<AgentShowDelegate>(
            GetVFuncByName(AgentQuickPanel.Instance()->VirtualTable, "Show"),
            AgentQuickPanelShowDetour);
        AgentQuickPanelShowHook.Enable();
        
        ToggleUIHook = DService.Hook.HookFromAddress<ToggleUIDelegate>(
            GetVFuncByName(UIModule.Instance()->VirtualTable, "ToggleUi"),
            ToggleUIDetour);
        ToggleUIHook.Enable(); 
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "QuickPanel", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "QuickPanel", OnAddon);

        UpdateAddonFlags();
    }

    protected override void Uninit()
    {
        ChatManager.Unreg(OnPreExecuteCommandInner);
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Command"));

        using (ImRaii.PushIndent())
        {
            ImGui.Text($"{QuickPanelLine.Command} {GetLoc("OptimizedQuickPanel-CommandArgs")} → {GetLoc("OptimizedQuickPanel-CommandArgs-Help")}");
            ImGui.Text($"{QuickPanelLine.Alias} {GetLoc("OptimizedQuickPanel-CommandArgs")} → {GetLoc("OptimizedQuickPanel-CommandArgs-Help")}");
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                LockCheckBoxNode?.DetachNode();
                LockCheckBoxNode = null;
                
                if (ModuleConfig != null)
                    ModuleConfig.Save(this);
                break;
            
            case AddonEvent.PostDraw:
                if (QuickPanel == null) return;
                
                if (ModuleConfig.IsLock)
                    QuickPanel->SetPosition((short)ModuleConfig.LastPosition.X, (short)ModuleConfig.LastPosition.Y);
                ModuleConfig.LastPosition = new(QuickPanel->RootNode->GetXFloat(), QuickPanel->RootNode->GetYFloat());

                // 正常比较高帧率状态下应该是没问题的
                if (ModuleConfig.IsLock                                   &&
                    UIInputData.Instance()->IsInputIdPressed(InputId.ESC) &&
                    AtkStage.Instance()->GetFocus() == null               &&
                    SystemMenu                      == null)
                    AgentHUD.Instance()->HandleMainCommandOperation(MainCommandOperation.OpenSystemMenu, 0);

                if (LockCheckBoxNode == null)
                {
                    LockCheckBoxNode = new()
                    {
                        Position  = new(8, 34),
                        Tooltip   = LuminaWrapper.GetAddonText(ModuleConfig.IsLock ? 3061U : 3060),
                        Size      = new(20, 24),
                        IsChecked = ModuleConfig.IsLock
                    };

                    LockCheckBoxNode.OnClick = x =>
                    {
                        ModuleConfig.IsLock = x;
                        ModuleConfig.Save(this);

                        LockCheckBoxNode.Tooltip = LuminaWrapper.GetAddonText(ModuleConfig.IsLock ? 3061U : 3060);
                        LockCheckBoxNode.ShowTooltip();
                        UpdateAddonFlags();
                    };
                    
                    LockCheckBoxNode.BoxBackground.IsVisible = false;
                    LockCheckBoxNode.BoxForeground.IsVisible = false;
                    LockCheckBoxNode.Label.IsVisible         = false;
                    
                    var lockImageNode = new SimpleImageNode
                    {
                        Size = new(20, 24),
                        TexturePath = "ui/uld/ActionBar_hr1.tex"
                    };
                    lockImageNode.AddPart(
                        new Part
                        {
                            Size               = new(20, 24),
                            TexturePath        = "ui/uld/ActionBar_hr1.tex",
                            TextureCoordinates = new(48, 0),
                            Id                 = 1,
                        },
                        new Part
                        {
                            Size               = new(20, 24),
                            TexturePath        = "ui/uld/ActionBar_hr1.tex",
                            TextureCoordinates = new(68, 0),
                            Id                 = 2,
                        },
                        new Part
                        {
                            Size               = new(20, 24),
                            TexturePath        = "ui/uld/ActionBar_hr1.tex",
                            TextureCoordinates = new(88, 0),
                            Id                 = 3,
                        });
                    lockImageNode.AttachNode(LockCheckBoxNode);
                    
                    lockImageNode.AddTimeline(new TimelineBuilder()
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
                                              .Build());
                    
                    LockCheckBoxNode.AttachNode(QuickPanel);
                }
                break;
        }
    }
    
    // 给 Addon 上 Flag 处理锁定
    private static void AgentQuickPanelShowDetour(AgentInterface* agent)
    {
        AgentQuickPanelShowHook.Original(agent);
        UpdateAddonFlags();
    }
    
    // 让快捷面板支持打开面板参数
    private static void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        var messageText = message.ToString();
        if (!messageText.StartsWith('/')) return;
        if (messageText.Split(' ') is not { Length: 2 } prasedCommand                                                      ||
            (prasedCommand[0] != QuickPanelLine.Command.ToString() && prasedCommand[0] != QuickPanelLine.Alias.ToString()) ||
            !int.TryParse(prasedCommand[1], out var index)                                                                 ||
            index is not (> 0 and < 5))
            return;
        
        AgentQuickPanel.Instance()->OpenPanel((uint)(index - 1), showFirstTimeHelp: false);
        isPrevented = true;
    }
    
    // 随着 ActionBar 隐藏一并隐藏, 和 ActionBar 逻辑保持一致
    private static void ToggleUIDetour(UIModule* module, UIModule.UiFlags flags, bool enable, bool unknown)
    {
        ToggleUIHook.Original(module, flags, enable, unknown);
        
        if (flags.HasAnyFlag(UIModule.UiFlags.ActionBars))
        {
            // 隐藏
            if (!enable)
            {
                IsLastQuickPanelEnabled = QuickPanel != null;
                AgentQuickPanel.Instance()->Hide();
            }
            else
            {
                if (IsLastQuickPanelEnabled && QuickPanel == null)
                    AgentQuickPanel.Instance()->OpenPanel(AgentQuickPanel.Instance()->ActivePanel);

                IsLastQuickPanelEnabled = false;
            }
        }
    }

    private static void UpdateAddonFlags()
    {
        if (QuickPanel == null) return;
        
        // 禁止 ESC 键关闭
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A1, 0x4, ModuleConfig.IsLock);
        
        // 禁止聚焦
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A0, 0x80, ModuleConfig.IsLock);
        
        // 禁止自动聚焦
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A1, 0x40, ModuleConfig.IsLock);
        
        // 禁止右键菜单
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A3, 0x1, ModuleConfig.IsLock);
        
        // 禁止交互
        FlagHelper.UpdateFlag(ref QuickPanel->Flags1A3, 0x40, !ModuleConfig.IsLock);
    }

    private class Config : ModuleConfiguration
    {
        public bool    IsLock = true;
        public Vector2 LastPosition;
    }
}
