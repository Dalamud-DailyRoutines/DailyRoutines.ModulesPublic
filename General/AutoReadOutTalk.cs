using System.Threading;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public class AutoReadOutTalk : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("AutoReadOutTalkTitle"),
        Description     = GetLoc("AutoReadOutTalkDescription"),
        Category        = ModuleCategories.General,
        ModulesConflict = ["AutoTalkSkip"]
    };

    private static Config ModuleConfig = null!;
    
    private static CancellationTokenSource? CancelSource;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Talk", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreHide,     "Talk", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        
        CancelBefore();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Format"));

        using (ImRaii.PushIndent())
        {
            ImGui.InputText($"##FormatInput", ref ModuleConfig.Format);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostRefresh:
                string? line    = null;
                string? speaker = null;
        
                unsafe
                {
                    if (Talk == null) return;
        
                    // 没有实际文本
                    if (Talk->AtkValues[0].Type != ValueType.ManagedString || !Talk->AtkValues[0].String.HasValue) return;
                    
                    // 没有说话人
                    if (Talk->AtkValues[1].Type == ValueType.ManagedString && Talk->AtkValues[1].String.HasValue)
                        speaker = Talk->AtkValues[1].String.ExtractText();

                    // 非普通对话
                    if (Talk->AtkValues[3].Type != ValueType.UInt || Talk->AtkValues[3].UInt != 0) return;
            
                    line = Talk->AtkValues[0].String.ExtractText();
                }
        
                if (string.IsNullOrEmpty(line)) return;
        
                CancelBefore();

                CancelSource = new();
                DService.Instance().Framework.RunOnTick
                (
                    async () => await SpeakAsync(string.Format(ModuleConfig.Format, speaker, line)),
                    cancellationToken: CancelSource.Token
                );
                break;
            
            case AddonEvent.PreFinalize:
            case AddonEvent.PreHide:
                CancelBefore();
                break;
        }
    }

    private static void CancelBefore()
    {
        if (CancelSource == null) return;
        
        if (!CancelSource.IsCancellationRequested)
            CancelSource.Cancel();
            
        CancelSource.Dispose();
        CancelSource = null;
        
        StopSpeak();
    }

    private class Config : ModuleConfiguration
    {
        public string Format = "{0}: {1}";
    }
}
