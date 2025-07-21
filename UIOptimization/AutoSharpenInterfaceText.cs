using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSharpenInterfaceText : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoSharpenInterfaceTextTitle"),
        Description = GetLoc("AutoSharpenInterfaceTextDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static readonly Dictionary<string, TextNodeInfo[]> TextWindows = new()
    {
        ["LookingForGroupDetail"]    = [new(20)],
        ["LookingForGroupCondition"] = [new(22, 16) { TextFlags1 = 224, TextFlags2 = 1 }],
        ["RetainerInputString"]      = [new(3, 16) { TextFlags1  = 224, TextFlags2 = 1 }],
        ["FreeCompanyInputString"] =
        [
            new(3, 16) { TextFlags1 = 224, TextFlags2 = 1 },
            new(2, 16) { TextFlags1 = 224, TextFlags2 = 1 }
        ],
        ["HousingEditMessage"] = [new(2, 16) { TextFlags1 = 224, TextFlags2 = 1 },],
        ["HousingSignBoard"]   = [new(28)],
        ["FreeCompanyProfile"] = [new(30)],
        ["ConfigSystem"]       = [new(516) { TextFlags1 = 32, TextFlags2 = 0 }]
    };

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, TextWindows.Keys, OnTextAddon);

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnTextAddon);

        foreach (var (window, infos) in TextWindows)
        {
            if (!TryGetAddonByName(window, out var addon))  continue;

            var infosCopy = infos;
            ModifyTextNode(addon, ref infosCopy, false);
            TextWindows[window] = infosCopy;
        }
    }
    
    private static void OnTextAddon(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon;
        if (addon == null) return;

        if (!TextWindows.TryGetValue(args.AddonName, out var infos)) return;

        ModifyTextNode(addon, ref infos, true);
    }
    
    private static void ModifyTextNode(AtkUnitBase* addon, ref TextNodeInfo[] infos, bool isAdd)
    {
        if (addon == null) return;
        
        foreach (var info in infos)
        {
            var node = addon->GetNodeById(info.NodeID[0]);
            foreach (var id in info.NodeID[1..])
            {
                if (node is null) continue;
                node = node->GetComponent()->UldManager.SearchNodeById(id);
            }

            var textNode = node->GetAsAtkTextNode();
            if (textNode is null) continue;
            
            if (!info.Modified)
            {
                info.TextFlag1Original = textNode->TextFlags;
                info.TextFlag2Original = textNode->TextFlags2;
                info.FontSize          = textNode->FontSize;
            }

            if (isAdd)
            {
                textNode->TextFlags  = info.TextFlags1;
                textNode->TextFlags2 = info.TextFlags2;
                info.Modified        = true;
            }
            else
            {
                if (info.TextFlag1Original != null)
                    textNode->TextFlags = (byte)info.TextFlag1Original;

                if (info.TextFlag2Original != null)
                    textNode->TextFlags2 = (byte)info.TextFlag2Original;

                if (info.FontSize != null)
                    textNode->FontSize = (byte)info.FontSize;

                info.Modified = false;
            }
        }
    }
    
    private class TextNodeInfo(uint nodeId, params uint[] nodeIds)
    {
        public uint[] NodeID            { get; set; } = [nodeId, .. nodeIds];
        public byte?  TextFlag1Original { get; set; }
        public byte?  TextFlag2Original { get; set; }
        public byte   TextFlags1        { get; set; } = 195;
        public byte   TextFlags2        { get; set; }
        public byte?  FontSize          { get; set; }
        public bool   Modified          { get; set; }
    }
}
