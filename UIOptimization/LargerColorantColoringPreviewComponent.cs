using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DailyRoutines.ModulesPublic;

public unsafe class LargerColorantColoringPreviewComponent : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("LargerColorantColoringPreviewComponentTitle"),
        Description = GetLoc("LargerColorantColoringPreviewComponentDescription"),
        Category    = ModuleCategories.UIOptimization,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    // 懒得恢复了, 就这样
    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ColorantColoring", OnAddon);
        if (IsAddonAndNodesReady(ColorantColoring))
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ColorantColoring", OnAddon);

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = ColorantColoring;
        if (addon == null) return;
        
        addon->WindowNode->SetWidth(754);
        for (var i = 0; i < addon->WindowNode->Component->UldManager.NodeListCount; i++)
        {
            var node = addon->WindowNode->Component->UldManager.NodeList[i];
            if (node == null) continue;

            if (node->Width == 654)
                node->SetWidth(754);
                        
            if (node->Width == 640)
                node->SetWidth(740);
            
            if (node->Width == 649)
                node->SetWidth(749);
            
            if (node->Width == 646)
                node->SetWidth(746);
            
            if (node->X == 621)
                node->SetXFloat(721);
        }

        var previewContainerNode = addon->GetNodeById(70);
        if (previewContainerNode != null)
            previewContainerNode->SetYFloat(56);

        var previewComponent = addon->GetComponentNodeById(71);
        if (previewComponent != null)
        {
            previewComponent->SetWidth(330);
            previewComponent->SetHeight(550);
            
            previewComponent->SetXFloat(-6);
            previewComponent->SetYFloat(1);

            var borderNode = previewComponent->Component->UldManager.SearchNodeById(3);
            if (borderNode != null)
            {
                borderNode->SetWidth(330);
                borderNode->SetHeight(550);
            }
            
            var collisionNode = previewComponent->Component->UldManager.SearchNodeById(5);
            if (collisionNode != null)
            {
                collisionNode->SetWidth(330);
                collisionNode->SetHeight(550);
            }
            
            var imageNode = previewComponent->Component->UldManager.SearchNodeById(4);
            if (imageNode != null)
            {
                imageNode->SetWidth(322);
                imageNode->SetHeight(542);
            }
        }

        var gearContainerNode = addon->GetNodeById(17);
        if (gearContainerNode != null)
            gearContainerNode->SetWidth(386);

        for (var i = 72U; i < 81; i++)
        {
            var checkBoxNode = addon->GetComponentNodeById(i);
            if (checkBoxNode == null) continue;
            
            checkBoxNode->SetXFloat(24 + (28 * (i - 72)));
            checkBoxNode->SetYFloat(556);
        }
    }
}
