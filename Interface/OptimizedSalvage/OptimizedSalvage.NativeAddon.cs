using System.Globalization;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class OptimizedSalvage
{
    private readonly Dictionary<nint, SkillColumnEntry> skillColumnNodes = [];

    private TextNode?         skillColumnHeader;
    private AtkComponentList* skillListComponent;

    private void OnSalvageItemSelector
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                skillColumnHeader  = null;
                skillListComponent = null;
                skillColumnNodes.Clear();
                break;
            case AddonEvent.PostSetup:
            case AddonEvent.PostDraw:
                RefreshSkillColumn();
                break;
        }
    }

    private void RefreshSkillColumn()
    {
        if (!SalvageItemSelector->IsAddonAndNodesReady()) return;

        if (skillListComponent == null)
            CreateSkillColumn(SalvageItemSelector);

        if (skillListComponent == null) return;

        for (var i = 0; i < skillListComponent->AllocatedItemRendererListLength; i++)
        {
            var listItemRenderer = skillListComponent->ItemRendererList[i].AtkComponentListItemRenderer;
            if (listItemRenderer == null) continue;

            var index = listItemRenderer->ListItemIndex;
            if (index < 0 || index >= skillListComponent->ListLength) continue;

            UpdateSkillColumn(index, listItemRenderer);
        }
    }

    private void CreateSkillColumn
    (
        AtkUnitBase* hostAddon
    )
    {
        var headerContainer = hostAddon->GetNodeById(7);
        var listNode        = (AtkComponentNode*)hostAddon->GetNodeById(12);

        if (headerContainer == null || listNode == null) return;
        if (listNode->Component == null) return;

        AtkResNode* rightmostHeaderText = null;

        for (var node = headerContainer->ChildNode; node != null; node = node->PrevSiblingNode)
        {
            if (node->GetNodeType() is not NodeType.Text) continue;
            if (rightmostHeaderText == null || node->GetXShort() > rightmostHeaderText->GetXShort())
                rightmostHeaderText = node;
        }

        if (rightmostHeaderText == null) return;

        var headerTextNode = rightmostHeaderText->GetAsAtkTextNode();
        if (headerTextNode == null) return;

        if (!ApplySkillColumnLayout(hostAddon, SKILL_COLUMN_WIDTH)) return;

        skillColumnHeader = new TextNode
        {
            Position         = new(rightmostHeaderText->GetXShort() + rightmostHeaderText->GetWidth(), rightmostHeaderText->GetYShort()),
            Size             = new(SKILL_COLUMN_WIDTH, rightmostHeaderText->GetHeight()),
            String           = Lang.Get("OptimizedSalvage-DesynthesisSkill"),
            AlignmentType    = headerTextNode->AlignmentType,
            FontType         = headerTextNode->FontType,
            FontSize         = headerTextNode->FontSize,
            LineSpacing      = headerTextNode->LineSpacing,
            TextColor        = headerTextNode->TextColor.ToVector4(),
            TextOutlineColor = headerTextNode->EdgeColor.ToVector4(),
            BackgroundColor  = headerTextNode->BackgroundColor.ToVector4(),
            TextFlags        = headerTextNode->TextFlags
        };
        skillColumnHeader.AttachNode(rightmostHeaderText, NodePosition.AfterTarget);

        var header = skillColumnHeader;
        header.ColorThemeChanged += () =>
        {
            header.TextColor        = headerTextNode->TextColor.ToVector4();
            header.TextOutlineColor = headerTextNode->EdgeColor.ToVector4();
            header.BackgroundColor  = headerTextNode->BackgroundColor.ToVector4();
        };

        skillListComponent = (AtkComponentList*)listNode->Component;
    }

    private void RemoveSkillColumn()
    {
        skillColumnHeader?.Dispose();
        skillColumnHeader = null;

        foreach (var entry in skillColumnNodes.Values)
            entry.Node.Dispose();

        skillColumnNodes.Clear();

        if (skillListComponent == null) return;

        if (SalvageItemSelector->IsAddonAndNodesReady())
            ApplySkillColumnLayout(SalvageItemSelector, -SKILL_COLUMN_WIDTH);

        skillListComponent = null;
    }

    private static bool ApplySkillColumnLayout
    (
        AtkUnitBase* hostAddon,
        int          widthDelta
    )
    {
        var headerContainer = hostAddon->GetNodeById(7);
        var separator       = hostAddon->GetNodeById(11);
        var listNode        = (AtkComponentNode*)hostAddon->GetNodeById(12);
        var windowNode      = (AtkComponentNode*)hostAddon->GetNodeById(14);

        if (headerContainer     == null || separator             == null || listNode == null || windowNode == null) return false;
        if (listNode->Component == null || windowNode->Component == null) return false;

        headerContainer->SetWidth((ushort)(headerContainer->GetWidth() + widthDelta));
        separator->SetWidth((ushort)(separator->GetWidth()             + widthDelta));

        hostAddon->RootNode->SetWidth((ushort)(hostAddon->RootNode->GetWidth()             + widthDelta));
        ((AtkResNode*)windowNode)->SetWidth((ushort)(((AtkResNode*)windowNode)->GetWidth() + widthDelta));

        foreach (var nodeID in (uint[])[2, 8, 9, 10, 11, 12, 13])
        {
            var node = windowNode->Component->UldManager.SearchNodeById(nodeID);
            if (node == null) continue;

            node->SetWidth((ushort)(node->GetWidth() + widthDelta));
        }

        foreach (var nodeID in (uint[])[5, 6, 7])
        {
            var node = windowNode->Component->UldManager.SearchNodeById(nodeID);
            if (node == null) continue;

            node->SetXShort((short)(node->GetXShort() + widthDelta));
        }

        ((AtkResNode*)listNode)->SetWidth((ushort)(((AtkResNode*)listNode)->GetWidth() + widthDelta));

        var listUldManager = listNode->Component->UldManager;

        for (var i = 0; i < listUldManager.NodeListCount; i++)
        {
            var node = listUldManager.NodeList[i];
            if (node == null) continue;

            // 列表滚动条
            if (node->NodeId is 5)
            {
                node->SetXShort((short)(node->GetXShort() + widthDelta));
                continue;
            }

            node->SetWidth((ushort)(node->GetWidth() + widthDelta));
        }

        return true;
    }

    private void UpdateSkillColumn
    (
        int                           index,
        AtkComponentListItemRenderer* listItemRenderer
    )
    {
        var key = (nint)listItemRenderer;

        if (!skillColumnNodes.TryGetValue(key, out var entry))
        {
            var skillNode = CreateSkillColumnNode(listItemRenderer);
            if (skillNode == null) return;

            entry = new()
            {
                Node         = skillNode,
                DefaultColor = skillNode.TextColor
            };
            skillColumnNodes[key] = entry;

            var skillEntry = entry;
            skillNode.ColorThemeChanged += () =>
            {
                var baseNode = listItemRenderer->UldManager.SearchNodeById(6);
                if (baseNode == null)
                    return;

                var baseTextNode = baseNode->GetAsAtkTextNode();
                if (baseTextNode == null)
                    return;

                skillEntry.DefaultColor    = baseTextNode->TextColor.ToVector4();
                var (_, themedColor)       = GetSkillColumnDisplay(listItemRenderer->ListItemIndex, skillEntry.DefaultColor);
                skillNode.TextColor        = themedColor;
                skillNode.TextOutlineColor = baseTextNode->EdgeColor.ToVector4();
                skillNode.BackgroundColor  = baseTextNode->BackgroundColor.ToVector4();
            };
        }

        var (text, color) = GetSkillColumnDisplay(index, entry.DefaultColor);
        if (entry.Text == text) return;

        entry.Text           = text;
        entry.Node.String    = text;
        entry.Node.TextColor = color;
    }

    private static TextNode? CreateSkillColumnNode
    (
        AtkComponentListItemRenderer* listItemRenderer
    )
    {
        // 行内最后一列文本
        var baseNode = listItemRenderer->UldManager.SearchNodeById(6);
        if (baseNode == null) return null;

        var baseTextNode = baseNode->GetAsAtkTextNode();
        if (baseTextNode == null) return null;

        var skillNode = new TextNode
        {
            Position         = new(baseNode->GetXShort() + baseNode->GetWidth(), baseNode->GetYShort()),
            Size             = new(SKILL_COLUMN_WIDTH, baseNode->GetHeight()),
            AlignmentType    = AlignmentType.Right,
            FontType         = baseTextNode->FontType,
            FontSize         = baseTextNode->FontSize,
            LineSpacing      = baseTextNode->LineSpacing,
            TextColor        = baseTextNode->TextColor.ToVector4(),
            TextOutlineColor = baseTextNode->EdgeColor.ToVector4(),
            BackgroundColor  = baseTextNode->BackgroundColor.ToVector4()
        };
        skillNode.AttachNode(listItemRenderer->UldManager.RootNode);

        return skillNode;
    }

    private static (string Text, Vector4 Color) GetSkillColumnDisplay
    (
        int     index,
        Vector4 defaultColor
    )
    {
        if (!TryGetDesynthesisInfo(index, out var skillLevel, out var itemLevel))
            return (string.Empty, defaultColor);

        var color = defaultColor;
        if (skillLevel > itemLevel + 50)
            color = ColorHelper.GetColor(COLOR_LEVEL_TOO_HIGH);
        else if (skillLevel > itemLevel)
            color = ColorHelper.GetColor(COLOR_LEVEL_HIGH);

        return (string.Create(CultureInfo.InvariantCulture, $"{skillLevel:F2}/{itemLevel:F2}"), color);
    }

    private static bool TryGetDesynthesisInfo
    (
        int       index,
        out float skillLevel,
        out float itemLevel
    )
    {
        skillLevel = 0;
        itemLevel  = 0;

        var agent = AgentSalvage.Instance();
        if (agent == null) return false;
        if (index < 0 || index >= agent->ItemCount) return false;

        var itemData = agent->ItemList[index];
        var slot     = InventoryManager.Instance()->GetInventorySlot(itemData.InventoryType, (int)itemData.InventorySlot);
        if (slot == null) return false;

        if (!LuminaGetter.TryGetRow(slot->GetBaseItemId(), out Item item)) return false;
        if (item.Desynth == 0) return false;

        skillLevel = PlayerState.Instance()->GetDesynthesisLevel(item.ClassJobRepair.RowId);
        itemLevel  = item.LevelItem.RowId;

        return true;
    }

    private sealed class SkillColumnEntry
    {
        public required TextNode Node         { get; init; }
        public required Vector4  DefaultColor { get; set; }

        public string Text { get; set; } = string.Empty;
    }
}
