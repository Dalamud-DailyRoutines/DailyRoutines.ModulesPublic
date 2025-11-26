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

/// <summary>
/// 预览可解锁物品模块
/// 功能: 在物品详情界面为坐骑、宠物和发型显示预览图像
/// </summary>
public unsafe class PreviewUnlockableItems : DailyModuleBase
{
    #region 模块信息

    /// <summary>
    /// 模块基本信息
    /// </summary>
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "快捷预览",
        Description = "在物品详情界面预览坐骑、宠物和发型的外观图像",
        Category    = ModuleCategories.Assist,
        Author      = ["AZZ"],
    };

    /// <summary>
    /// 模块权限设置
    /// </summary>
    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    #endregion

    #region 字段定义

    /// <summary>
    /// 上次加载的图像路径，用于避免重复加载同一图像
    /// </summary>
    private string lastImage = string.Empty;

    /// <summary>
    /// 自定义图像节点ID，用于标识我们创建的图像节点，避免与游戏原有节点冲突
    /// 使用一个较大的数字确保不会与游戏内置节点ID重复
    /// </summary>
    private const uint CustomNodeId = 99990001;

    /// <summary>
    /// 记录图像尺寸的结构
    /// </summary>
    /// <param name="Width">图像宽度</param>
    /// <param name="Height">图像高度</param>
    /// <param name="Scale">缩放比例</param>
    private record ImageSize(float Width = 0, float Height = 0, float Scale = 1);

    #endregion

    #region 生命周期方法

    /// <summary>
    /// 模块初始化
    /// 当模块被启用时调用此方法，注册必要的事件监听器
    /// </summary>
    protected override void Init()
    {
        // 注册 ItemDetail 界面的 PostUpdate 事件
        // 当物品详情窗口更新时，我们的回调函数会被调用
        DService.AddonLifecycle.RegisterListener(
            AddonEvent.PostUpdate, "ItemDetail", OnItemDetailUpdate);
    }

    /// <summary>
    /// 模块清理
    /// 当模块被禁用时调用此方法，取消事件注册并清理资源
    /// </summary>
    protected override void Uninit()
    {
        // 取消注册事件监听器
        DService.AddonLifecycle.UnregisterListener(OnItemDetailUpdate);

        // 清理我们创建的图像节点
        CleanupImageNode();

        // 重置最后加载的图像路径
        lastImage = string.Empty;
    }

    #endregion

    #region 事件处理

    /// <summary>
    /// 物品详情界面更新事件处理
    /// 当 ItemDetail 窗口更新时被调用，用于显示物品预览图像
    /// </summary>
    /// <param name="type">事件类型</param>
    /// <param name="args">事件参数</param>
    private void OnItemDetailUpdate(AddonEvent type, AddonArgs args)
    {
        // 获取 ItemDetail 窗口的指针
        var atkUnitBase = (AtkUnitBase*)args.Addon.Address;
        if (atkUnitBase == null) return;

        // 先隐藏现有的图像节点
        var imageNode = (AtkImageNode*)GetNodeById(&atkUnitBase->UldManager, CustomNodeId, NodeType.Image);
        if (imageNode != null)
        {
            imageNode->AtkResNode.ToggleVisibility(false);
        }

        // 获取当前查看的物品ID
        var itemId = AgentItemDetail.Instance()->ItemId;

        // 物品ID验证：ID必须大于0且小于2000000
        if (itemId is >= 2000000 or <= 0) return;

        // 处理特殊的物品ID格式（取模500000）
        itemId %= 500000;

        // 从游戏数据中获取物品信息
        var item = DService.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        var itemAction = item?.ItemAction.Value;
        if (itemAction == null) return;

        // 根据物品动作类型确定图像路径和尺寸
        var (imagePath, size) = itemAction.Value.Type switch
        {
            1322 => (GetMountImagePath(itemAction.Value.Data[0]), new ImageSize(190, 234, 0.8f)),  // 坐骑
            853  => (GetMinionImagePath(itemAction.Value.Data[0]), new ImageSize(100, 100, 0.8f)), // 宠物
            2633 => (GetHairstylePath(itemId), new ImageSize(100, 100, 0.8f)),                     // 发型
            _    => (string.Empty, new ImageSize()),                                                // 其他类型不处理
        };

        // 如果没有找到有效的图像路径，直接返回
        if (string.IsNullOrEmpty(imagePath)) return;

        // 获取用于插入图像节点的参考节点（ID为2）
        var insertNode = atkUnitBase->GetNodeById(2);
        if (insertNode == null) return;

        // 获取用于定位的锚点节点（ID为47）
        var anchorNode = atkUnitBase->GetNodeById(47);
        if (anchorNode == null) return;

        // 如果图像节点不存在，创建新节点
        if (imageNode == null)
        {
            imageNode = CreateImageNode(atkUnitBase, insertNode);
            if (imageNode == null) return;
        }

        // 显示图像节点
        imageNode->AtkResNode.ToggleVisibility(true);

        // 如果图像路径改变了，加载新的纹理
        if (imagePath != lastImage)
        {
            if (!string.IsNullOrEmpty(imagePath))
            {
                imageNode->LoadTexture(imagePath);
            }
            else
            {
                imageNode->UnloadTexture();
            }
            lastImage = imagePath;
        }

        // 计算并设置图像节点的尺寸
        // 宽度 = (窗口宽度 - 边距) * 缩放比例
        var imageWidth = (ushort)((atkUnitBase->RootNode->Width - 20f) * size.Scale);
        // 高度 = 宽度 * 原始宽高比
        var imageHeight = (ushort)(imageWidth * size.Height / size.Width);

        imageNode->AtkResNode.SetWidth(imageWidth);
        imageNode->AtkResNode.SetHeight(imageHeight);

        // 设置图像位置：水平居中，垂直位于锚点下方8像素
        var posX = atkUnitBase->RootNode->Width / 2f - imageNode->AtkResNode.Width / 2f;
        var posY = anchorNode->Y + anchorNode->GetHeight() + 8;
        imageNode->AtkResNode.SetPositionFloat(posX, posY);

        // 调整窗口高度以容纳图像
        var windowHeight = (ushort)(imageNode->AtkResNode.Y + imageNode->AtkResNode.GetHeight() + 16);
        atkUnitBase->WindowNode->AtkResNode.SetHeight(windowHeight);

        // 更新窗口背景高度
        var bgNode = atkUnitBase->WindowNode->Component->UldManager.SearchNodeById(2);
        if (bgNode != null)
        {
            bgNode->SetHeight(windowHeight);
        }

        // 调整插入节点的位置（通常是窗口底部元素）
        insertNode->SetPositionFloat(insertNode->X, windowHeight - 20);

        // 更新根节点高度
        atkUnitBase->RootNode->SetHeight(windowHeight);
    }

    #endregion

    #region 图像路径获取方法

    /// <summary>
    /// 获取坐骑预览图像的路径
    /// </summary>
    /// <param name="mountId">坐骑ID</param>
    /// <param name="hr">是否使用高分辨率版本 (_hr1.tex)</param>
    /// <returns>图像文件路径，如果坐骑不存在则返回空字符串</returns>
    private string GetMountImagePath(uint mountId, bool hr = true)
    {
        // 从 Lumina 数据表获取坐骑信息
        var mount = DService.Data.GetExcelSheet<Mount>()?.GetRowOrDefault(mountId);
        if (mount == null) return string.Empty;

        // 坐骑图标ID = 原始图标ID + 64000
        // 64000是游戏内部用于坐骑预览图的偏移量
        var iconId = mount.Value.Icon + 64000U;

        // 构建图像路径
        // 格式: ui/icon/分组文件夹/图标ID.tex
        // 分组文件夹 = (ID / 1000) * 1000，即按千位数分组
        // 例如: 图标65123 -> ui/icon/065000/065123_hr1.tex
        return $"ui/icon/{iconId / 1000 * 1000:000000}/{iconId:000000}{(hr ? "_hr1.tex" : ".tex")}";
    }

    /// <summary>
    /// 获取宠物预览图像的路径
    /// </summary>
    /// <param name="minionId">宠物ID</param>
    /// <param name="hr">是否使用高分辨率版本 (_hr1.tex)</param>
    /// <returns>图像文件路径，如果宠物不存在则返回空字符串</returns>
    private string GetMinionImagePath(uint minionId, bool hr = true)
    {
        // 从 Lumina 数据表获取宠物信息
        var minion = DService.Data.GetExcelSheet<LuminaCompanion>()?.GetRowOrDefault(minionId);
        if (minion == null) return string.Empty;

        // 宠物图标ID = 原始图标ID + 64000
        // 与坐骑相同，使用相同的偏移量获取预览图
        var iconId = minion.Value.Icon + 64000U;

        // 构建图像路径（格式与坐骑相同）
        return $"ui/icon/{iconId / 1000 * 1000:000000}/{iconId:000000}{(hr ? "_hr1.tex" : ".tex")}";
    }

    /// <summary>
    /// 获取发型预览图像的路径
    /// 发型图像与角色的种族和性别相关
    /// </summary>
    /// <param name="hairstyleItem">发型物品ID</param>
    /// <param name="hr">是否使用高分辨率版本 (_hr1.tex)</param>
    /// <returns>图像文件路径，如果发型不存在或角色信息无效则返回空字符串</returns>
    private string GetHairstylePath(uint hairstyleItem, bool hr = true)
    {
        // 获取当前角色
        var character = (Character*)DService.ClientState.LocalPlayer?.Address;
        if (character == null) return string.Empty;

        // 获取角色的种族和性别
        var tribeId = character->DrawData.CustomizeData.Tribe;
        var sex = character->DrawData.CustomizeData.Sex;

        // 如果角色有绘制对象，优先使用绘制对象的数据（更准确）
        if (character->DrawObject != null && character->DrawObject->Object.GetObjectType() == ObjectType.CharacterBase)
        {
            var cb = (CharacterBase*)character->DrawObject;
            if (cb->GetModelType() == CharacterBase.ModelType.Human)
            {
                var human = (Human*)cb;
                tribeId = human->Customize.Tribe;
                sex = human->Customize.Sex;
            }
        }

        // 获取HairMakeType表
        var hairMakeTypeSheet = DService.Data.GetExcelSheet<HairMakeType>();
        if (hairMakeTypeSheet == null) return string.Empty;

        // 查找匹配种族和性别的HairMakeType
        var hairMakeType = hairMakeTypeSheet.FirstOrDefault(t =>
            t.Tribe.RowId == tribeId && t.Gender == sex);

        if (hairMakeType.RowId == 0) return string.Empty;

        // 直接读取HairMakeType的原始数据获取发型列表
        var hairstyleIds = ReadHairstyleIds(hairMakeType);
        if (hairstyleIds.Count == 0) return string.Empty;

        // 获取CharaMakeCustomize表
        var customizeSheet = DService.Data.GetExcelSheet<CharaMakeCustomize>();
        if (customizeSheet == null) return string.Empty;

        // 查找匹配的发型
        foreach (var id in hairstyleIds)
        {
            var customize = customizeSheet.GetRowOrDefault(id);
            if (customize == null) continue;

            if (customize.Value.HintItem.RowId == hairstyleItem)
            {
                var iconId = customize.Value.Icon;
                if (iconId == 0) return string.Empty;
                return $"ui/icon/{iconId / 1000 * 1000:000000}/{iconId:000000}{(hr ? "_hr1.tex" : ".tex")}";
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 从HairMakeType读取发型ID列表
    /// </summary>
    private List<uint> ReadHairstyleIds(HairMakeType hairMakeType)
    {
        var ids = new List<uint>();

        try
        {
            var hairMakeTypeType = typeof(HairMakeType);
            var pageField = hairMakeTypeType.GetField("<page>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var offsetField = hairMakeTypeType.GetField("<offset>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (pageField != null && offsetField != null)
            {
                var page = (Lumina.Excel.ExcelPage?)pageField.GetValue(hairMakeType);
                var offset = (uint?)offsetField.GetValue(hairMakeType);

                if (page != null && offset.HasValue)
                {
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
            // 读取失败，返回空列表
        }

        return ids;
    }

    #endregion

    #region UI节点管理

    /// <summary>
    /// 创建图像节点
    /// 包括节点本身以及相关的Parts List、Part和Asset结构
    /// </summary>
    /// <param name="atkUnitBase">父窗口</param>
    /// <param name="insertNode">插入位置参考节点</param>
    /// <returns>创建的图像节点，如果创建失败返回null</returns>
    private AtkImageNode* CreateImageNode(AtkUnitBase* atkUnitBase, AtkResNode* insertNode)
    {
        // 分配图像节点内存
        var imageNode = IMemorySpace.GetUISpace()->Create<AtkImageNode>();
        if (imageNode == null) return null;

        // 设置节点基本属性
        imageNode->AtkResNode.Type = NodeType.Image;      // 节点类型为图像
        imageNode->AtkResNode.NodeId = CustomNodeId;      // 使用自定义ID
        imageNode->AtkResNode.NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft;  // 锚点设置
        imageNode->AtkResNode.DrawFlags = 0;              // 绘制标志
        imageNode->WrapMode = 1;                          // 包裹模式
        imageNode->Flags = 128;                           // 图像标志

        // 创建Parts List（纹理部件列表）
        var partsList = (AtkUldPartsList*)IMemorySpace.GetUISpace()->Malloc(
            (ulong)sizeof(AtkUldPartsList), 8);
        if (partsList == null)
        {
            imageNode->AtkResNode.Destroy(true);
            return null;
        }

        partsList->Id = 0;
        partsList->PartCount = 1;

        // 创建Part（纹理部件）
        var part = (AtkUldPart*)IMemorySpace.GetUISpace()->Malloc(
            (ulong)sizeof(AtkUldPart), 8);
        if (part == null)
        {
            IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
            imageNode->AtkResNode.Destroy(true);
            return null;
        }

        // 设置纹理坐标和大小（完整纹理，无裁剪）
        part->U = 0;
        part->V = 0;
        part->Width = 256;
        part->Height = 256;

        partsList->Parts = part;

        // 创建Asset（纹理资源）
        var asset = (AtkUldAsset*)IMemorySpace.GetUISpace()->Malloc(
            (ulong)sizeof(AtkUldAsset), 8);
        if (asset == null)
        {
            IMemorySpace.Free(part, (ulong)sizeof(AtkUldPart));
            IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
            imageNode->AtkResNode.Destroy(true);
            return null;
        }

        asset->Id = 0;
        asset->AtkTexture.Ctor();  // 初始化纹理对象
        part->UldAsset = asset;
        imageNode->PartsList = partsList;

        // 将图像节点插入到UI树中
        // 插入位置：在insertNode之前
        var prev = insertNode->PrevSiblingNode;
        imageNode->AtkResNode.ParentNode = insertNode->ParentNode;

        insertNode->PrevSiblingNode = (AtkResNode*)imageNode;
        if (prev != null) prev->NextSiblingNode = (AtkResNode*)imageNode;

        imageNode->AtkResNode.PrevSiblingNode = prev;
        imageNode->AtkResNode.NextSiblingNode = insertNode;

        // 更新绘制节点列表，使新节点生效
        atkUnitBase->UldManager.UpdateDrawNodeList();

        return imageNode;
    }

    /// <summary>
    /// 清理图像节点
    /// 从UI树中移除节点并释放所有相关内存
    /// </summary>
    private void CleanupImageNode()
    {
        // 获取ItemDetail窗口
        var addon = DService.Gui.GetAddonByName("ItemDetail");
        if (addon == nint.Zero) return;
        var unitBase = (AtkUnitBase*)addon.Address;

        // 查找我们创建的图像节点
        var imageNode = (AtkImageNode*)GetNodeById(&unitBase->UldManager, CustomNodeId, NodeType.Image);
        if (imageNode == null) return;

        // 从节点树中移除
        if (imageNode->AtkResNode.PrevSiblingNode != null)
            imageNode->AtkResNode.PrevSiblingNode->NextSiblingNode = imageNode->AtkResNode.NextSiblingNode;
        if (imageNode->AtkResNode.NextSiblingNode != null)
            imageNode->AtkResNode.NextSiblingNode->PrevSiblingNode = imageNode->AtkResNode.PrevSiblingNode;

        // 更新绘制节点列表
        unitBase->UldManager.UpdateDrawNodeList();

        // 释放内存（必须按照分配的逆序释放）
        if (imageNode->PartsList != null)
        {
            if (imageNode->PartsList->Parts != null)
            {
                // 释放Asset
                if (imageNode->PartsList->Parts->UldAsset != null)
                    IMemorySpace.Free(imageNode->PartsList->Parts->UldAsset, (ulong)sizeof(AtkUldAsset));

                // 释放Part
                IMemorySpace.Free(imageNode->PartsList->Parts, (ulong)sizeof(AtkUldPart));
            }

            // 释放PartsList
            IMemorySpace.Free(imageNode->PartsList, (ulong)sizeof(AtkUldPartsList));
        }

        // 销毁节点本身
        imageNode->AtkResNode.Destroy(true);
    }

    /// <summary>
    /// 根据ID和类型获取UI节点
    /// </summary>
    /// <param name="uldManager">UI管理器</param>
    /// <param name="nodeId">节点ID</param>
    /// <param name="type">节点类型</param>
    /// <returns>找到的节点指针，如果未找到返回null</returns>
    private AtkResNode* GetNodeById(AtkUldManager* uldManager, uint nodeId, NodeType? type = null)
    {
        // 遍历节点列表查找匹配的节点
        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var node = uldManager->NodeList[i];
            if (node == null) continue;

            // 检查ID和类型是否匹配
            if (node->NodeId == nodeId && (type == null || node->Type == type.Value))
                return node;
        }
        return null;
    }

    #endregion
}