using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools;
using LuminaCompanion = Lumina.Excel.Sheets.Companion;


namespace 预览坐骑衣服;


public unsafe class PreviewUnlockableItems : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "快捷预览",
        Description = "在物品详情界面预览坐骑、宠物和发型的外观图像",
        Category    = ModuleCategories.Assist,
        Author      = ["AZZ"],
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private const uint CustomNodeId = 99990001;
    private string _lastImagePath = string.Empty;

    private record ImageSize(float Width = 0, float Height = 0, float Scale = 1);

    // 物品类型定义
    private const ushort ItemTypeMount = 1322;
    private const ushort ItemTypeMinion = 853;
    private const ushort ItemTypeHairstyle = 2633;


    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(
            AddonEvent.PostUpdate, "ItemDetail", OnItemDetailUpdate);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnItemDetailUpdate);
        CleanupImageNode();
        _lastImagePath = string.Empty;
    }

    private void OnItemDetailUpdate(AddonEvent type, AddonArgs args)
    {
        var window = (AtkUnitBase*)args.Addon.Address;
        if (window == null) return;
        
        var imgNode = (AtkImageNode*)FindNodeById(&window->UldManager, CustomNodeId, NodeType.Image);
        if (imgNode != null)
            imgNode->AtkResNode.ToggleVisibility(false);

        // 拿到当前查看的物品ID
        var itemId = AgentItemDetail.Instance()->ItemId;
        if (itemId is >= 2000000 or <= 0) return;
        
        itemId %= 500000;

        var item = DService.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        var itemAction = item?.ItemAction.Value;
        if (itemAction == null) return;

        // 获取对应物品预览图
        var (imagePath, imageSize) = itemAction.Value.Type switch
        {
            ItemTypeMount => (GetMountImagePath(itemAction.Value.Data[0]), new ImageSize(190, 234, 0.8f)),
            ItemTypeMinion => (GetMinionImagePath(itemAction.Value.Data[0]), new ImageSize(100, 100, 0.8f)),
            ItemTypeHairstyle => (GetHairstylePath(itemId), new ImageSize(100, 100, 0.8f)),
            _ => (string.Empty, new ImageSize()),
        };

        if (string.IsNullOrEmpty(imagePath)) return;

        // 插入图片
        var insertPos = window->GetNodeById(2);
        var anchorPos = window->GetNodeById(47);
        if (insertPos == null || anchorPos == null) return;

        // 不存在-创建一个图片节点
        if (imgNode == null)
        {
            imgNode = CreateImageNode(window, insertPos);
            if (imgNode == null) return;
        }

        imgNode->AtkResNode.ToggleVisibility(true);

        // 加载图片
        if (imagePath != _lastImagePath)
        {
            imgNode->LoadTexture(imagePath);
            _lastImagePath = imagePath;
        }

        // 调整大小和位置
        var imgWidth = (ushort)((window->RootNode->Width - 20f) * imageSize.Scale);
        var imgHeight = (ushort)(imgWidth * imageSize.Height / imageSize.Width);

        imgNode->AtkResNode.SetWidth(imgWidth);
        imgNode->AtkResNode.SetHeight(imgHeight);

        var posX = window->RootNode->Width / 2f - imgWidth / 2f;
        var posY = anchorPos->Y + anchorPos->GetHeight() + 8;
        imgNode->AtkResNode.SetPositionFloat(posX, posY);

        // 扩大窗口
        ResizeWindowForImage(window, imgNode, insertPos);
    }

    // 获取坐骑路径
    private string GetMountImagePath(uint mountId)
    {
        var mount = DService.Data.GetExcelSheet<Mount>()?.GetRowOrDefault(mountId);
        if (mount == null) return string.Empty;

        var iconId = mount.Value.Icon + 64000U;
        return BuildIconPath(iconId);
    }

    // 获取宠物路径
    private string GetMinionImagePath(uint minionId)
    {
        var minion = DService.Data.GetExcelSheet<LuminaCompanion>()?.GetRowOrDefault(minionId);
        if (minion == null) return string.Empty;

        var iconId = minion.Value.Icon + 64000U;
        return BuildIconPath(iconId);
    }

    // 获取发型路径
    private string GetHairstylePath(uint hairstyleItemId)
    {
        // 角色的种族和性别
        var (tribeId, gender) = GetPlayerCharacterInfo();
        if (tribeId == 0) return string.Empty;

        // 种族性别找到对应的发型表
        var hairTable = DService.Data.GetExcelSheet<HairMakeType>()?.FirstOrDefault(t =>
            t.Tribe.RowId == tribeId && t.Gender == gender);

        if (hairTable?.RowId == 0 || hairTable == null) return string.Empty;

        // 表里所有发型ID
        var hairstyleIds = ReadHairstyleIds(hairTable.Value);
        if (hairstyleIds.Count == 0) return string.Empty;

        // 匹配发型
        var customizeSheet = DService.Data.GetExcelSheet<CharaMakeCustomize>();
        if (customizeSheet == null) return string.Empty;

        foreach (var id in hairstyleIds)
        {
            var customize = customizeSheet.GetRowOrDefault(id);
            if (customize == null) continue;

            if (customize.Value.HintItem.RowId == hairstyleItemId)
            {
                var iconId = customize.Value.Icon;
                if (iconId == 0) return string.Empty;
                return BuildIconPath(iconId);
            }
        }

        return string.Empty;
    }

    // 当前角色的种族和性别
    private (byte tribeId, byte gender) GetPlayerCharacterInfo()
    {
        var playerAddr = DService.ClientState.LocalPlayer?.Address;
        if (playerAddr == null) return (0, 0);

        var character = (Character*)playerAddr;

        var tribeId = character->DrawData.CustomizeData.Tribe;
        var gender = character->DrawData.CustomizeData.Sex;

        // 幻化状态
        if (character->DrawObject != null &&
            character->DrawObject->Object.GetObjectType() == ObjectType.CharacterBase)
        {
            var charBase = (CharacterBase*)character->DrawObject;
            if (charBase->GetModelType() == CharacterBase.ModelType.Human)
            {
                var human = (Human*)charBase;
                tribeId = human->Customize.Tribe;
                gender = human->Customize.Sex;
            }
        }

        return (tribeId, gender);
    }

    // 从HairMakeType里读发型ID列表
    private List<uint> ReadHairstyleIds(HairMakeType hairTable)
    {
        var ids = new List<uint>();

        try
        {
            var tableType = typeof(HairMakeType);

            // 通过反射拿到内部字段
            var pageField = tableType.GetField("<page>P",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var offsetField = tableType.GetField("<offset>P",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (pageField != null && offsetField != null)
            {
                var page = (Lumina.Excel.ExcelPage?)pageField.GetValue(hairTable);
                var offset = (uint?)offsetField.GetValue(hairTable);

                if (page != null && offset.HasValue)
                {
                    // 从内存里一个一个读ID，直到遇到0
                    for (var i = 0U; i < 100; i++)
                    {
                        var id = page.ReadUInt32(offset.Value + 0xC + 4 * i);
                        if (id == 0) break;
                        ids.Add(id);
                    }
                }
            }
        }
        catch
        {
            //空列表
        }

        return ids;
    }

    // 调整窗口大小
    private void ResizeWindowForImage(AtkUnitBase* window, AtkImageNode* imgNode, AtkResNode* insertNode)
    {
        var newHeight = (ushort)(imgNode->AtkResNode.Y + imgNode->AtkResNode.GetHeight() + 16);

        window->WindowNode->AtkResNode.SetHeight(newHeight);

        // 背景
        var bgNode = window->WindowNode->Component->UldManager.SearchNodeById(2);
        if (bgNode != null)
            bgNode->SetHeight(newHeight);

        // 底部按钮
        insertNode->SetPositionFloat(insertNode->X, newHeight - 20);

        window->RootNode->SetHeight(newHeight);
    }

    //图标路径生成
    private string BuildIconPath(uint iconId, bool useHighRes = true)
    {
        var suffix = useHighRes ? "_hr1.tex" : ".tex";
        return $"ui/icon/{iconId / 1000 * 1000:000000}/{iconId:000000}{suffix}";
    }

    // 创建图片节点
    private AtkImageNode* CreateImageNode(AtkUnitBase* window, AtkResNode* insertNode)
    {
        // 图片节点
        var imgNode = IMemorySpace.GetUISpace()->Create<AtkImageNode>();
        if (imgNode == null) return null;

        // 节点基本属性
        imgNode->AtkResNode.Type = NodeType.Image;
        imgNode->AtkResNode.NodeId = CustomNodeId;
        imgNode->AtkResNode.NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft;
        imgNode->AtkResNode.DrawFlags = 0;
        imgNode->WrapMode = 1;
        imgNode->Flags = 128;

        // UI系统结构
        var partsList = (AtkUldPartsList*)IMemorySpace.GetUISpace()->Malloc(
            (ulong)sizeof(AtkUldPartsList), 8);
        if (partsList == null)
        {
            imgNode->AtkResNode.Destroy(true);
            return null;
        }

        partsList->Id = 0;
        partsList->PartCount = 1;

        // 图片的坐标和大小
        var part = (AtkUldPart*)IMemorySpace.GetUISpace()->Malloc(
            (ulong)sizeof(AtkUldPart), 8);
        if (part == null)
        {
            IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
            imgNode->AtkResNode.Destroy(true);
            return null;
        }

        part->U = 0;
        part->V = 0;
        part->Width = 256;
        part->Height = 256;

        partsList->Parts = part;

        // 分配Asset，用来存纹理
        var asset = (AtkUldAsset*)IMemorySpace.GetUISpace()->Malloc(
            (ulong)sizeof(AtkUldAsset), 8);
        if (asset == null)
        {
            IMemorySpace.Free(part, (ulong)sizeof(AtkUldPart));
            IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
            imgNode->AtkResNode.Destroy(true);
            return null;
        }

        asset->Id = 0;
        asset->AtkTexture.Ctor();
        part->UldAsset = asset;
        imgNode->PartsList = partsList;

        // 把节点插入到UI里
        var prevNode = insertNode->PrevSiblingNode;
        imgNode->AtkResNode.ParentNode = insertNode->ParentNode;

        insertNode->PrevSiblingNode = (AtkResNode*)imgNode;
        if (prevNode != null)
            prevNode->NextSiblingNode = (AtkResNode*)imgNode;

        imgNode->AtkResNode.PrevSiblingNode = prevNode;
        imgNode->AtkResNode.NextSiblingNode = insertNode;

        // 刷新UI
        window->UldManager.UpdateDrawNodeList();

        return imgNode;
    }

    // 清理图片节点，释放所有分配的内存
    private void CleanupImageNode()
    {
        var addon = DService.Gui.GetAddonByName("ItemDetail");
        if (addon == nint.Zero) return;

        var window = (AtkUnitBase*)addon.Address;
        var imgNode = (AtkImageNode*)FindNodeById(&window->UldManager, CustomNodeId, NodeType.Image);
        if (imgNode == null) return;

        // 从UI里移除
        if (imgNode->AtkResNode.PrevSiblingNode != null)
            imgNode->AtkResNode.PrevSiblingNode->NextSiblingNode = imgNode->AtkResNode.NextSiblingNode;
        if (imgNode->AtkResNode.NextSiblingNode != null)
            imgNode->AtkResNode.NextSiblingNode->PrevSiblingNode = imgNode->AtkResNode.PrevSiblingNode;

        window->UldManager.UpdateDrawNodeList();

        // 释放所有分配的内存
        if (imgNode->PartsList != null)
        {
            if (imgNode->PartsList->Parts != null)
            {
                if (imgNode->PartsList->Parts->UldAsset != null)
                    IMemorySpace.Free(imgNode->PartsList->Parts->UldAsset, (ulong)sizeof(AtkUldAsset));

                IMemorySpace.Free(imgNode->PartsList->Parts, (ulong)sizeof(AtkUldPart));
            }

            IMemorySpace.Free(imgNode->PartsList, (ulong)sizeof(AtkUldPartsList));
        }

        imgNode->AtkResNode.Destroy(true);
    }

    // 在UI里找指定ID的节点
    private AtkResNode* FindNodeById(AtkUldManager* uldManager, uint nodeId, NodeType? type = null)
    {
        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var node = uldManager->NodeList[i];
            if (node == null) continue;

            if (node->NodeId == nodeId && (type == null || node->Type == type.Value))
                return node;
        }
        return null;
    }
}
