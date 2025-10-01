using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
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
            ImGui.ColorConvertFloat4ToU32(KnownColor.LightSkyBlue.ToVector4())
        ),
        // bilibili
        new(
            @"BV[a-zA-Z0-9]{10}",
            match => Util.OpenLink($"https://www.bilibili.com/video/{match.Value}"),
            ImGui.ColorConvertFloat4ToU32(KnownColor.Pink.ToVector4())
        ),
        // 数字
        new(@"(\d{5,11})",
            match =>
            {
                var number = match.Value;

                ImGui.SetClipboardText(number);
                NotificationSuccess($"{GetLoc("CopiedToClipboard")}: {number}");
            },
            ImGui.ColorConvertFloat4ToU32(KnownColor.LightYellow.ToVector4())
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

        var resNode     = addon->GetNodeById(19);
        var buttonShare = addon->GetComponentButtonById(34);
        var locatedNode = addon->GetNodeById(30);
        var textNode    = addon->GetTextNodeById(20);
        if (resNode == null || buttonShare == null || locatedNode == null || textNode == null) return;
        
        var nodeStateInfo    = NodeState.Get(resNode);
        var nodeStateShare   = NodeState.Get((AtkResNode*)buttonShare->OwnerNode);
        var nodeStateLocated = NodeState.Get(locatedNode);

        var offsetSpacing       = ImGui.GetStyle().ItemSpacing;
        var offsetHeightSpacing = new Vector2(0f, ImGui.GetTextLineHeightWithSpacing());
        
        using var fontBefore = FontManager.UIFont80.Push();
        
        var windowPos = nodeStateInfo.Position - (3 * offsetSpacing) - offsetHeightSpacing;
        
        var width  = nodeStateShare.Position.X   - nodeStateInfo.Position.X + ImGui.GetStyle().ItemSpacing.X;
        var height = nodeStateLocated.Position.Y - windowPos.Y - offsetSpacing.Y;
        
        ImGui.SetWindowPos(windowPos);
        ImGui.SetWindowSize(new(width, height));
        
        using var fontAfter = FontManager.UIFont.Push();
        ImGuiOm.TextSelectable(textNode->NodeText.ExtractText(), width - (2 * offsetSpacing.X), LinkTypes);
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
