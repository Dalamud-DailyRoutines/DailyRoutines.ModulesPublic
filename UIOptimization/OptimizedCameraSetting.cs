using System;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedCameraSetting : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedCameraSettingTitle"),
        Description = GetLoc("OptimizedCameraSettingDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static readonly CompSig                         AgentCameraSettingReceiveEventSig = new("E8 ?? ?? ?? ?? 0F B6 F8 EB 34");
    private delegate        byte                            AgentCameraSettingReceiveEventDelegate(AgentInterface* agent, AtkValue* values, uint valueCount, nint a4);
    private static          Hook<AgentCameraSettingReceiveEventDelegate> AgentCameraSettingReceiveEventHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        Overlay ??= new(this);

        AgentCameraSettingReceiveEventHook = AgentCameraSettingReceiveEventSig.GetHook<AgentCameraSettingReceiveEventDelegate>(AgentCameraSettingReceiveEventDetour);
        AgentCameraSettingReceiveEventHook.Enable();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "CameraSetting", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CameraSetting", OnAddon);
        if (IsAddonAndNodesReady(CameraSetting))
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }

    protected override void OverlayUI()
    {
        if (CameraSetting == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var node = CameraSetting->GetTextNodeById(25);
        if (node == null || !GetNodeVisible((AtkResNode*)node))
        {
            Overlay.Flags |= ImGuiWindowFlags.NoBackground;
            return;
        }
        
        Overlay.Flags &= ~ImGuiWindowFlags.NoBackground;
        
        ImGui.TextColored(LightSkyBlue, LuminaWrapper.GetAddonText(5972));

        using (ImRaii.PushIndent())
        {
            // 远近
            ImGui.Text($"{LuminaWrapper.GetAddonText(5935)} {GetLoc("StepSize")}");

            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            ImGuiOm.InputUInt("###AngleOfView", ref ModuleConfig.AngleofViewStepSize, 1, 1);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        
            // 远近
            ImGui.Text($"{LuminaWrapper.GetAddonText(5936)} {GetLoc("StepSize")}");

            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            ImGuiOm.InputUInt("###RollAngle", ref ModuleConfig.RollAngleStepSize, 1, 1);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }
    }
    
    private static byte AgentCameraSettingReceiveEventDetour(AgentInterface* agent, AtkValue* values, uint valueCount, nint a4)
    {
        switch (values->Int)
        {
            // 镜头远近减少
            case 90:
            {
                var original = values[1].UInt;
                var adjusted = MathF.Max(0, original - ModuleConfig.AngleofViewStepSize);

                SendEvent(AgentId.CameraSetting, 1, 89, (uint)adjusted, values[2], values[3], values[4]);
                return 0;
            }
            // 镜头远近增加
            case 91:
            {
                var original     = values[1].UInt;
                var adjusted = MathF.Min(200, original + ModuleConfig.AngleofViewStepSize);
                
                SendEvent(AgentId.CameraSetting, 1, 89, (uint)adjusted, values[2], values[3], values[4]);
                return 0;
            }
            // 镜头旋转减少
            case 93:
            {
                var original = values[1].Int;
                var adjusted = MathF.Max(-180, original - ModuleConfig.RollAngleStepSize);

                SendEvent(AgentId.CameraSetting, 1, 92, (int)adjusted, values[2], values[3], values[4]);
                return 0;
            }
            // 镜头旋转减少
            case 94:
            {
                var original = values[1].Int;
                var adjusted = MathF.Min(180, original + ModuleConfig.RollAngleStepSize);

                SendEvent(AgentId.CameraSetting, 1, 92, (int)adjusted, values[2], values[3], values[4]);
                return 0;
            }
        }
        
        return AgentCameraSettingReceiveEventHook.Original(agent, values, valueCount, a4);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
    }

    private class Config : ModuleConfiguration
    {
        public uint AngleofViewStepSize = 25;
        public uint RollAngleStepSize   = 45;
    }
}
