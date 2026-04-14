using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedTargetInfo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedTargetInfoTitle"),
        Description = Lang.Get("OptimizedTargetInfoDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private TextNode? targetHPTextNode;
    private TextNode? focusTargetHPTextNode;
    private TextNode? mainTargetSplitHPTextNode;

    private TextNode? targetCastBarTextNode;
    private TextNode? targetSplitCastBarTextNode;
    private TextNode? focusTargetCastBarTextNode;

    private TextButtonNode? clearFocusButtonNode;

    private int numberPreview = 12345678;

    private int currentSecondRowOffset = 41;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_TargetInfo", OnAddonTargetInfo);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_TargetInfo", OnAddonTargetInfo);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "_TargetInfo", OnAddonTargetInfo);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_TargetInfoMainTarget", OnAddonTargetInfoSplitTarget);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_TargetInfoMainTarget", OnAddonTargetInfoSplitTarget);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "_TargetInfoMainTarget", OnAddonTargetInfoSplitTarget);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_FocusTargetInfo", OnAddonFocusTargetInfo);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_FocusTargetInfo", OnAddonFocusTargetInfo);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "_FocusTargetInfo", OnAddonFocusTargetInfo);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_TargetInfoCastBar", OnAddonTargetInfoCastBar);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_TargetInfoCastBar", OnAddonTargetInfoCastBar);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "_TargetInfoCastBar", OnAddonTargetInfoCastBar);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_TargetInfoCastBar", OnAddonTargetInfoCastBar);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_TargetInfoCastBar", OnAddonTargetInfoCastBar);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "_TargetInfoCastBar", OnAddonTargetInfoCastBar);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_TargetInfoBuffDebuff", OnAddonTargetInfoBuffDebuff);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_TargetInfoBuffDebuff", OnAddonTargetInfoBuffDebuff);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "_TargetInfoBuffDebuff", OnAddonTargetInfoBuffDebuff);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "CastBarEnemy", OnAddonCastBarEnemy);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "CastBarEnemy", OnAddonCastBarEnemy);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "CastBarEnemy", OnAddonCastBarEnemy);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonTargetInfo);
        OnAddonTargetInfo(AddonEvent.PreFinalize, null);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonTargetInfoSplitTarget);
        OnAddonTargetInfoSplitTarget(AddonEvent.PreFinalize, null);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonFocusTargetInfo);
        OnAddonFocusTargetInfo(AddonEvent.PreFinalize, null);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonTargetInfoCastBar);
        OnAddonTargetInfoCastBar(AddonEvent.PreFinalize, null);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonTargetInfoBuffDebuff);
        OnAddonTargetInfoBuffDebuff(AddonEvent.PreFinalize, null);

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonCastBarEnemy);
        OnAddonCastBarEnemy(AddonEvent.PreFinalize, null);
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("OptimizedTargetInfo-DisplayFormat")}");

        ImGui.SetNextItemWidth(400f * GlobalUIScale);

        using (ImRaii.PushIndent())
        using (var combo = ImRaii.Combo
               (
                   "###DisplayFormatCombo",
                   $"{DisplayFormatLoc.GetValueOrDefault(config.DisplayFormat, Lang.Get("OptimizedTargetInfo-UnknownDisplayFormat"))} " +
                   $"({FormatNumber((uint)numberPreview, config.DisplayFormat)})",
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{Lang.Get("OptimizedTargetInfo-NumberPreview")}:");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputInt("###PreviewNumberInput", ref numberPreview))
                    numberPreview = (int)Math.Clamp(numberPreview, 0, uint.MaxValue);

                ImGui.Separator();
                ImGui.Spacing();

                foreach (var displayFormat in Enum.GetValues<DisplayFormat>())
                {
                    if (ImGui.Selectable
                        (
                            $"{DisplayFormatLoc.GetValueOrDefault(displayFormat, Lang.Get("OptimizedTargetInfo-UnknownDisplayFormat"))} " +
                            $"({FormatNumber((uint)numberPreview, displayFormat)})##FormatSelect",
                            config.DisplayFormat == displayFormat
                        ))
                    {
                        config.DisplayFormat = displayFormat;
                        config.Save(this);
                    }
                }
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("OptimizedTargetInfo-DisplayStringFormat")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(400f * GlobalUIScale);
            ImGui.InputText("###DisplayStringFormatInput", ref config.DisplayFormatString, 128);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
            ImGuiOm.HelpMarker(Lang.Get("OptimizedTargetInfo-DisplayStringFormatHelp"));
        }

        ImGui.NewLine();

        // 目标
        DrawTargetConfigSection
        (
            LuminaWrapper.GetAddonText(1030),
            "Target",
            ref config.AlignLeft,
            ref config.Position,
            ref config.CustomColor,
            ref config.OutlineColor,
            ref config.FontSize,
            ref config.HideAutoAttack,
            true,
            ref config.IsEnabled
        );

        ImGui.NewLine();

        // 焦点目标
        DrawTargetConfigSection
        (
            LuminaWrapper.GetAddonText(1110),
            "Focus",
            ref config.FocusAlignLeft,
            ref config.FocusPosition,
            ref config.FocusCustomColor,
            ref config.FocusOutlineColor,
            ref config.FocusFontSize,
            ref config.HideAutoAttack,
            false,
            ref config.FocusIsEnabled
        );

        ImGui.NewLine();

        // 咏唱栏
        DrawTargetConfigSection
        (
            LuminaWrapper.GetAddonText(1032),
            "CastBar",
            ref config.CastBarAlignLeft,
            ref config.CastBarPosition,
            ref config.CastBarCustomColor,
            ref config.CastBarOutlineColor,
            ref config.CastBarFontSize,
            ref config.HideAutoAttack,
            false,
            ref config.CastBarIsEnabled
        );

        ImGui.NewLine();

        // 焦点目标咏唱栏
        DrawTargetConfigSection
        (
            $"{LuminaWrapper.GetAddonText(1110)} {LuminaWrapper.GetAddonText(1032)}",
            "FocusCastBar",
            ref config.FocusCastBarAlignLeft,
            ref config.FocusCastBarPosition,
            ref config.FocusCastBarCustomColor,
            ref config.FocusCastBarOutlineColor,
            ref config.FocusCastBarFontSize,
            ref config.HideAutoAttack,
            false,
            ref config.FocusCastBarIsEnabled
        );

        ImGui.NewLine();

        // 状态效果
        using (ImRaii.PushId("Status"))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(215));

            ImGui.SameLine(0, 8f * GlobalUIScale);

            if (ImGui.Checkbox($"{Lang.Get("Enable")}", ref config.StatusIsEnabled))
            {
                config.Save(this);

                if (!config.StatusIsEnabled)
                {
                    OnAddonTargetInfoCastBar(AddonEvent.PreFinalize, null);
                    OnAddonTargetInfoBuffDebuff(AddonEvent.PreFinalize, null);
                }
            }

            if (!config.StatusIsEnabled) return;

            ImGui.Spacing();

            using (ImRaii.PushIndent())
            {
                ImGui.SetNextItemWidth(150f * GlobalUIScale);
                if (ImGui.InputFloat($"{Lang.Get("Scale")}", ref config.StatusScale, 0.1f, 0.1f, "%.2f"))
                    config.StatusScale = Math.Clamp(config.StatusScale, 0.1f, 10f);

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    config.Save(this);

                    OnAddonTargetInfoCastBar(AddonEvent.PreFinalize, null);
                    OnAddonTargetInfoBuffDebuff(AddonEvent.PreFinalize, null);
                }
            }
        }

        ImGui.NewLine();

        // 清除焦点目标
        using (ImRaii.PushId("ClearFocus"))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("OptimizedTargetInfo-ClearFocusTarget")}");

            ImGui.SameLine(0, 8f * GlobalUIScale);
            if (ImGui.Checkbox($"{Lang.Get("Enable")}", ref config.ClearFocusIsEnabled))
                config.Save(this);

            if (!config.ClearFocusIsEnabled) return;

            ImGui.Spacing();

            using (ImRaii.PushIndent())
            {
                ImGui.SetNextItemWidth(150f * GlobalUIScale);
                ImGui.InputFloat2($"{Lang.Get("OptimizedTargetInfo-PosOffset")}", ref config.ClearFocusPosition, format: "%.2f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);
            }
        }
    }

    private void DrawTargetConfigSection
    (
        string      sectionTitle,
        string      prefix,
        ref bool    alignLeft,
        ref Vector2 position,
        ref Vector4 customColor,
        ref Vector4 outlineColor,
        ref byte    fontSize,
        ref bool    hideAutoAttack,
        bool        showHideAutoAttack,
        ref bool    isEnabled
    )
    {
        using var id = ImRaii.PushId($"{prefix}_{sectionTitle}");

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), sectionTitle);

        ImGui.SameLine(0, 8f * GlobalUIScale);
        if (ImGui.Checkbox($"{Lang.Get("Enable")}", ref isEnabled))
            config.Save(this);

        if (!isEnabled) return;

        ImGui.Spacing();

        using var indent = ImRaii.PushIndent();

        if (ImGui.Checkbox($"{Lang.Get("OptimizedTargetInfo-AlignLeft")}###AlignLeft", ref alignLeft))
            config.Save(this);

        if (ImGui.ColorButton($"###{prefix}CustomColorButton", customColor))
            ImGui.OpenPopup($"{prefix}CustomColorPopup");
        ImGuiOm.TooltipHover(Lang.Get("OptimizedTargetInfo-ZeroAlphaHelp"));

        ImGui.SameLine();
        ImGui.TextUnformatted($"{Lang.Get("OptimizedTargetInfo-CustomColor")}");

        using (var popup = ImRaii.Popup($"{prefix}CustomColorPopup"))
        {
            if (popup)
            {
                ImGui.ColorPicker4($"###{prefix}CustomColor", ref customColor);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);
            }
        }

        if (ImGui.ColorButton($"###{prefix}OutlineColorButton", outlineColor))
            ImGui.OpenPopup($"{prefix}OutlineColorPopup");
        ImGuiOm.TooltipHover(Lang.Get("OptimizedTargetInfo-ZeroAlphaHelp"));

        ImGui.SameLine();
        ImGui.TextUnformatted($"{Lang.Get("EdgeColor")}");

        using (var popup = ImRaii.Popup($"{prefix}OutlineColorPopup"))
        {
            if (popup)
            {
                ImGui.ColorPicker4($"###{prefix}OutlineColor", ref outlineColor);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);
            }
        }

        if (showHideAutoAttack)
        {
            if (ImGui.Checkbox($"{Lang.Get("OptimizedTargetInfo-HideAutoAttackIcon")}###{prefix}HideAutoAttackIcon", ref hideAutoAttack))
                config.Save(this);
        }

        ImGui.SetNextItemWidth(150f * GlobalUIScale);
        ImGui.InputFloat2($"{Lang.Get("OptimizedTargetInfo-PosOffset")}###Position", ref position, format: "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        var fontSizeInt = (int)fontSize;
        ImGui.SetNextItemWidth(150f * GlobalUIScale);
        if (ImGui.SliderInt($"{Lang.Get("FontScale")}###FontSize", ref fontSizeInt, 1, 32))
            fontSize = (byte)fontSizeInt;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);
    }
    
    #region 事件

    private static void OnAddonCastBarEnemy(AddonEvent type, AddonArgs args) =>
        HandleAddonEventCastBarEnemy(type);

    private void OnAddonTargetInfo(AddonEvent type, AddonArgs args)
    {
        HandleAddonEventTargetInfo
        (
            type,
            config.IsEnabled,
            config.HideAutoAttack,
            18,
            TargetInfo,
            ref targetHPTextNode,
            16,
            19,
            config.Position,
            config.AlignLeft,
            config.FontSize,
            config.CustomColor,
            config.OutlineColor,
            () => (TargetManager.SoftTarget ?? TargetManager.Target) as IBattleChara,
            (width, height) => new Vector2(width - 5, height + 2)
        );

        HandleAddonEventCastBar
        (
            type,
            config.CastBarIsEnabled,
            TargetInfo,
            ref targetCastBarTextNode,
            10,
            12,
            config.CastBarPosition,
            config.CastBarAlignLeft,
            config.CastBarFontSize,
            config.CastBarCustomColor,
            config.CastBarOutlineColor,
            12,
            () => (TargetManager.SoftTarget ?? TargetManager.Target) as IBattleChara,
            (width, height) => new Vector2(width - 5, height)
        );

        HandleAddonEventTargetStatus(type, TargetInfo, 32);
    }

    private void OnAddonTargetInfoSplitTarget(AddonEvent type, AddonArgs args)
    {
        HandleAddonEventTargetInfo
        (
            type,
            config.IsEnabled,
            config.HideAutoAttack,
            12,
            TargetInfoMainTarget,
            ref mainTargetSplitHPTextNode,
            10,
            13,
            config.Position,
            config.AlignLeft,
            config.FontSize,
            config.CustomColor,
            config.OutlineColor,
            () => (TargetManager.SoftTarget ?? TargetManager.Target) as IBattleChara,
            (width, height) => new Vector2(width - 5, height + 2)
        );
    }

    private void OnAddonFocusTargetInfo(AddonEvent type, AddonArgs args)
    {
        HandleAddonEventTargetInfo
        (
            type,
            config.FocusIsEnabled,
            false,
            0,
            FocusTargetInfo,
            ref focusTargetHPTextNode,
            10,
            18,
            config.FocusPosition,
            config.FocusAlignLeft,
            config.FocusFontSize,
            config.FocusCustomColor,
            config.FocusOutlineColor,
            () => TargetManager.FocusTarget as IBattleChara,
            (width, height) => new Vector2(width - 5, height + 2)
        );

        HandleAddonEventCastBar
        (
            type,
            config.FocusCastBarIsEnabled,
            FocusTargetInfo,
            ref focusTargetCastBarTextNode,
            3,
            5,
            config.FocusCastBarPosition,
            config.FocusCastBarAlignLeft,
            config.FocusCastBarFontSize,
            config.FocusCastBarCustomColor,
            config.FocusCastBarOutlineColor,
            5,
            () => TargetManager.FocusTarget as IBattleChara,
            (width, height) => new Vector2(width - 5, height)
        );

        HandleAddonEventFocusTargetControl(type);
    }

    private void OnAddonTargetInfoCastBar(AddonEvent type, AddonArgs args)
    {
        HandleAddonEventCastBar
        (
            type,
            config.CastBarIsEnabled,
            TargetInfoCastBar,
            ref targetSplitCastBarTextNode,
            2,
            4,
            config.CastBarPosition,
            config.CastBarAlignLeft,
            config.CastBarFontSize,
            config.CastBarCustomColor,
            config.CastBarOutlineColor,
            4,
            () => (TargetManager.SoftTarget ?? TargetManager.Target) as IBattleChara,
            (width, height) => new Vector2(width - 5, height)
        );
    }

    private void OnAddonTargetInfoBuffDebuff(AddonEvent type, AddonArgs args) =>
        HandleAddonEventTargetStatus(type, TargetInfoBuffDebuff, 31);

    #endregion
    
    private void HandleAddonEventFocusTargetControl(AddonEvent type)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                clearFocusButtonNode?.Dispose();
                clearFocusButtonNode = null;
                break;

            case AddonEvent.PostRequestedUpdate:
                if (!FocusTargetInfo->IsAddonAndNodesReady()) return;

                if (clearFocusButtonNode == null)
                {
                    clearFocusButtonNode = new()
                    {
                        IsVisible   = true,
                        Size        = new(32),
                        Position    = new(-13, 12),
                        String      = "\ue04c",
                        TextTooltip = Lang.Get("OptimizedTargetInfo-ClearFocusTarget"),
                        OnClick     = () => TargetSystem.Instance()->SetFocusTargetByObjectId(0xE0000000)
                    };
                    clearFocusButtonNode.BackgroundNode.IsVisible = false;
                    clearFocusButtonNode.AttachNode(FocusTargetInfo->RootNode);
                }

                clearFocusButtonNode.IsVisible = config.ClearFocusIsEnabled;
                clearFocusButtonNode.Position  = new Vector2(-13, 12) + config.ClearFocusPosition;
                break;
        }
    }

    // 状态效果
    private void HandleAddonEventTargetStatus(AddonEvent type, AtkUnitBase* addon, int statusNodeStartIndex)
    {
        if (!addon->IsAddonAndNodesReady()) return;

        switch (type)
        {
            case AddonEvent.PreFinalize:
                for (var i = 0; i < 15; i++)
                {
                    var node = addon->UldManager.NodeList[statusNodeStartIndex - i];
                    node->ScaleX    =  1.0f;
                    node->ScaleY    =  1.0f;
                    node->X         =  i * 25;
                    node->Y         =  0;
                    node->DrawFlags |= 0x1;
                }

                for (var i = statusNodeStartIndex - 14; i >= statusNodeStartIndex - 29; i--)
                {
                    addon->UldManager.NodeList[i]->Y         =  41;
                    addon->UldManager.NodeList[i]->DrawFlags |= 0x1;
                }

                addon->UldManager.NodeList[statusNodeStartIndex - 30]->DrawFlags |= 0x4;

                currentSecondRowOffset = 41;
                break;

            case AddonEvent.PostDraw:
                if (!DService.Instance().Condition[ConditionFlag.InCombat] ||
                    !Throttler.Shared.Throttle($"OptimizedTargetInfo-{addon->NameString}", 10))
                    return;

                HandleAddonEventTargetStatus(AddonEvent.PostRequestedUpdate, addon, statusNodeStartIndex);
                break;
            case AddonEvent.PostRequestedUpdate:
                if (!config.StatusIsEnabled || TargetManager.Target is not IBattleChara target)
                    return;

                var playerStatusCount = 0;

                foreach (var status in target.StatusList)
                {
                    if (status.SourceID == LocalPlayerState.EntityID)
                        playerStatusCount++;
                }

                var adjustOffsetY = -(int)(41 * (config.StatusScale - 1.0f) / 4.5);
                var xIncrement    = (int)((config.StatusScale - 1.0f) * 25);

                var growingOffsetX = 0;

                for (var i = 0; i < 15; i++)
                {
                    var node = addon->UldManager.NodeList[statusNodeStartIndex - i];
                    if (!node->IsVisible()) return;

                    node->X = i * 25 + growingOffsetX;

                    if (i < playerStatusCount)
                    {
                        node->ScaleX   =  config.StatusScale;
                        node->ScaleY   =  config.StatusScale;
                        node->Y        =  adjustOffsetY;
                        growingOffsetX += xIncrement;
                    }
                    else
                    {
                        node->ScaleX = 1.0f;
                        node->ScaleY = 1.0f;
                        node->Y      = 0;
                    }

                    node->DrawFlags |= 0x1;
                }

                var newSecondRowOffset = playerStatusCount > 0 ? (int)(config.StatusScale * 41) : 41;

                if (newSecondRowOffset != currentSecondRowOffset)
                {
                    for (var i = statusNodeStartIndex - 15; i >= statusNodeStartIndex - 29; i--)
                    {
                        addon->UldManager.NodeList[i]->Y         =  newSecondRowOffset;
                        addon->UldManager.NodeList[i]->DrawFlags |= 0x1;
                    }

                    currentSecondRowOffset = newSecondRowOffset;
                }

                addon->UldManager.NodeList[statusNodeStartIndex - 30]->DrawFlags |= 0x4;
                addon->UldManager.NodeList[statusNodeStartIndex - 30]->DrawFlags |= 0x1;
                break;
        }
    }

    // 敌人头上的小咏唱条
    private static void HandleAddonEventCastBarEnemy(AddonEvent type)
    {
        if (!CastBarEnemy->IsAddonAndNodesReady()) return;

        switch (type)
        {
            case AddonEvent.PreFinalize:
                if (CastBarEnemy == null) return;

                for (var i = 10; i > 0; i--)
                {
                    var node = (AtkComponentNode*)CastBarEnemy->UldManager.NodeList[i];
                    if (node == null) continue;

                    var textNode = node->Component->GetTextNodeById(4);
                    if (textNode == null) continue;

                    textNode->SetText(LuminaWrapper.GetAddonText(16482));
                    textNode->FontSize = 12;
                }

                break;

            case AddonEvent.PostDraw:
                if (!DService.Instance().Condition[ConditionFlag.InCombat] ||
                    !Throttler.Shared.Throttle("OptimizedTargetInfo-CastBarEnemy", 10))
                    return;

                HandleAddonEventCastBarEnemy(AddonEvent.PostRequestedUpdate);
                break;
            case AddonEvent.PostRequestedUpdate:
                if (CastBarEnemy == null) return;

                var addon = (AddonCastBarEnemy*)CastBarEnemy;

                var maxCount     = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.CastBarEnemy)->IntArray[1];
                var currentCount = 0;

                foreach (var nodeInfo in addon->CastBarNodes)
                {
                    var componentNode = (AtkComponentNode*)nodeInfo.CastBarNode;
                    if (!componentNode->IsVisible() || !nodeInfo.ProgressBarNode->IsVisible()) continue;

                    if (DService.Instance().ObjectTable.SearchByID(nodeInfo.ObjectId.Id) is not IBattleChara { CurrentCastTime: > 0 } target)
                        continue;

                    currentCount++;

                    var textNode     = componentNode->Component->GetTextNodeById(4);
                    var leftCastTime = target.TotalCastTime - target.CurrentCastTime;

                    textNode->SetText($"{leftCastTime:F2}");
                    textNode->FontSize = 16;

                    if (currentCount >= maxCount)
                        break;
                }

                break;
        }
    }

    // 目标信息
    private void HandleAddonEventTargetInfo
    (
        AddonEvent                type,
        bool                      isEnabled,
        bool                      isHideAutoAttack,
        uint                      autoAttackNodeID,
        AtkUnitBase*              addon,
        ref TextNode?             textNode,
        uint                      textNodeID,
        uint                      gaugeNodeID,
        Vector2                   position,
        bool                      alignLeft,
        byte                      fontSize,
        Vector4                   customColor,
        Vector4                   outlineColor,
        Func<IGameObject?>        getTarget,
        Func<uint, uint, Vector2> getSizeFunc
    )
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                textNode?.Dispose();
                textNode = null;
                break;

            case AddonEvent.PostDraw:
                if (!addon->IsAddonAndNodesReady()) return;

                if (!DService.Instance().Condition[ConditionFlag.InCombat] ||
                    !Throttler.Shared.Throttle($"OptimizedTargetInfo-{addon->NameString}", 10))
                    return;

                HandleAddonEventTargetInfo
                (
                    AddonEvent.PostRequestedUpdate,
                    isEnabled,
                    isHideAutoAttack,
                    autoAttackNodeID,
                    addon,
                    ref textNode,
                    textNodeID,
                    gaugeNodeID,
                    position,
                    alignLeft,
                    fontSize,
                    customColor,
                    outlineColor,
                    getTarget,
                    getSizeFunc
                );
                break;
            case AddonEvent.PostRequestedUpdate:
                if (!addon->IsAddonAndNodesReady()) return;

                if (textNode == null)
                {
                    var sourceTextNode = addon->GetTextNodeById(textNodeID);
                    if (sourceTextNode == null) return;

                    var gauge = addon->GetComponentByNodeId(gaugeNodeID);
                    if (gauge == null) return;

                    textNode = new()
                    {
                        IsVisible        = isEnabled,
                        Position         = position,
                        AlignmentType    = alignLeft ? AlignmentType.BottomLeft : AlignmentType.BottomRight,
                        FontSize         = fontSize,
                        TextFlags        = TextFlags.Edge | TextFlags.Bold,
                        TextColor        = customColor.W  != 0 ? customColor : sourceTextNode->TextColor.ToVector4(),
                        TextOutlineColor = outlineColor.W == 0 ? sourceTextNode->EdgeColor.ToVector4() : outlineColor
                    };

                    textNode.AttachNode(gauge->OwnerNode);
                }

                if (autoAttackNodeID != 0 && isHideAutoAttack)
                {
                    var autoAttackNode = addon->GetImageNodeById(autoAttackNodeID);
                    if (autoAttackNode != null && autoAttackNode->IsVisible())
                        autoAttackNode->ToggleVisibility(false);
                }

                textNode.IsVisible = isEnabled && !DService.Instance().Condition[ConditionFlag.Gathering];
                if (!isEnabled) return;

                if (getTarget() is IBattleChara { ObjectKind: not ObjectKind.GatheringPoint } target)
                {
                    var sourceTextNode = addon->GetTextNodeById(textNodeID);
                    if (sourceTextNode == null) return;

                    var gauge = addon->GetComponentByNodeId(gaugeNodeID);
                    if (gauge == null) return;

                    textNode.Position         = position;
                    textNode.Size             = getSizeFunc(gauge->OwnerNode->Width, gauge->OwnerNode->Height);
                    textNode.AlignmentType    = alignLeft ? AlignmentType.BottomLeft : AlignmentType.BottomRight;
                    textNode.FontSize         = fontSize;
                    textNode.TextColor        = customColor.W  != 0 ? customColor : sourceTextNode->TextColor.ToVector4();
                    textNode.TextOutlineColor = outlineColor.W == 0 ? sourceTextNode->EdgeColor.ToVector4() : outlineColor;

                    textNode.String = string.Format
                    (
                        config.DisplayFormatString,
                        FormatNumber(target.MaxHp),
                        FormatNumber(target.CurrentHp)
                    );
                }

                break;
        }
    }

    // 大咏唱条
    private static void HandleAddonEventCastBar
    (
        AddonEvent                type,
        bool                      isEnabled,
        AtkUnitBase*              addon,
        ref TextNode?             textNode,
        uint                      nodeIDToAttach,
        uint                      textNodeID,
        Vector2                   position,
        bool                      alignLeft,
        byte                      fontSize,
        Vector4                   customColor,
        Vector4                   outlineColor,
        uint                      actionNameTextNodeID,
        Func<IGameObject?>        getTarget,
        Func<uint, uint, Vector2> getSizeFunc
    )
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                textNode?.Dispose();
                textNode = null;
                break;

            case AddonEvent.PostDraw:
                if (!addon->IsAddonAndNodesReady()) return;

                if (!DService.Instance().Condition[ConditionFlag.InCombat] ||
                    !Throttler.Shared.Throttle($"OptimizedTargetInfo-{addon->NameString}", 10))
                    return;

                HandleAddonEventCastBar
                (
                    AddonEvent.PostRequestedUpdate,
                    isEnabled,
                    addon,
                    ref textNode,
                    nodeIDToAttach,
                    textNodeID,
                    position,
                    alignLeft,
                    fontSize,
                    customColor,
                    outlineColor,
                    actionNameTextNodeID,
                    getTarget,
                    getSizeFunc
                );
                break;
            case AddonEvent.PostRequestedUpdate:
                if (!addon->IsAddonAndNodesReady()) return;

                if (textNode == null)
                {
                    var sourceTextNode = addon->GetTextNodeById(textNodeID);
                    if (sourceTextNode == null) return;

                    textNode = new()
                    {
                        IsVisible        = isEnabled,
                        Position         = position + new Vector2(4, -12),
                        AlignmentType    = alignLeft ? AlignmentType.TopLeft : AlignmentType.TopRight,
                        FontSize         = fontSize,
                        TextFlags        = TextFlags.Edge | TextFlags.Bold,
                        TextColor        = customColor.W  != 0 ? customColor : sourceTextNode->TextColor.ToVector4(),
                        TextOutlineColor = outlineColor.W == 0 ? sourceTextNode->EdgeColor.ToVector4() : outlineColor,
                        FontType         = FontType.Miedinger
                    };

                    textNode.AttachNode(addon->GetNodeById(nodeIDToAttach));
                }

                textNode.IsVisible = isEnabled && getTarget() != null;
                if (!textNode.IsVisible) return;

                if (getTarget() is IBattleChara target)
                {
                    var sourceTextNode = addon->GetTextNodeById(textNodeID);
                    if (sourceTextNode == null) return;

                    var actionNameNode = addon->GetTextNodeById(actionNameTextNodeID);
                    if (actionNameNode == null) return;

                    var actionProgressBorderNode = addon->GetImageNodeById(actionNameTextNodeID + 3);
                    if (actionProgressBorderNode == null) return;

                    var leftCastTime = target.TotalCastTime - target.CurrentCastTime;

                    textNode.IsVisible = target.CurrentCastTime > 0 && leftCastTime > 0;
                    actionNameNode->ToggleVisibility(textNode.IsVisible);
                    actionProgressBorderNode->ToggleVisibility(textNode.IsVisible);
                    if (!textNode.IsVisible) return;

                    textNode.Position         = position + new Vector2(4, -12);
                    textNode.Size             = getSizeFunc(sourceTextNode->Width, sourceTextNode->Height);
                    textNode.AlignmentType    = alignLeft ? AlignmentType.TopLeft : AlignmentType.TopRight;
                    textNode.FontSize         = fontSize;
                    textNode.TextColor        = customColor.W  != 0 ? customColor : sourceTextNode->TextColor.ToVector4();
                    textNode.TextOutlineColor = outlineColor.W == 0 ? sourceTextNode->EdgeColor.ToVector4() : outlineColor;

                    textNode.String = $"{leftCastTime:F2}";
                    if (target.CastActionType == ActionType.Action)
                        actionNameNode->SetText(LuminaWrapper.GetActionName(target.CastActionID));
                }

                break;
        }
    }

    private string FormatNumber(uint num, DisplayFormat? displayFormat = null)
    {
        displayFormat ??= config.DisplayFormat;

        switch (displayFormat)
        {
            case DisplayFormat.FullNumber:
                return num.ToString();
            case DisplayFormat.FullNumberSeparators:
                return num.ToString("N0");
            case DisplayFormat.ChineseFull:
                return num.ToChineseString();
            case DisplayFormat.ChineseZeroPrecision:
            case DisplayFormat.ChineseOnePrecision:
            case DisplayFormat.ChineseTwoPrecision:
                var (divisor, unit) = num switch
                {
                    >= 1_0000_0000 => (1_0000_0000f,
                                          GameState.ClientLanguge is Language.ChineseTraditional or Language.Japanese or Language.TraditionalChinese ? "億" : "亿"),
                    >= 1_0000 => (1_0000f, GameState.ClientLanguge is Language.ChineseTraditional or Language.TraditionalChinese ? "萬" : "万"),
                    _         => (1f, string.Empty)
                };

                var value = num / divisor;
                var fStrChinese = displayFormat switch
                {
                    DisplayFormat.ChineseOnePrecision  => "F1",
                    DisplayFormat.ChineseTwoPrecision  => "F2",
                    DisplayFormat.ChineseZeroPrecision => "F0"
                };

                var formattedValue = value.ToString(fStrChinese);
                formattedValue = formattedValue.TrimEnd('0').TrimEnd('.');

                return $"{formattedValue}{unit}";
            case DisplayFormat.ZeroPrecision:
            case DisplayFormat.OnePrecision:
            case DisplayFormat.TwoPrecision:
                var fStrEnglish = displayFormat switch
                {
                    DisplayFormat.OnePrecision  => "F1",
                    DisplayFormat.TwoPrecision  => "F2",
                    DisplayFormat.ZeroPrecision => "F0"
                };

                return num switch
                {
                    >= 1000000 => $"{(num / 1000000f).ToString(fStrEnglish)}M",
                    >= 1000    => $"{(num / 1000f).ToString(fStrEnglish)}K",
                    _          => $"{num}"
                };
            default:
                return num.ToString("N0");
        }
    }

    private class Config : ModuleConfig
    {
        public bool    AlignLeft;
        public bool    CastBarAlignLeft;
        public Vector4 CastBarCustomColor = new(1, 1, 1, 0);
        public byte    CastBarFontSize    = 14;

        public bool    CastBarIsEnabled    = true;
        public Vector4 CastBarOutlineColor = new(0, 0.372549f, 1, 1);
        public Vector2 CastBarPosition     = new(0);

        public bool          ClearFocusIsEnabled = true;
        public Vector2       ClearFocusPosition  = new(0);
        public Vector4       CustomColor         = new(1, 1, 1, 0);
        public DisplayFormat DisplayFormat       = DisplayFormat.ChineseOnePrecision;
        public string        DisplayFormatString = "{0} / {1}";
        public bool          FocusAlignLeft;
        public bool          FocusCastBarAlignLeft;
        public Vector4       FocusCastBarCustomColor = new(1, 1, 1, 0);
        public byte          FocusCastBarFontSize    = 14;

        public bool    FocusCastBarIsEnabled    = true;
        public Vector4 FocusCastBarOutlineColor = new(0, 0.372549f, 1, 1);
        public Vector2 FocusCastBarPosition     = new(0);
        public Vector4 FocusCustomColor         = new(1, 1, 1, 0);
        public byte    FocusFontSize            = 14;

        public bool    FocusIsEnabled    = true;
        public Vector4 FocusOutlineColor = new(0, 0.372549f, 1, 0);
        public Vector2 FocusPosition     = new(0);
        public byte    FontSize          = 14;
        public bool    HideAutoAttack    = true;

        public bool    IsEnabled    = true;
        public Vector4 OutlineColor = new(0, 0.372549f, 1, 0);
        public Vector2 Position     = new(0);

        public bool  StatusIsEnabled = true;
        public float StatusScale     = 1.4f;
    }
    
    private enum DisplayFormat
    {
        FullNumber,
        FullNumberSeparators,
        ChineseFull,
        ChineseZeroPrecision,
        ChineseOnePrecision,
        ChineseTwoPrecision,
        ZeroPrecision,
        OnePrecision,
        TwoPrecision
    }

    #region 常量

    private static readonly FrozenDictionary<DisplayFormat, string> DisplayFormatLoc = new Dictionary<DisplayFormat, string>
    {
        [DisplayFormat.FullNumber]           = Lang.Get("OptimizedTargetInfo-FullNumber"),
        [DisplayFormat.FullNumberSeparators] = Lang.Get("OptimizedTargetInfo-FullNumberSeparators"),
        [DisplayFormat.ChineseFull]          = Lang.Get("OptimizedTargetInfo-ChineseFull"),
        [DisplayFormat.ChineseZeroPrecision] = Lang.Get("OptimizedTargetInfo-ChineseZeroPrecision"),
        [DisplayFormat.ChineseOnePrecision]  = Lang.Get("OptimizedTargetInfo-ChineseOnePrecision"),
        [DisplayFormat.ChineseTwoPrecision]  = Lang.Get("OptimizedTargetInfo-ChineseTwoPrecision"),
        [DisplayFormat.ZeroPrecision]        = Lang.Get("OptimizedTargetInfo-ZeroPrecision"),
        [DisplayFormat.OnePrecision]         = Lang.Get("OptimizedTargetInfo-OnePrecision"),
        [DisplayFormat.TwoPrecision]         = Lang.Get("OptimizedTargetInfo-TwoPrecision")
    }.ToFrozenDictionary();

    #endregion
}
