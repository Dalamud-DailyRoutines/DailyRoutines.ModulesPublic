using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Interface;
using FFXIVClientStructs.Attributes;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;



public unsafe class CastBarAddon : DailyModuleBase
{
    protected enum Alignment : byte
    {
        TopLeft = 0x0,
        Top = 0x1,
        TopRight = 0x2,
        Left = 0x3,
        Center = 0x4,
        Right = 0x5,
        BottomLeft = 0x6,
        Bottom = 0x7,
        BottomRight = 0x8
    }//自定义的方向 比基类多四个斜角
    protected enum HorizontalAlignment : byte
    {
        Left, Center, Right
    }//木有用上
    protected enum VerticalAlignment : byte
    {
        Top, Middle, Bottom
    }//水平对齐方向

    protected delegate void CastBarOnUpdateDelegate(AddonCastBar* castBar, void* a2);//咏唱栏更新函数委托

    protected Hook<CastBarOnUpdateDelegate> castBarOnUpdateHook;//咏唱栏更新函数钩子

    protected static readonly CompSig CastBarOnUpdateSig = new("48 83 EC 38 48 8B 92");//咏唱栏钩子标识

    protected static class ImGuiExt
    {

        public static void NextRow()
        {
            while (ImGui.GetColumnIndex() != 0) ImGui.NextColumn();
        }

        public static void SetColumnWidths(int start, float firstWidth, params float[] widths)
        {
            ImGui.SetColumnWidth(start, firstWidth);
            for (var i = 0; i < widths.Length; i++)
            {
                ImGui.SetColumnWidth(start + i + 1, widths[i]);
            }
        }

        public static void SetColumnWidths(float firstWidth, params float[] widths) => SetColumnWidths(0, firstWidth, widths);

        public static bool InputByte(string label, ref byte v)
        {
            var vInt = (int)v;
            if (!ImGui.InputInt(label, ref vInt, 1)) return false;
            if (vInt < byte.MinValue || vInt > byte.MaxValue) return false;
            v = (byte)vInt;
            return true;
        }

        public static bool HorizontalAlignmentSelector(string name, ref Alignment selectedAlignment, VerticalAlignment verticalAlignment = VerticalAlignment.Middle)
        {
            var changed = false;

            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.One);

            ImGui.PushID(name);
            ImGui.BeginGroup();
            ImGui.PushFont(UiBuilder.IconFont);

            var alignments = verticalAlignment switch
            {
                VerticalAlignment.Top => new[] { Alignment.TopLeft, Alignment.Top, Alignment.TopRight },
                VerticalAlignment.Bottom => new[] { Alignment.BottomLeft, Alignment.Bottom, Alignment.BottomRight },
                _ => new[] { Alignment.Left, Alignment.Center, Alignment.Right },
            };


