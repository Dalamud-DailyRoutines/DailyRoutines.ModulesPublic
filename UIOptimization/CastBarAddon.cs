using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using System.Numerics;
using static FFXIVClientStructs.FFXIV.Client.UI.ListPanel;

namespace DailyRoutines.CastBarAddon;


public unsafe class CastBarAddon : DailyModuleBase
{

    protected static class CustomNodes
    {
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
    }//去除多余功能 仅保留int类型标记
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("咏唱栏调整"),
        Description = GetLoc("为咏唱栏添加可滑步位置的图形提示"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Middo"]
    };

    private static SimpleNineGridNode? slideMarker;//滑步标记

    private static SimpleNineGridNode? classicSlideMarker;//传统滑步标记
    protected class Configs : ModuleConfiguration
    {
        public bool RemoveCastingText;
        public bool RemoveIcon = false;
        public bool RemoveCounter;
        public bool RemoveName;
        public bool RemoveInterruptedText;

        public bool SlideCast;
        public int SlideCastAdjust = 500;
        public Vector4 SlideCastColor = new(0.8F, 0.3F, 0.3F, 1);
        public Vector4 SlideCastReadyColor = new(0.3F, 0.8F, 0.3F, 1);
        public bool ClassicSlideCast;
        public int ClassicSlideCastWidth = 3;
        public int ClassicSlideCastOverHeight;

        public Alignment AlignName = Alignment.Left;
        public Alignment AlignCounter = Alignment.Right;

        public Vector2 OffsetName = new(0);
        public Vector2 OffsetCounter = new(0);
    }//各项配置函数 原来dr不自带保存功能

    private Configs Config = null!;//配置设置实例

