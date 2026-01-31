using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Helpers;

namespace DailyRoutines.ModulesPublic;

public unsafe class easyRelicQuest : DailyModuleBase
{
    // 定义模块基础信息
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("easyRelicQuestTitle"),
        Description = GetLoc("easyRelicQuestDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["PunishXIV/yimo"]
    };

    // 定义模块权限
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static List<(uint[] RelicQuestId, string RelicStep)> SimpleRelics = null!;

    private static Dictionary<uint, string> QuestNameCache = null!;


    // 模块初始化方法
    protected override void Init()
    {
        BuildSimpleRelicsAndCache();
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreUpdate, ["SelectIconString", "SelectString"], OnAddon);
    }


    // 模块卸载方法
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        QuestNameCache = null!;
        SimpleRelics = null!;
    }


    // 构建任务数据和任务名称缓存
    private static void BuildSimpleRelicsAndCache()
    {
        var data = DService.Instance().Data;
        var classJobCategory = data.GetExcelSheet<ClassJobCategory>();

        // 定义任务ID数组和对应的步骤名称映射关系
        SimpleRelics =
        [
            ([69381, 70189, 70267, 69429, 67748], "Step 1"),
            ([69506, 70262, 70268, 69430, 67749], "Step 2"),
            ([69507, 70308, 70304, 69519, 67750], "Step 3"),
            ([69574, 70305, 67820, 70343], "Step 4"),
            ([69576, 70339, 67864], "Step 5"),
            ([69637, 70340, 67915], "Step 6"),
            ([67932], "Step 7"),
            ([67940], "Step 8"),
            ([66655], classJobCategory.GetRow(22).Name.ToString()),
            ([66656], classJobCategory.GetRow(20).Name.ToString()),
            ([66657], classJobCategory.GetRow(21).Name.ToString()),
            ([66658], classJobCategory.GetRow(23).Name.ToString()),
            ([66659], classJobCategory.GetRow(26).Name.ToString()),
            ([66660], classJobCategory.GetRow(25).Name.ToString()),
            ([66661], classJobCategory.GetRow(24).Name.ToString()),
            ([66662], classJobCategory.GetRow(28).Name.ToString()),
            ([66663], classJobCategory.GetRow(29).Name.ToString()),
            ([67115], classJobCategory.GetRow(92).Name.ToString()),
        ];
        
        QuestNameCache = new Dictionary<uint, string>();
        foreach (var (ids, _) in SimpleRelics)
        {
            foreach (var qid in ids)
            {
                if (QuestNameCache.ContainsKey(qid)) continue;
                if (LuminaGetter.TryGetRow<Quest>(qid, out var qrow))
                {
                    QuestNameCache[qid] = qrow.Name.ToString();
                }
            }
        }
    }
    
    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (args.AddonName)
        {
            case "SelectIconString":
            {
                var addon = (AddonSelectIconString*)args.Addon.Address;
                if (addon == null) break;
                var list = addon->PopupMenu.PopupMenu.List;
                if (list == null) break;
                
                try
                {
                    for (var i = 0; i < list->ListLength; i++)
                    {
                        var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
                        if (renderer == null) continue;
                        var buttonTextNode = renderer->AtkComponentButton.ButtonTextNode;
                        if (buttonTextNode == null) continue;
                        UpdateAddonText(buttonTextNode);
                    }
                }
                catch (Exception e)
                {
                    DService.Instance().Log.Error(e, "模块 easyRelicQuest 处理 SelectIconString 插件时发生异常");
                }

                break;
            }

            case "SelectString":
            {
                var addon = (AddonSelectString*)args.Addon.Address;
                if (addon == null) break;
                var list = addon->PopupMenu.PopupMenu.List;
                if (list == null) break;
             
                try
                {
                    for (var i = 0; i < list->ListLength; i++)
                    {
                        var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
                        if (renderer == null) continue;
                        var buttonTextNode = renderer->AtkComponentButton.ButtonTextNode;
                        if (buttonTextNode == null) continue;
                        UpdateAddonText(buttonTextNode);
                    }
                }
                catch (Exception e)
                {
                    DService.Instance().Log.Error(e, "模块 easyRelicQuest 处理 SelectString 插件时发生异常");
                }

                break;
            }
        }
    }

    
    private void UpdateAddonText(AtkTextNode* buttonTextNode)
    {
        try
        {
            var raw = buttonTextNode->NodeText.ToString();
            if (string.IsNullOrEmpty(raw)) return;
            
            if (raw.Contains("Step", StringComparison.OrdinalIgnoreCase)) return;

            foreach (var (ids, step) in SimpleRelics)
            {
                foreach (var qid in ids)
                {
                    if (!QuestNameCache.TryGetValue(qid, out var qname)) continue;
                    if (raw.Contains(qname, StringComparison.Ordinal))
                    {
                        var newText = $"{step}: {qname}";
                        buttonTextNode->SetText(newText);
                        return;
                    }
                }
            }
        }
        catch (Exception e)
        {
            DService.Instance().Log.Error(e, "模块 easyRelicQuest 更新弹出文本时发生异常");
        }
    }
}