            ImGui.PushStyleColor(ImGuiCol.Border, selectedAlignment == alignments[0] ? 0xFF00A5FF : 0x0);
            if (ImGui.Button($"{(char)FontAwesomeIcon.AlignLeft}##{name}"))
            {
                selectedAlignment = alignments[0];
                changed = true;
            }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Border, selectedAlignment == alignments[1] ? 0xFF00A5FF : 0x0);
            if (ImGui.Button($"{(char)FontAwesomeIcon.AlignCenter}##{name}"))
            {
                selectedAlignment = alignments[1];
                changed = true;
            }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Border, selectedAlignment == alignments[2] ? 0xFF00A5FF : 0x0);
            if (ImGui.Button($"{(char)FontAwesomeIcon.AlignRight}##{name}"))
            {
                selectedAlignment = alignments[2];
                changed = true;
            }
            ImGui.PopStyleColor();

            ImGui.PopFont();
            ImGui.PopStyleVar();
            ImGui.SameLine();
            ImGui.Text(name);
            ImGui.EndGroup();

            ImGui.PopStyleVar();
            ImGui.PopID();
            return changed;
        }
    }//stp扩展的一个可以左右拉动的类
    protected static class CustomNodes
    {
        private static readonly Dictionary<string, uint> NodeIds = new();
        private static readonly Dictionary<uint, string> NodeNames = new();
        private static uint _nextId = 0x53541000;


        public static uint Get(string name, int index = 0)
        {
            if (TryGet(name, index, out var id)) return id;
            lock (NodeIds)
            {
                lock (NodeNames)
                {
                    id = _nextId;
                    _nextId += 16;
                    NodeIds.Add($"{name}#{index}", id);
                    NodeNames.Add(id, $"{name}#{index}");
                    return id;
                }
            }
        }

        public static bool TryGet(string name, out uint id) => TryGet(name, 0, out id);
        public static bool TryGet(string name, int index, out uint id) => NodeIds.TryGetValue($"{name}#{index}", out id);
        public static bool TryGet(uint id, out string name) => NodeNames.TryGetValue(id, out name);

        public const int
            TargetHP = SimpleTweaksNodeBase + 1,
            SlideCastMarker = SimpleTweaksNodeBase + 2,
            TimeUntilGpMax = SimpleTweaksNodeBase + 3,
            ComboTimer = SimpleTweaksNodeBase + 4,
            PartyListStatusTimer = SimpleTweaksNodeBase + 5,
            InventoryGil = SimpleTweaksNodeBase + 6,
            GearPositionsBg = SimpleTweaksNodeBase + 7, // and 8
            ClassicSlideCast = SimpleTweaksNodeBase + 9,
            PaintingPreview = SimpleTweaksNodeBase + 10,
            AdditionalInfo = SimpleTweaksNodeBase + 11,
            CraftingGhostBar = SimpleTweaksNodeBase + 12,
            CraftingGhostText = SimpleTweaksNodeBase + 13,
            SimpleTweaksNodeBase = 0x53540000;
    }//自定义一个node 用来给咏唱栏上加提示框框
    protected static class Configs//各项配置函数 去除了configsave部分 因为dr好像自带
    {
        public static bool RemoveCastingText;
        public static bool RemoveIcon;
        public static bool RemoveCounter;
        public static bool RemoveName;
        public static bool RemoveInterruptedText;

        public static bool SlideCast;
        public static int SlideCastAdjust = 500;
        public static Vector4 SlideCastColor = new(0.8F, 0.3F, 0.3F, 1);
        public static Vector4 SlideCastReadyColor = new(0.3F, 0.8F, 0.3F, 1);
        public static bool ClassicSlideCast;
        public static int ClassicSlideCastWidth = 3;
        public static int ClassicSlideCastOverHeight;

        public static Alignment AlignName = Alignment.Left;
        public static Alignment AlignCounter = Alignment.Right;

        public static Vector2 OffsetName = new(0);
        public static Vector2 OffsetCounter = new(0);
    }

    protected float configAlignmentX;//用来确认拉动的条的位置
    protected static void SetSize(AtkResNode* node, int? width, int? height)
    {
        if (width != null && width >= ushort.MinValue && width <= ushort.MaxValue) node->Width = (ushort)width.Value;
        if (height != null && height >= ushort.MinValue && height <= ushort.MaxValue) node->Height = (ushort)height.Value;
        node->DrawFlags |= 0x1;
    }//通用的atk设置大小的函数

    protected static void SetPosition(AtkResNode* node, float? x, float? y)
    {
        if (x != null) node->X = x.Value;
        if (y != null) node->Y = y.Value;
        node->DrawFlags |= 0x1;
    }//通用的atk设置位置的函数

    protected static void SetSize<T>(T* node, int? w, int? h) where T : unmanaged => SetSize((AtkResNode*)node, w, h);//偷懒使用的泛型版本

    protected static void SetPosition<T>(T* node, float? x, float? y) where T : unmanaged => SetPosition((AtkResNode*)node, x, y);//偷懒使用的泛型版本*2
    protected static T* GetUnitBase<T>(string name = null) where T : unmanaged
    {
        if (string.IsNullOrEmpty(name))
        {
            var attr = (AddonAttribute)typeof(T).GetCustomAttribute(typeof(AddonAttribute));
            if (attr != null)
            {
                name = attr.AddonIdentifiers.FirstOrDefault();
            }
        }

        if (string.IsNullOrEmpty(name)) return null;

        return (T*)GetAddonByName(name);
    }//感觉没什么作用 但是因为dr的GetAddonByName没有index参数 所以保留了 用来得到原本未修改的咏唱栏

    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("咏唱栏调整"),
        Description = GetLoc("为咏唱栏添加可滑步位置的图形提示"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Middo"]
    };

    protected override void Init()
    {
        castBarOnUpdateHook ??= CastBarOnUpdateSig.GetHook<CastBarOnUpdateDelegate>(CastBarOnUpdateDetour);//上钩子 感觉需要一个trycatch 但是看别的都没加 就先不加
        castBarOnUpdateHook.Enable();
    }

    protected override void Uninit()
    {
        castBarOnUpdateHook.Disable();
        UpdateCastBar(null, true);//此处用于在卸载时还原咏唱栏
        base.Uninit();
    }

    protected override void ConfigUI()
    {
        bool hasChanged = false;//用来判断配置是否有更改 有更改会在更改后统一进行一次更新
        hasChanged |= ImGui.Checkbox("隐藏“发动中...”文本", ref Configs.RemoveCastingText);
        hasChanged |= ImGui.Checkbox("隐藏技能图标", ref Configs.RemoveIcon);
        hasChanged |= ImGui.Checkbox("隐藏“打断成功”文本", ref Configs.RemoveInterruptedText);
        hasChanged |= ImGui.Checkbox("隐藏倒计时", ref Configs.RemoveCounter);
        if (Configs.RemoveCastingText && !Configs.RemoveCounter)//如果隐藏了发动中 但是没隐藏倒计时 那么可以调整倒计时位置
        {
            ImGui.SameLine();
            if (ImGui.GetCursorPosX() > configAlignmentX) configAlignmentX = ImGui.GetCursorPosX();
            ImGui.SetCursorPosX(configAlignmentX);
            hasChanged |= ImGuiExt.HorizontalAlignmentSelector("倒计时位置", ref Configs.AlignCounter);

            ImGui.SetCursorPosX(configAlignmentX);
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            hasChanged |= ImGui.SliderFloat2("水平/垂直偏移量" + "##offsetCounterPosition", ref Configs.OffsetCounter, -100, 100, $"%.0f");
            Configs.OffsetCounter = Vector2.Clamp(Configs.OffsetCounter, new Vector2(-100), new Vector2(100));
        }

        hasChanged |= ImGui.Checkbox("隐藏技能名", ref Configs.RemoveName);
        if (!Configs.RemoveName)//如果没隐藏技能名 那么可以调整技能名位置
        {
            ImGui.SameLine();
            if (ImGui.GetCursorPosX() > configAlignmentX) configAlignmentX = ImGui.GetCursorPosX();
            ImGui.SetCursorPosX(configAlignmentX);
            hasChanged |= ImGuiExt.HorizontalAlignmentSelector("技能名位置", ref Configs.AlignName);
            ImGui.SetCursorPosX(configAlignmentX);
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);

            hasChanged |= ImGui.SliderFloat2("水平/垂直偏移量" + "##offsetNamePosition", ref Configs.OffsetName, -100, 100, "%.0f");
            Configs.OffsetName = Vector2.Clamp(Configs.OffsetName, new Vector2(-100), new Vector2(100));
        }

        hasChanged |= ImGui.Checkbox("显示滑步标记", ref Configs.SlideCast);
        if (Configs.SlideCast)
        {
            ImGui.Indent();
            ImGui.Indent();
            hasChanged |= ImGui.Checkbox("传统模式", ref Configs.ClassicSlideCast);
            if (Configs.ClassicSlideCast)
            {
                ImGui.Indent();
                ImGui.Indent();
                ImGui.SetNextItemWidth(100 * ImGui.GetIO().FontGlobalScale);
                hasChanged |= ImGui.SliderInt("宽度", ref Configs.ClassicSlideCastWidth, 1, 10);
                ImGui.SetNextItemWidth(100 * ImGui.GetIO().FontGlobalScale);
                hasChanged |= ImGui.SliderInt("额外高度", ref Configs.ClassicSlideCastOverHeight, 0, 20);

                ImGui.Unindent();
                ImGui.Unindent();
            }

            hasChanged |= ImGui.SliderInt("滑步时间阈值(ms)", ref Configs.SlideCastAdjust, 0, 1000);
            hasChanged |= ImGui.ColorEdit4("滑步标记颜色", ref Configs.SlideCastColor);
            hasChanged |= ImGui.ColorEdit4("可滑步标记颜色", ref Configs.SlideCastReadyColor);
            ImGui.Unindent();
            ImGui.Unindent();
        }

        ImGui.Dummy(new Vector2(5) * ImGui.GetIO().FontGlobalScale);

        if (hasChanged)
        {
            UpdateCastBar(null, true);//统一更新咏唱栏
        }
    }

    protected void CastBarOnUpdateDetour(AddonCastBar* castBar, void* a2)//钩子函数 用来给原本的咏唱栏更新函数添砖加瓦
    {
        castBarOnUpdateHook.Original(castBar, a2);

        try
        {
            UpdateCastBar(castBar);
        }
        catch (Exception ex)
        {
            DService.Log.Error(ex.Message);//万一出错了就记录一下日志
        }
    }

    protected void UpdateCastBar(AddonCastBar* castBar, bool reset = false)//咏唱栏更新函数主体 本模块主体
    {

        if (castBar == null)
        {
            castBar = GetUnitBase<AddonCastBar>();//如果传入的为空 那么就获取原本的咏唱栏 这一步是为了在卸载时还原咏唱栏
            if (castBar == null) return;//如果还是空 那就算了 可能有别的插件彻底干掉了咏唱栏
        }

        if (castBar->AtkUnitBase.UldManager.NodeList == null || castBar->AtkUnitBase.UldManager.NodeListCount < 12) return;//如果节点列表为空或者节点数小于12 可能有别的插件改动过咏唱栏 就不对其进行修改 不然可能会出问题

        var barNode = castBar->AtkUnitBase.UldManager.NodeList[3];

        var icon = (AtkComponentNode*)castBar->AtkUnitBase.GetNodeById(8);
        var countdownText = castBar->AtkUnitBase.GetTextNodeById(7);
        var castingText = castBar->AtkUnitBase.GetTextNodeById(6);
        var skillNameText = castBar->AtkUnitBase.GetTextNodeById(4);
        var progressBar = (AtkNineGridNode*)castBar->AtkUnitBase.GetNodeById(11);
        var interruptedText = castBar->AtkUnitBase.GetTextNodeById(2);
        var slideMarker = (AtkNineGridNode*)null;
        var classicSlideMarker = (AtkImageNode*)null;

        for (var i = 0; i < castBar->AtkUnitBase.UldManager.NodeListCount; i++)
        {
            if (castBar->AtkUnitBase.UldManager.NodeList[i]->NodeId == CustomNodes.SlideCastMarker)
            {
                slideMarker = (AtkNineGridNode*)castBar->AtkUnitBase.UldManager.NodeList[i];//找到自定义的滑步标记节点
            }

            if (castBar->AtkUnitBase.UldManager.NodeList[i]->NodeId == CustomNodes.ClassicSlideCast)
            {
                classicSlideMarker = (AtkImageNode*)castBar->AtkUnitBase.UldManager.NodeList[i];//找到自定义的传统模式的滑步标记节点
            }
        }

        if (reset)//如果是卸载或者更新配置时调用的 那么就还原咏唱栏
        {
            icon->AtkResNode.ToggleVisibility(true);
            countdownText->AtkResNode.ToggleVisibility(true);
            castingText->AtkResNode.ToggleVisibility(true);
            skillNameText->AtkResNode.ToggleVisibility(true);

            SetSize(skillNameText, 170, null);
            SetPosition(skillNameText, barNode->X + 4, 0);

            SetSize(countdownText, 42, null);
            SetPosition(countdownText, 170, 30);
            interruptedText->AtkResNode.SetScale(1, 1);

            if (slideMarker != null)
            {
                slideMarker->AtkResNode.ToggleVisibility(false);
            }

            if (classicSlideMarker != null)
            {
                classicSlideMarker->AtkResNode.ToggleVisibility(false);
                if (classicSlideMarker->AtkResNode.PrevSiblingNode != null)
                    classicSlideMarker->AtkResNode.PrevSiblingNode->NextSiblingNode = classicSlideMarker->AtkResNode.NextSiblingNode;
                if (classicSlideMarker->AtkResNode.NextSiblingNode != null)
                    classicSlideMarker->AtkResNode.NextSiblingNode->PrevSiblingNode = classicSlideMarker->AtkResNode.PrevSiblingNode;
                castBar->AtkUnitBase.UldManager.UpdateDrawNodeList();

                IMemorySpace.Free(classicSlideMarker->PartsList->Parts->UldAsset, (ulong)sizeof(AtkUldPart));
                IMemorySpace.Free(classicSlideMarker->PartsList->Parts, (ulong)sizeof(AtkUldPart));
                IMemorySpace.Free(classicSlideMarker->PartsList, (ulong)sizeof(AtkUldPartsList));
                classicSlideMarker->AtkResNode.Destroy(true);
            }

            countdownText->AlignmentFontType = 0x25;
            skillNameText->AlignmentFontType = 0x03;

            return;
        }

        if (Configs.RemoveIcon) icon->AtkResNode.ToggleVisibility(false);
        if (Configs.RemoveName) skillNameText->AtkResNode.ToggleVisibility(false);
        if (Configs.RemoveCounter) countdownText->AtkResNode.ToggleVisibility(false);
        if (Configs.RemoveCastingText) castingText->AtkResNode.ToggleVisibility(false);

        if (Configs.RemoveCastingText && !Configs.RemoveCounter)
        {
            countdownText->AlignmentFontType = (byte)(0x20 | (byte)Configs.AlignCounter);
            SetSize(countdownText, barNode->Width - 8, null);
            SetPosition(countdownText, barNode->X + 4 + Configs.OffsetCounter.X, 30 + Configs.OffsetCounter.Y);
        }
        else
        {
            countdownText->AlignmentFontType = 0x20 | (byte)Alignment.Right;
            SetSize(countdownText, 42, null);
            SetPosition(countdownText, 170, null);
        }

        if (!Configs.RemoveName)
        {
            skillNameText->AlignmentFontType = (byte)(0x00 | (byte)Configs.AlignName);
            SetPosition(skillNameText, (barNode->X + 4) + Configs.OffsetName.X, Configs.OffsetName.Y);
            SetSize(skillNameText, barNode->Width - 8, null);
        }

        if (Configs.RemoveInterruptedText)
        {
            interruptedText->AtkResNode.SetScale(0, 0);
        }

        if (Configs.SlideCast && Configs.ClassicSlideCast == false)
        {
            if (classicSlideMarker != null) classicSlideMarker->AtkResNode.ToggleVisibility(false);
            if (slideMarker == null)
            {
                //此处创建滑步附加条

                slideMarker = CloneNode(progressBar);
                slideMarker->AtkResNode.NodeId = CustomNodes.SlideCastMarker;
                castBar->AtkUnitBase.GetNodeById(10)->PrevSiblingNode = (AtkResNode*)slideMarker;
                slideMarker->AtkResNode.NextSiblingNode = castBar->AtkUnitBase.GetNodeById(10);
                slideMarker->AtkResNode.ParentNode = castBar->AtkUnitBase.GetNodeById(9);
                castBar->AtkUnitBase.UldManager.UpdateDrawNodeList();
            }

            if (slideMarker != null)
            {
                var slidePer = ((float)(castBar->CastTime * 10) - Configs.SlideCastAdjust) / (castBar->CastTime * 10);
                var pos = 160 * slidePer;
                slideMarker->AtkResNode.ToggleVisibility(true);
                SetSize(slideMarker, 168 - (int)pos, 20);
                SetPosition(slideMarker, pos - 8, 0);
                var c = (slidePer * 100) >= castBar->CastPercent ? Configs.SlideCastColor : Configs.SlideCastReadyColor;
                slideMarker->AtkResNode.AddRed = (byte)(255 * c.X);
                slideMarker->AtkResNode.AddGreen = (byte)(255 * c.Y);
                slideMarker->AtkResNode.AddBlue = (byte)(255 * c.Z);
                slideMarker->AtkResNode.MultiplyRed = (byte)(255 * c.X);
                slideMarker->AtkResNode.MultiplyGreen = (byte)(255 * c.Y);
                slideMarker->AtkResNode.MultiplyBlue = (byte)(255 * c.Z);
                slideMarker->AtkResNode.Color.A = (byte)(255 * c.W);
                slideMarker->PartId = 0;
                slideMarker->AtkResNode.DrawFlags |= 1;
            }
        }
        else if (Configs.SlideCast && Configs.ClassicSlideCast)
        {
            if (slideMarker != null) slideMarker->AtkResNode.ToggleVisibility(false);
            if (classicSlideMarker == null)
            {
                if (progressBar == null) return;

                // Create Node
                classicSlideMarker = IMemorySpace.GetUISpace()->Create<AtkImageNode>();
                classicSlideMarker->AtkResNode.Type = NodeType.Image;
                classicSlideMarker->AtkResNode.NodeId = CustomNodes.ClassicSlideCast;
                classicSlideMarker->AtkResNode.NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft;
                classicSlideMarker->AtkResNode.DrawFlags = 0;
                classicSlideMarker->WrapMode = 1;
                classicSlideMarker->Flags = 0;

                var partsList = (AtkUldPartsList*)IMemorySpace.GetUISpace()->Malloc((ulong)sizeof(AtkUldPartsList), 8);
                if (partsList == null)
                {
                    DService.Log.Error("为列表分配内存失败。");
                    classicSlideMarker->AtkResNode.Destroy(true);
                    return;
                }

                partsList->Id = 0;
                partsList->PartCount = 1;

                var part = (AtkUldPart*)IMemorySpace.GetUISpace()->Malloc((ulong)sizeof(AtkUldPart), 8);
                if (part == null)
                {
                    DService.Log.Error("为部件分配内存失败。");
                    IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
                    classicSlideMarker->AtkResNode.Destroy(true);
                    return;
                }

                part->U = 30;
                part->V = 30;
                part->Width = 1;
                part->Height = 12;

                partsList->Parts = part;

                var asset = (AtkUldAsset*)IMemorySpace.GetUISpace()->Malloc((ulong)sizeof(AtkUldAsset), 8);
                if (asset == null)
                {
                    DService.Log.Error("为资源分配内存失败。");
                    IMemorySpace.Free(part, (ulong)sizeof(AtkUldPart));
                    IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
                    classicSlideMarker->AtkResNode.Destroy(true);
                    return;
                }

                asset->Id = 0;
                asset->AtkTexture.Ctor();
                part->UldAsset = asset;
                classicSlideMarker->PartsList = partsList;

                classicSlideMarker->LoadTexture("ui/uld/emjfacemask.tex");//这个贴图是个纯色贴图 狒狒自带的

                classicSlideMarker->AtkResNode.ToggleVisibility(true);

                classicSlideMarker->AtkResNode.SetWidth(1);
                classicSlideMarker->AtkResNode.SetHeight(12);
                classicSlideMarker->AtkResNode.SetPositionShort(100, 4);

                classicSlideMarker->AtkResNode.ParentNode = progressBar->AtkResNode.ParentNode;

                var prev = progressBar->AtkResNode.PrevSiblingNode;

                progressBar->AtkResNode.PrevSiblingNode = (AtkResNode*)classicSlideMarker;
                prev->NextSiblingNode = (AtkResNode*)classicSlideMarker;

                classicSlideMarker->AtkResNode.PrevSiblingNode = prev;
                classicSlideMarker->AtkResNode.NextSiblingNode = (AtkResNode*)progressBar;

                castBar->AtkUnitBase.UldManager.UpdateDrawNodeList();
            }

            if (classicSlideMarker != null)
            {
                classicSlideMarker->AtkResNode.ToggleVisibility(true);

                var slidePer = ((float)(castBar->CastTime * 10) - Configs.SlideCastAdjust) / (castBar->CastTime * 10);
                var pos = 160 * slidePer;

                classicSlideMarker->AtkResNode.SetWidth((ushort)Configs.ClassicSlideCastWidth);
                classicSlideMarker->AtkResNode.SetHeight((ushort)(12 + Configs.ClassicSlideCastOverHeight * 2));
                classicSlideMarker->AtkResNode.SetPositionFloat(pos, 4 - Configs.ClassicSlideCastOverHeight);

                var c = (slidePer * 100) >= castBar->CastPercent ? Configs.SlideCastColor : Configs.SlideCastReadyColor;
                classicSlideMarker->AtkResNode.Color.R = (byte)(255 * c.X);
                classicSlideMarker->AtkResNode.Color.G = (byte)(255 * c.Y);
                classicSlideMarker->AtkResNode.Color.B = (byte)(255 * c.Z);

                classicSlideMarker->AtkResNode.Color.A = (byte)(255 * c.W);
            }
        }
    }


}