    protected override void ConfigUI()
    {
        bool hasChanged = false;//用来判断配置是否有更改 有更改会在更改后统一进行一次更新
        hasChanged |= ImGui.Checkbox(GetLoc("隐藏“发动中...”文本"), ref Config.RemoveCastingText);
        hasChanged |= ImGui.Checkbox(GetLoc("隐藏技能图标"), ref Config.RemoveIcon);
        hasChanged |= ImGui.Checkbox(GetLoc("隐藏“中断”文本"), ref Config.RemoveInterruptedText);
        hasChanged |= ImGui.Checkbox(GetLoc("隐藏倒计时"), ref Config.RemoveCounter);
        if (Config.RemoveCastingText && !Config.RemoveCounter)//如果隐藏了发动中 但是没隐藏倒计时 那么可以调整倒计时位置
        {
            ImGui.SameLine();
            using (ImRaii.PushId("CounterPosition"))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 2f))//此处边框值用于突出显示当前选中状态
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.One))
            using (ImRaii.PushFont(UiBuilder.IconFont))//使用图标字体
            using (ImRaii.Group())
            {
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignCounter == Alignment.Left ? 0xFF00A5FFu : 0u))//此处用颜色来表示当前选中状态 以下皆相同作用
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignLeft}"))
                    {
                        Config.AlignCounter = Alignment.Left;
                        hasChanged |= true;
                    }
                }

                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignCounter == Alignment.Center ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignCenter}"))
                    {
                        Config.AlignCounter = Alignment.Center;
                        hasChanged |= true;
                    }
                }
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignCounter == Alignment.Right ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignRight}"))
                    {
                        Config.AlignCounter = Alignment.Right;
                        hasChanged |= true;
                    }
                }
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("倒计时对齐位置"));
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            hasChanged |= ImGui.SliderFloat2(GetLoc("水平/垂直偏移量") + "##offsetCounterPosition", ref Config.OffsetCounter, -100, 100, $"%.0f");

            Config.OffsetCounter = Vector2.Clamp(Config.OffsetCounter, new Vector2(-100), new Vector2(100));
        }

        hasChanged |= ImGui.Checkbox(GetLoc("隐藏技能名"), ref Config.RemoveName);
        if (!Config.RemoveName)//如果没隐藏技能名 那么可以调整技能名位置
        {
            ImGui.SameLine();
            using (ImRaii.PushId("NamePosition"))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 2f))
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.One))
            using (ImRaii.PushFont(UiBuilder.IconFont))
            using (ImRaii.Group())
            {
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignName == Alignment.Left ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignLeft}"))
                    {
                        Config.AlignName = Alignment.Left;
                        hasChanged |= true;
                    }
                }

                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignName == Alignment.Center ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignCenter}"))
                    {
                        Config.AlignName = Alignment.Center;
                        hasChanged |= true;
                    }
                }
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignName == Alignment.Right ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignRight}"))
                    {
                        Config.AlignName = Alignment.Right;
                        hasChanged |= true;
                    }
                }
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("技能名对齐位置"));
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);

            hasChanged |= ImGui.SliderFloat2(GetLoc("水平/垂直偏移量") + "##offsetNamePosition", ref Config.OffsetName, -100, 100, "%.0f");
            Config.OffsetName = Vector2.Clamp(Config.OffsetName, new Vector2(-100), new Vector2(100));
        }

        hasChanged |= ImGui.Checkbox(GetLoc("显示滑步标记"), ref Config.SlideCast);
        if (Config.SlideCast)
        {
            ImGui.Indent();
            ImGui.Indent();
            hasChanged |= ImGui.Checkbox(GetLoc("传统模式"), ref Config.ClassicSlideCast);
            if (Config.ClassicSlideCast)
            {
                ImGui.Indent();
                ImGui.Indent();
                ImGui.SetNextItemWidth(100 * ImGui.GetIO().FontGlobalScale);
                hasChanged |= ImGui.SliderInt(GetLoc("宽度"), ref Config.ClassicSlideCastWidth, 1, 10);
                ImGui.SetNextItemWidth(100 * ImGui.GetIO().FontGlobalScale);
                hasChanged |= ImGui.SliderInt(GetLoc("额外高度"), ref Config.ClassicSlideCastOverHeight, 0, 20);

                ImGui.Unindent();
                ImGui.Unindent();
            }

            hasChanged |= ImGui.SliderInt(GetLoc("滑步时间阈值(ms)"), ref Config.SlideCastAdjust, 0, 1000);
            hasChanged |= ImGui.ColorEdit4(GetLoc("滑步标记颜色"), ref Config.SlideCastColor);
            hasChanged |= ImGui.ColorEdit4(GetLoc("可滑步标记颜色"), ref Config.SlideCastReadyColor);
            ImGui.Unindent();
            ImGui.Unindent();
        }

        ImGui.Dummy(new Vector2(5) * ImGui.GetIO().FontGlobalScale);

        if (hasChanged)
        {
            OnAddon(AddonEvent.PreFinalize, null);//统一更新咏唱栏
            SaveConfig(Config);//再保存一次用户配置
        }
    }

    protected override void Init()
    {
        Config = LoadConfig<Configs>() ?? new(); //加载用户配置
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_CastBar", OnAddon);//注册咏唱栏更新事件监听
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_CastBar", OnAddon);//注册咏唱栏注销事件监听
    }
    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);//注销监听器
        OnAddon(AddonEvent.PreFinalize, null);//最后对咏唱栏进行一次复原
        if (Config != null)
            SaveConfig(Config);//退出时保存用户配置
    }
    protected void OnAddon(AddonEvent type, AddonArgs args)
    {
        var barNode = CastBar->GetNodeById(3);
        var icon = (AtkComponentNode*)CastBar->GetNodeById(8);
        var countdownText = CastBar->GetTextNodeById(7);
        var castingText = CastBar->GetTextNodeById(6);
        var skillNameText = CastBar->GetTextNodeById(4);
        var progressBar = (AtkNineGridNode*)CastBar->GetNodeById(11);
        var interruptedText = CastBar->GetTextNodeById(2);
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
                if (CastBar->UldManager.NodeList == null || CastBar->UldManager.NodeListCount < 12) return;//如果节点列表为空或者节点数小于12 可能有别的插件改动过咏唱栏 就不对其进行修改 不然可能会出问题


                if (Config.RemoveIcon)
                    icon->AtkResNode.ToggleVisibility(false);
                if (Config.RemoveName)
                    skillNameText->AtkResNode.ToggleVisibility(false);
                if (Config.RemoveCounter)
                    countdownText->AtkResNode.ToggleVisibility(false);
                if (Config.RemoveCastingText)
                    castingText->AtkResNode.ToggleVisibility(false);

                if (Config.RemoveCastingText && !Config.RemoveCounter)//此函数用来修改用倒计时的位置
                {
                    countdownText->AlignmentFontType = (byte)(0x20 | (byte)Config.AlignCounter);
                    SetSize(countdownText, barNode->Width - 8, null);
                    SetPosition(countdownText, barNode->X + 4 + Config.OffsetCounter.X, 30 + Config.OffsetCounter.Y);
                }
                else
                {
                    countdownText->AlignmentFontType = 0x20 | (byte)Alignment.Right;
                    SetSize(countdownText, 42, null);
                    SetPosition(countdownText, 170, null);
                }

                if (!Config.RemoveName)
                {
                    skillNameText->AlignmentFontType = (byte)(0x00 | (byte)Config.AlignName);
                    SetPosition(skillNameText, (barNode->X + 4) + Config.OffsetName.X, Config.OffsetName.Y);
                    SetSize(skillNameText, barNode->Width - 8, null);
                }

                if (Config.RemoveInterruptedText)
                    interruptedText->AtkResNode.SetScale(0, 0);

                if (Config.SlideCast && Config.ClassicSlideCast == false)
                {
                    if (classicSlideMarker != null)
                        classicSlideMarker.IsVisible = false;
                    if (slideMarker == null)
                    {
                        // 创建滑步节点
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

                        Service.AddonController.AttachNode(slideMarker, CastBar->GetNodeById(10));
                    }

                    if (slideMarker != null)
                    {
                        var slidePer = ((float)(((AddonCastBar*)CastBar)->CastTime * 10) - Config.SlideCastAdjust) / (((AddonCastBar*)CastBar)->CastTime * 10);
                        var pos = 160 * slidePer;
                        slideMarker.IsVisible = true;
                        slideMarker.Size = new Vector2(168 - (int)pos, 15);
                        slideMarker.Position = new Vector2(pos - 11, 3);
                        var c = (slidePer * 100) >= ((AddonCastBar*)CastBar)->CastPercent ? Config.SlideCastColor : Config.SlideCastReadyColor;
                        slideMarker.AddColor = new Vector3(c.X, c.Y, c.Z);//用于改变滑步节点颜色 由于滑步节点本身颜色会随着读条进度变化 所以用叠加色来改变
                        slideMarker.MultiplyColor = new Vector3(c.X, c.Y, c.Z);
                        slideMarker.Alpha = c.W;
                        slideMarker.PartId = 0;
                    }

                }
                else if (Config.SlideCast && Config.ClassicSlideCast)
                {
                    if (slideMarker != null)
                        slideMarker.IsVisible = false;
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

                        var slidePer = ((float)(((AddonCastBar*)CastBar)->CastTime * 10) - Config.SlideCastAdjust) / (((AddonCastBar*)CastBar)->CastTime * 10);
                        var pos = 160 * slidePer;

                        classicSlideMarker.Width = (ushort)Config.ClassicSlideCastWidth;
                        classicSlideMarker.Height = (ushort)(12 + Config.ClassicSlideCastOverHeight * 2);
                        classicSlideMarker.Position = new Vector2(pos, 4 - Config.ClassicSlideCastOverHeight);

                        var c = (slidePer * 100) >= ((AddonCastBar*)CastBar)->CastPercent ? Config.SlideCastColor : Config.SlideCastReadyColor;
                        classicSlideMarker.Color = new Vector4(c.X, c.Y, c.Z, c.W);
                    }
                }






                return;
        }
        return;
    }//使用了监听器方法进行重构
}
