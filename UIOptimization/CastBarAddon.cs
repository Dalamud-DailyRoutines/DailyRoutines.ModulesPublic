using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using System.Collections.Generic;
using System.Numerics;

namespace DailyRoutines.CastBarAddon;


public unsafe class CastBarAddon : DailyModuleBase
{
    protected unsafe static AddonCastBar* AddonCastBar => (AddonCastBar*)HelpersOm.GetAddonByName("_CastBar");//获得咏唱栏指针
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

    protected float configAlignmentX;//用来确认拉动的条的位置
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
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("咏唱栏调整"),
        Description = GetLoc("为咏唱栏添加可滑步位置的图形提示"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Middo"]
    };

    private static SimpleNineGridNode? slideMarker;//滑步标记

    private static SimpleNineGridNode? classicSlideMarker;//传统滑步标记
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
    protected override void ConfigUI()
    {
        bool hasChanged = false;//用来判断配置是否有更改 有更改会在更改后统一进行一次更新
        hasChanged |= ImGui.Checkbox("隐藏“发动中...”文本", ref Configs.RemoveCastingText);
        hasChanged |= ImGui.Checkbox("隐藏技能图标", ref Configs.RemoveIcon);
        hasChanged |= ImGui.Checkbox("隐藏“中断”文本", ref Configs.RemoveInterruptedText);
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
            OnAddon(AddonEvent.PreFinalize, null);//统一更新咏唱栏
        }
    }
    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_CastBar", OnAddon);//注册咏唱栏更新事件监听
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_CastBar", OnAddon);//注册咏唱栏注销事件监听
        }
    protected override void Uninit()
        {
        DService.AddonLifecycle.UnregisterListener(OnAddon);//注销监听器
        OnAddon(AddonEvent.PreFinalize, null);//最后对咏唱栏进行一次复原
        base.Uninit();
        }
    protected static void OnAddon(AddonEvent type, AddonArgs args)
    {
        var barNode = AddonCastBar->UldManager.NodeList[3];
        var icon = (AtkComponentNode*)AddonCastBar->GetNodeById(8);
        var countdownText = AddonCastBar->GetTextNodeById(7);
        var castingText = AddonCastBar->GetTextNodeById(6);
        var skillNameText = AddonCastBar->GetTextNodeById(4);
        var progressBar = (AtkNineGridNode*)AddonCastBar->GetNodeById(11);
        var interruptedText = AddonCastBar->GetTextNodeById(2);
        switch (type)
        {
            case AddonEvent.PreFinalize://注销时复原 断开并删除自定义节点
                if (slideMarker != null)
            {
                    slideMarker.IsVisible = false;
                    Service.AddonController.DetachNode(slideMarker);
                    slideMarker = null;
            }
                if (classicSlideMarker != null)
            {
                    classicSlideMarker.IsVisible = false;
                    Service.AddonController.DetachNode(classicSlideMarker);
                    classicSlideMarker = null;
        }
            icon->AtkResNode.ToggleVisibility(true);
            countdownText->AtkResNode.ToggleVisibility(true);
            castingText->AtkResNode.ToggleVisibility(true);
            skillNameText->AtkResNode.ToggleVisibility(true);

            SetSize(skillNameText, 170, null);
            SetPosition(skillNameText, barNode->X + 4, 0);

            SetSize(countdownText, 42, null);
            SetPosition(countdownText, 170, 30);
            interruptedText->AtkResNode.SetScale(1, 1);

            countdownText->AlignmentFontType = 0x25;
            skillNameText->AlignmentFontType = 0x03;

            return;
            case AddonEvent.PostDraw://每帧更新时进行修改
                if (AddonCastBar->UldManager.NodeList == null || AddonCastBar->UldManager.NodeListCount < 12) return;//如果节点列表为空或者节点数小于12 可能有别的插件改动过咏唱栏 就不对其进行修改 不然可能会出问题


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
                    if (classicSlideMarker != null) classicSlideMarker.IsVisible = false;
            if (slideMarker == null)
            {
                        slideMarker = new SimpleNineGridNode
                        {
                            PartId = 0,
                            TexturePath = "ui/uld/bgparts_hr1.tex",//背景板的贴图 咏唱栏的贴图没找着 找的别的
                            TextureCoordinates = new(32, 37),
                            TextureSize = new(28, 30),
                            IsVisible = false,
                            Color = progressBar->Color.RGBA.ToVector4(),
                            NodeId = CustomNodes.SlideCastMarker,
                            NodeFlags = progressBar->NodeFlags,
                        };

                        Service.AddonController.AttachNode(slideMarker, AddonCastBar->GetNodeById(10));
            }

            if (slideMarker != null)
            {
                        var slidePer = ((float)(AddonCastBar->CastTime * 10) - Configs.SlideCastAdjust) / (AddonCastBar->CastTime * 10);
                var pos = 160 * slidePer;
                        slideMarker.IsVisible = true;
                        slideMarker.Size = new Vector2(168 - (int)pos, 15);
                        slideMarker.Position = new Vector2(pos - 11, 3);
                        var c = (slidePer * 100) >= AddonCastBar->CastPercent ? Configs.SlideCastColor : Configs.SlideCastReadyColor;
                        slideMarker.AddColor = new Vector3(c.X, c.Y, c.Z);
                        slideMarker.MultiplyColor = new Vector3(c.X, c.Y, c.Z);
                        slideMarker.Alpha = c.W;
                        slideMarker.PartId = 0;
            }

        }
        else if (Configs.SlideCast && Configs.ClassicSlideCast)
        {
                    if (slideMarker != null) slideMarker.IsVisible = false;
            if (classicSlideMarker == null)
            {
                if (progressBar == null) return;

                        // 创建传统模式节点
                        classicSlideMarker = new SimpleNineGridNode()
                {
                            TexturePath = "ui/uld/emjfacemask.tex",//这个贴图是个纯色贴图 狒狒自带的
                            TextureCoordinates = new(28, 28),
                            TextureSize = new(8, 8),
                            NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft,
                            IsVisible = true,
                            Width = 1,
                            Height = 12,
                            Position = new Vector2(100, 4),
                        };

                        Service.AddonController.AttachNode(classicSlideMarker, progressBar->ParentNode);
                }

                    if (classicSlideMarker != null)
                {
                        classicSlideMarker.IsVisible = true;

                        var slidePer = ((float)(AddonCastBar->CastTime * 10) - Configs.SlideCastAdjust) / (AddonCastBar->CastTime * 10);
                        var pos = 160 * slidePer;

                        classicSlideMarker.Width = (ushort)Configs.ClassicSlideCastWidth;
                        classicSlideMarker.Height = (ushort)(12 + Configs.ClassicSlideCastOverHeight * 2);
                        classicSlideMarker.Position = new Vector2(pos, 4 - Configs.ClassicSlideCastOverHeight);

                        var c = (slidePer * 100) >= AddonCastBar->CastPercent ? Configs.SlideCastColor : Configs.SlideCastReadyColor;
                        classicSlideMarker.Color = new Vector4(c.X, c.Y, c.Z, c.W);
                    }
                }






                return;
    }
        return;
    }//使用了监听器方法进行重构
}
