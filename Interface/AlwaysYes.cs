using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic.Interface;

/// <summary>
/// Ported from SimpleTweaksPlugin's Always Yes tweak by Aireil.
/// </summary>
public unsafe class AlwaysYes : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AlwaysYesTitle"),
        Description = Lang.Get("AlwaysYesDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["Sachimi"]
    };

    private Config config = null!;
    private string newException = string.Empty;
    private CancellationTokenSource delayedFocusCancellation = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        delayedFocusCancellation = new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonNames, OnAddonSetup);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox("当“是”不可用且存在复选框时，默认选择复选框", ref config.SelectCheckBox))
            config.Save(this);

        using (ImRaii.Disabled(!config.SelectCheckBox))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("复选框已勾选时忽略上一项设置", ref config.IgnoreTickedCheckBox))
                config.Save(this);
        }

        ImGui.TextUnformatted("应用于：");
        using var indent = ImRaii.PushIndent();

        DrawOption("大多数 是/（长按）/否 对话框", ref config.YesNo);

        using (ImRaii.PushIndent())
        {
            if (ImGui.CollapsingHeader("例外###AlwaysYesExceptions"))
                DrawExceptions();
        }

        DrawOption("任务搜索器确认", ref config.DutyConfirmation);
        DrawOption("九宫幻卡出售", ref config.CardsShop);
        DrawOption("雇员探险", ref config.RetainerVentures);
        DrawOption("雇员重复物品委托", ref config.RetainerEntrustDuplicates);
        DrawOption("镶嵌魔晶石", ref config.MateriaMelds);
        DrawOption("精制魔晶石", ref config.MateriaExtractions);
        DrawOption("回收魔晶石", ref config.MateriaRetrievals);
        DrawOption("解除投影", ref config.GlamourDispels);
        DrawOption("染色确认", ref config.Dyes);
        DrawOption("分解物品", ref config.Desynthesis);
        DrawOption("自动以太还原", ref config.AutomaticAetherialReduction);
        DrawOption("角色选择界面", ref config.Lobby);
        DrawOption("物品交换确认", ref config.ItemExchangeConfirmations);
        DrawOption("金碟游乐场特殊活动退出确认", ref config.BlundervilleExitDialog);
    }

    private void DrawOption(string label, ref bool value)
    {
        if (ImGui.Checkbox(label, ref value))
            config.Save(this);
    }

    private void DrawExceptions()
    {
        ImGui.TextUnformatted("对话文本包含以下内容时不改变默认选项：");

        for (var i = 0; i < config.ExceptionsYesNo.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            var exception = config.ExceptionsYesNo[i];

            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            if (ImGui.InputText("###Exception", ref exception, 500))
            {
                config.ExceptionsYesNo[i] = exception;
                config.Save(this);
            }

            ImGui.SameLine();
            if (!ImGui.Button("删除")) continue;

            config.ExceptionsYesNo.RemoveAt(i--);
            config.Save(this);
        }

        ImGui.SetNextItemWidth(300f * GlobalUIScale);
        ImGui.InputText("###NewException", ref newException, 500);

        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newException)))
        {
            if (!ImGui.Button("添加") || string.IsNullOrWhiteSpace(newException)) return;

            config.ExceptionsYesNo.Add(newException);
            newException = string.Empty;
            config.Save(this);
        }
    }

    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        switch (args.AddonName)
        {
            case "SelectYesno":
                if (config.YesNo && !IsYesNoException(args.Addon.Address))
                    SetFocusOnNextTick(args.AddonName, 8, 9, 4, 1);
                break;
            case "ContentsFinderConfirm":
                if (config.DutyConfirmation) SetFocusYes(args.Addon.Address, 63);
                break;
            case "ShopCardDialog":
                if (config.CardsShop) SetFocusYes(args.Addon.Address, 16);
                break;
            case "RetainerTaskAsk":
                if (config.RetainerVentures) SetFocusYes(args.Addon.Address, 40);
                break;
            case "RetainerItemTransferList":
                if (config.RetainerEntrustDuplicates) SetFocusYes(args.Addon.Address, 7);
                break;
            case "RetainerTaskResult":
                if (config.RetainerVentures) SetFocusYes(args.Addon.Address, 20);
                break;
            case "MateriaAttachDialog":
                if (config.MateriaMelds) SetFocusYes(args.Addon.Address, 35, null, 39);
                break;
            case "MaterializeDialog":
                if (config.MateriaExtractions) SetFocusYes(args.Addon.Address, 13);
                break;
            case "MateriaRetrieveDialog":
                if (config.MateriaRetrievals) SetFocusYes(args.Addon.Address, 17);
                break;
            case "MiragePrismRemove":
                if (config.GlamourDispels) SetFocusYes(args.Addon.Address, 15);
                break;
            case "MiragePrismMiragePlateConfirm":
                if (config.Dyes) SetFocusYes(args.Addon.Address, 6);
                break;
            case "SalvageDialog":
                if (config.Desynthesis) SetFocusOnNextTick(args.AddonName, 25, null, 24);
                break;
            case "PurifyResult":
                if (config.AutomaticAetherialReduction) SetFocusYes(args.Addon.Address, 19);
                break;
            case "LobbyWKTCheck":
                if (config.Lobby) SetFocusYes(args.Addon.Address, 4);
                break;
            case "LobbyDKTWorldList":
                if (config.Lobby) SetFocusOnNextTick(args.AddonName, 25);
                break;
            case "LobbyDKTCheckExec":
                if (config.Lobby) SetFocusOnNextTick(args.AddonName, 3);
                break;
            case "ShopExchangeItemDialog":
                if (config.ItemExchangeConfirmations) SetFocusYes(args.Addon.Address, 18);
                break;
            case "FGSExitDialog":
                if (config.BlundervilleExitDialog) SetSpecialFocus(args.Addon.Address, 10, 6);
                break;
        }
    }

    private void SetFocusOnNextTick
    (
        string addonName,
        uint yesButtonID,
        uint? yesHoldButtonID = null,
        uint? checkBoxID = null,
        int delayTicks = 0
    ) =>
        DService.Instance().Framework.RunOnTick
        (
            () =>
            {
                var addon = DService.Instance().GameGUI.GetAddonByName(addonName);
                if (addon != nint.Zero)
                    SetFocusYes(addon, yesButtonID, yesHoldButtonID, checkBoxID);
            },
            delayTicks: delayTicks,
            cancellationToken: delayedFocusCancellation.Token
        );

    private void SetFocusYes
    (
        nint unitBaseAddress,
        uint yesButtonID,
        uint? yesHoldButtonID = null,
        uint? checkBoxID = null
    )
    {
        var unitBase = (AtkUnitBase*)unitBaseAddress;
        if (unitBase == null || unitBase->UldManager.LoadedState != AtkLoadState.Loaded) return;

        var yesButton = unitBase->GetComponentNodeById(yesButtonID);
        if (yesButton == null || yesButton->Component == null ||
            yesButton->Component->GetComponentType() != ComponentType.Button)
            return;

        var yesButtonEnabled = ((AtkComponentButton*)yesButton->Component)->IsEnabled;

        var checkBox = checkBoxID.HasValue ? unitBase->GetComponentNodeById(checkBoxID.Value) : null;
        var checkBoxComponent = checkBox != null ? checkBox->Component : null;
        if (checkBoxComponent != null && checkBoxComponent->GetComponentType() != ComponentType.CheckBox)
            return;

        var checkBoxVisible = checkBox != null && checkBox->IsVisible();
        var checkBoxTicked = checkBoxComponent != null && ((AtkComponentCheckBox*)checkBoxComponent)->IsChecked;

        AtkComponentNode* targetNode;
        uint collisionID;

        if (config.SelectCheckBox && !yesButtonEnabled && checkBoxVisible &&
            (!config.IgnoreTickedCheckBox || !checkBoxTicked))
        {
            targetNode = checkBox;
            collisionID = 5;
        }
        else
        {
            var holdButton = yesHoldButtonID.HasValue
                                 ? unitBase->GetComponentNodeById(yesHoldButtonID.Value)
                                 : null;

            if (holdButton != null && !yesButton->IsVisible())
            {
                if (holdButton->Component == null ||
                    holdButton->Component->GetComponentType() != ComponentType.HoldButton)
                    return;

                targetNode = holdButton;
                collisionID = 7;
            }
            else
            {
                targetNode = yesButton;
                collisionID = 4;
            }
        }

        var targetComponent = targetNode->Component;
        if (targetComponent == null || targetComponent->UldManager.LoadedState != AtkLoadState.Loaded) return;

        var collision = targetComponent->GetNodeById(collisionID);
        if (collision == null || collision->GetNodeType() != NodeType.Collision) return;

        unitBase->SetFocusNode(collision, true);
    }

    private static void SetSpecialFocus(nint unitBaseAddress, uint buttonID, uint collisionID)
    {
        var unitBase = (AtkUnitBase*)unitBaseAddress;
        if (unitBase == null || unitBase->UldManager.LoadedState != AtkLoadState.Loaded) return;

        var button = unitBase->UldManager.SearchNodeById(buttonID);
        if (button == null || button->GetNodeType() != NodeType.Component) return;

        var buttonComponent = ((AtkComponentNode*)button)->Component;
        if (buttonComponent == null || buttonComponent->UldManager.LoadedState != AtkLoadState.Loaded) return;

        var collision = buttonComponent->UldManager.SearchNodeById(collisionID);
        if (collision == null || collision->GetNodeType() != NodeType.Collision) return;

        unitBase->SetFocusNode(collision);
        unitBase->CursorTarget = collision;
    }

    private bool IsYesNoException(nint unitBaseAddress)
    {
        var unitBase = (AtkUnitBase*)unitBaseAddress;
        if (unitBase == null || config.ExceptionsYesNo.Count == 0) return false;

        var textNode = unitBase->GetTextNodeById(2);
        if (textNode == null) return false;

        var text = textNode->NodeText.ToString().ReplaceLineEndings(string.Empty);
        return !string.IsNullOrEmpty(text) &&
               config.ExceptionsYesNo.Any(x => !string.IsNullOrEmpty(x) && text.Contains(x, StringComparison.Ordinal));
    }

    protected override void Uninit()
    {
        delayedFocusCancellation.Cancel();
        delayedFocusCancellation.Dispose();
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSetup);
    }

    private class Config : ModuleConfig
    {
        public bool SelectCheckBox = true;
        public bool IgnoreTickedCheckBox = true;
        public bool YesNo = true;
        public bool DutyConfirmation = true;
        public bool CardsShop = true;
        public bool RetainerVentures = true;
        public bool RetainerEntrustDuplicates = true;
        public bool MateriaMelds = true;
        public bool MateriaExtractions = true;
        public bool MateriaRetrievals = true;
        public bool GlamourDispels = true;
        public bool Dyes = true;
        public bool Desynthesis = true;
        public bool AutomaticAetherialReduction = true;
        public bool Lobby = true;
        public bool ItemExchangeConfirmations = true;
        public bool BlundervilleExitDialog = true;
        public List<string> ExceptionsYesNo = [];
    }

    private static readonly string[] AddonNames =
    [
        "SelectYesno",
        "ContentsFinderConfirm",
        "ShopCardDialog",
        "RetainerTaskAsk",
        "RetainerItemTransferList",
        "RetainerTaskResult",
        "MateriaAttachDialog",
        "MaterializeDialog",
        "MateriaRetrieveDialog",
        "MiragePrismRemove",
        "MiragePrismMiragePlateConfirm",
        "SalvageDialog",
        "PurifyResult",
        "LobbyWKTCheck",
        "LobbyDKTWorldList",
        "LobbyDKTCheckExec",
        "ShopExchangeItemDialog",
        "FGSExitDialog"
    ];
}
