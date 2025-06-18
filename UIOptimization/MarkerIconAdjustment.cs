using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using FFXIVClientStructs.FFXIV.Component.GUI;


namespace DailyRoutines.ModulesPublic;

public unsafe class MarkerIconAdjustment : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("MarkerIconAdjustmentTitle"), //目标标记调整
        Description = GetLoc("MarkerIconAdjustmentDescription"), //自定义目标标记的缩放与位置
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Marsh"]
    };
    private static Config ModuleConfig = null!;
    
    public class Config : ModuleConfiguration
    {
        public float Scale = 1f;
        public float PosX;
        public float PosY;
    }
    
    public override void Init()
    {
        ModuleConfig = new Config().Load(this);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw,"NamePlate", OnAddon);
        
        if (IsAddonAndNodesReady(NamePlate)) 
            OnAddon(AddonEvent.PreDraw, null);
    }
    
    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        var addon = NamePlate;
        if (!IsAddonAndNodesReady(NamePlate)) return;
        
        {
            var componentNode = addon->GetComponentNodeById(2);
        
            if (componentNode == null) return;

            var imageNode = (AtkImageNode*)componentNode->Component->UldManager.SearchNodeById(9);
            if (imageNode == null) return;
        
            imageNode->SetScale(ModuleConfig.Scale, ModuleConfig.Scale);
            var posX = ((1.5f - (ModuleConfig.Scale * 0.5f)) * 96f) + (ModuleConfig.PosX * ModuleConfig.Scale);
            var posY = 4 + (ModuleConfig.PosY * ModuleConfig.Scale);
            imageNode->SetPositionFloat(posX, posY);
        }
        
        for (uint i = 0; i < 49 ; i++)
        {
            var componentNode = addon->GetComponentNodeById(i + 20001);
        
            if (componentNode == null) return;

            var imageNode = (AtkImageNode*)componentNode->Component->UldManager.SearchNodeById(9);
            if (imageNode == null) return;
        
            imageNode->SetScale(ModuleConfig.Scale, ModuleConfig.Scale);
            var posX = ((1.5f - (ModuleConfig.Scale * 0.5f)) * 96f) + (ModuleConfig.PosX * ModuleConfig.Scale);
            var posY = 4 + (ModuleConfig.PosY * ModuleConfig.Scale);
            imageNode->SetPositionFloat(posX, posY);
        }

    }
    
    public override void ConfigUI()
    {
        ImGui.TextColored(White, GetLoc("MarkerIconAdjustment-Hint"));//拖动滑条或按住 Ctrl 后点击滑块以精确输入数值
        if (ImGui.SliderFloat(GetLoc("Scale"), ref ModuleConfig.Scale, 0f, 2f, "%.2f"))
            ModuleConfig.Save(this);

        if (ImGui.SliderFloat($"{GetLoc("IconOffset")}X", ref ModuleConfig.PosX, -100f, 100f, "%.1f"))
            ModuleConfig.Save(this);

        if (ImGui.SliderFloat($"{GetLoc("IconOffset")}Y", ref ModuleConfig.PosY, -100f, 100f, "%.1f"))
            ModuleConfig.Save(this);
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }
}
