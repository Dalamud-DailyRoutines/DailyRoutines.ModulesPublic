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


namespace DailyRoutines.ModulesPublic;


public unsafe class PreviewUnlockableItems : DailyModuleBase
{
    private const uint CustomNodeid = 99990001;
    private const ushort ItemTypeMount = 1322;
    private const ushort ItemTypeMinion = 853;
    private const ushort ItemTypeHairstyle = 2633;

    private string lastImagePath = string.Empty;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("PreviewUnlockableItemsTitle"),
        Description = GetLoc("PreviewUnlockableItemsDescription"),
        Category    = ModuleCategories.Assist,
        Author      = ["AZZ"],
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private record ImageSize(float Width = 0, float Height = 0, float Scale = 1);


    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(
            AddonEvent.PostUpdate, "ItemDetail", OnItemDetailUpdate);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnItemDetailUpdate);
        CleanupImageNode();
        lastImagePath = string.Empty;
    }

    private void OnItemDetailUpdate(AddonEvent type, AddonArgs args)
    {
        var window = (AtkUnitBase*)args.Addon.Address;
        if (window == null) return;
        
        var imgNode = (AtkImageNode*)FindNodeByid(&window->UldManager, CustomNodeid, NodeType.Image);
        if (imgNode != null)
            imgNode->AtkResNode.ToggleVisibility(false);

        // 拿到当前查看的物品id
        var itemid = AgentItemDetail.Instance()->ItemId;
        if (itemid is >= 2000000 or <= 0) return;
        
        itemid %= 500000;

        var item = DService.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemid);
        var itemAction = item?.ItemAction.Value;
        if (itemAction == null) return;

        // 获取对应物品预览图
        var (imagePath, imageSize) = itemAction.Value.Type switch
        {
            ItemTypeMount => (GetMountImagePath(itemAction.Value.Data[0]), new ImageSize(190, 234, 0.8f)),
            ItemTypeMinion => (GetMinionImagePath(itemAction.Value.Data[0]), new ImageSize(100, 100, 0.8f)),
            ItemTypeHairstyle => (GetHairstylePath(itemid), new ImageSize(100, 100, 0.8f)),
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
        if (imagePath != lastImagePath)
        {
            imgNode->LoadTexture(imagePath);
            lastImagePath = imagePath;
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
    private string GetMountImagePath(uint mountid)
    {
        var mount = DService.Data.GetExcelSheet<Mount>()?.GetRowOrDefault(mountid);
        if (mount == null) return string.Empty;

        var iconid = mount.Value.Icon + 64000U;
        return BuildIconPath(iconid);
    }

    // 获取宠物路径
    private string GetMinionImagePath(uint minionid)
    {
        var minion = DService.Data.GetExcelSheet<LuminaCompanion>()?.GetRowOrDefault(minionid);
        if (minion == null) return string.Empty;

        var iconid = minion.Value.Icon + 64000U;
        return BuildIconPath(iconid);
    }

    // 获取发型路径
    private string GetHairstylePath(uint hairstyleitemid)
    {
        // 角色的种族和性别
        var (tribeid, gender) = GetPlayerCharacterInfo();
        if (tribeid == 0) return string.Empty;

        // 种族性别找到对应的发型表
        var hairTable = DService.Data.GetExcelSheet<HairMakeType>()?.FirstOrDefault(t =>
            t.Tribe.RowId == tribeid && t.Gender == gender);

        if (hairTable?.RowId == 0 || hairTable == null) return string.Empty;

        // 表里所有发型id
        var hairstyleids = ReadHairstyleids(hairTable.Value);
        if (hairstyleids.Count == 0) return string.Empty;

        // 匹配发型
        var customizeSheet = DService.Data.GetExcelSheet<CharaMakeCustomize>();
        if (customizeSheet == null) return string.Empty;

        foreach (var id in hairstyleids)
        {
            var customize = customizeSheet.GetRowOrDefault(id);
            if (customize == null) continue;

            if (customize.Value.HintItem.RowId == hairstyleitemid)
            {
                var iconid = customize.Value.Icon;
                if (iconid == 0) return string.Empty;
                return BuildIconPath(iconid);
            }
        }

        return string.Empty;
    }

    // 当前角色的种族和性别
    private (byte tribeid, byte gender) GetPlayerCharacterInfo()
    {
        var playerAddr = DService.ClientState.LocalPlayer?.Address;
        if (playerAddr == null) return (0, 0);

        var character = (Character*)playerAddr;

        var tribeid = character->DrawData.CustomizeData.Tribe;
        var gender = character->DrawData.CustomizeData.Sex;

        // 幻化状态
        if (character->DrawObject != null &&
            character->DrawObject->Object.GetObjectType() == ObjectType.CharacterBase)
        {
            var charBase = (CharacterBase*)character->DrawObject;
            if (charBase->GetModelType() == CharacterBase.ModelType.Human)
            {
                var human = (Human*)charBase;
                tribeid = human->Customize.Tribe;
                gender = human->Customize.Sex;
            }
        }

        return (tribeid, gender);
    }

    // 从HairMakeType里读发型id列表
    private List<uint> ReadHairstyleids(HairMakeType hairTable)
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
                    // 从内存里一个一个读id，直到遇到0
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
    private string BuildIconPath(uint iconid, bool useHighRes = true)
    {
        var suffix = useHighRes ? "_hr1.tex" : ".tex";
        return $"ui/icon/{iconid / 1000 * 1000:000000}/{iconid:000000}{suffix}";
    }

    // 创建图片节点
    private AtkImageNode* CreateImageNode(AtkUnitBase* window, AtkResNode* insertNode)
    {
        // 图片节点
        var imgNode = IMemorySpace.GetUISpace()->Create<AtkImageNode>();
        if (imgNode == null) return null;

        // 节点基本属性
        imgNode->AtkResNode.Type = NodeType.Image;
        imgNode->AtkResNode.NodeId = CustomNodeid;
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
        var imgNode = (AtkImageNode*)FindNodeByid(&window->UldManager, CustomNodeid, NodeType.Image);
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

    // 在UI里找指定id的节点
    private AtkResNode* FindNodeByid(AtkUldManager* uldManager, uint nodeid, NodeType? type = null)
    {
        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var node = uldManager->NodeList[i];
            if (node == null) continue;

            if (node->NodeId == nodeid && (type == null || node->Type == type.Value))
                return node;
        }
        return null;
    }
}
