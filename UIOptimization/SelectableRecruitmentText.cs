using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class SelectableRecruitmentText : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("SelectableRecruitmentTextTitle"),
        Description = GetLoc("SelectableRecruitmentTextDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static readonly List<TextSelectableLinkTypeInfo> LinkTypes =
    [
        // http
        new(
            @"(https?:\/\/[^\s]+)|((www\.)?([a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}(:[0-9]{1,5})?(\/[^\s]*)?)",
            match =>
            {
                var url = match.Value;
                if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                    url = "http://" + url;

                Util.OpenLink(url);
            },
            ImGui.ColorConvertFloat4ToU32(LightSkyBlue)
        ),
        // bilibili
        new(
            @"BV[a-zA-Z0-9]{10}",
            match => Util.OpenLink($"https://www.bilibili.com/video/{match.Value}"),
            ImGui.ColorConvertFloat4ToU32(Pink)
        ),
        // 数字
        new(@"(\d{5,11})",
            match =>
            {
                var number = match.Value;

                ImGui.SetClipboardText(number);
                NotificationSuccess($"{GetLoc("CopiedToClipboard")}: {number}");
            },
            ImGui.ColorConvertFloat4ToU32(LightYellow)
        )
    ];

    protected override void Init()
    {
        Overlay       ??= new(this);
        Overlay.Flags &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags |= ImGuiWindowFlags.NoResize          | ImGuiWindowFlags.NoScrollbar |
                         ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoMove;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroupDetail", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddon);
        if (IsAddonAndNodesReady(LookingForGroupDetail)) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void OverlayUI()
    {
        var addon = LookingForGroupDetail;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var resNode  = addon->GetNodeById(19);
        var textNode = addon->GetTextNodeById(20);
        if (resNode == null || textNode == null) return;

        using var fontBefore = FontManager.UIFont80.Push();
        var nodeState = NodeState.Get(resNode);

        var offsetSpacing       = 3 * ImGui.GetStyle().ItemSpacing;
        var offsetHeightSpacing = new Vector2(0f, ImGui.GetTextLineHeightWithSpacing());
        
        ImGui.SetWindowPos(nodeState.Position - offsetSpacing - offsetHeightSpacing);
        ImGui.SetWindowSize(nodeState.Size    + (2 * offsetSpacing) + offsetHeightSpacing);
        
        using var fontAfter = FontManager.UIFont.Push();
        ImGuiOm.TextSelectable(textNode->NodeText.ExtractText(), nodeState.Size.X, LinkTypes);
    }
    
    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }
}
