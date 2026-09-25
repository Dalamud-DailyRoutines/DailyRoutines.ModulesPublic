using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using TinyPinyin;
using AtkEventWrapper = OmenTools.OmenService.AtkEventWrapper;

namespace DailyRoutines.ModulesPublic.Interface;

public class OptimizedLetter : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedLetterTitle"),
        Description = Lang.Get("OptimizedLetterDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private AddonDROptimizedLetter? addon;

    private TextInputNode?      textInputButton;
    private TextButtonListNode? listNode;

    protected override void Init()
    {
        TaskHelper ??= new();
        addon ??= new(TaskHelper)
        {
            InternalName = "DROptimizedLetter",
            Title        = Info.Title,
            Size         = new(290f, 190f)
        };

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonSelectYesNo);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,    "LetterAddress", OnAddonLetterAddress);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "LetterAddress", OnAddonLetterAddress);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw, "LetterList", OnAddon);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddonSelectYesNo);
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        IAddonLifecycle.Instance().UnregisterListener(OnAddonLetterAddress);

        textInputButton?.Dispose();
        textInputButton = null;

        listNode?.Dispose();
        listNode = null;

        addon?.Dispose();
        addon = null;
    }

    private unsafe void OnAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        if (addon.IsOpen || !LetterList->IsAddonAndNodesReady()) return;
        addon.Open();
    }

    private unsafe void OnAddonLetterAddress
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                textInputButton = null;
                listNode        = null;
                break;

            case AddonEvent.PostDraw:
                if (LetterAddress   == null) return;
                if (textInputButton != null) return;

                var titleNode = LetterAddress->GetTextNodeById(3);
                if (titleNode != null)
                    titleNode->ToggleVisibility(false);

                textInputButton = new()
                {
                    Size     = new(200, 30),
                    Position = new(12, 32),
                    OnInputReceived = name =>
                    {
                        if (listNode == null)
                        {
                            listNode = new()
                            {
                                IsVisible = false,
                                Position  = new(16, 68),
                                Size      = new(316, 192),
                                OnOptionSelected = option =>
                                {
                                    AgentId.LetterEdit.SendEvent(8, 2, 1, option);
                                    AgentId.LetterEdit.SendEvent(8, -1);

                                    if (LetterEditor != null)
                                        LetterEditor->GetComponentButtonById(3)->SetText(option);

                                    if (LetterAddress != null)
                                        LetterAddress->Close(true);
                                }
                            };

                            listNode.BackgroundNode.IsVisible            = false;
                            listNode.ScrollingListNode.AutoHideScrollBar = true;

                            listNode.AttachNode(LetterAddress);
                        }

                        List<string> names = [];

                        foreach (var chara in InfoProxyFriendList.Instance()->CharDataSpan)
                        {
                            if (chara.HomeWorld != GameState.HomeWorld) continue;

                            var remark   = GetRemarkByContentID.TryInvokeFunc(chara.ContentId)   ?? string.Empty;
                            var nickname = GetNicknameByContentID.TryInvokeFunc(chara.ContentId) ?? string.Empty;

                            var namePinyin     = PinyinHelper.GetPinyin(chara.NameString, string.Empty);
                            var remarkPinyin   = PinyinHelper.GetPinyin(remark,           string.Empty);
                            var nickNamePinyin = PinyinHelper.GetPinyin(nickname,         string.Empty);

                            if (chara.NameString.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                namePinyin.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase)       ||
                                remark.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase)           ||
                                remarkPinyin.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase)     ||
                                nickname.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase)         ||
                                nickNamePinyin.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase))
                                names.Add(chara.NameString);
                        }

                        var isInputEmpty = string.IsNullOrWhiteSpace(name.ToString());

                        listNode.IsVisible = !isInputEmpty;

                        var origList = LetterAddress->GetComponentListById(7);
                        if (origList != null)
                            origList->OwnerNode->ToggleVisibility(isInputEmpty);

                        listNode.MaxButtons = (int)MathF.Min(names.Count, 8);
                        listNode.Options    = names;
                    }
                };
                textInputButton.AttachNode(LetterAddress->RootNode);

                if (listNode != null)
                {
                    var shouldDisplay = !string.IsNullOrWhiteSpace(textInputButton.String.ToString());
                    listNode.IsVisible = shouldDisplay;
                }

                break;
        }
    }

    private void OnAddonSelectYesNo
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (!TaskHelper.IsBusy) return;
        AddonSelectYesnoEvent.ClickYes();
    }

    private class AddonDROptimizedLetter
    (
        TaskHelper taskHelper
    ) : AttachedAddon("LetterList")
    {
        private static AtkEventWrapper? FireRequestEvent;

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            if (LetterList->IsAddonAndNodesReady())
            {
                var button = LetterList->GetComponentButtonById(4);

                if (button != null)
                {
                    button->OwnerNode->ClearEvents();

                    FireRequestEvent = new AtkEventWrapper
                    ((_, _, _, _) =>
                        {
                            if (!LetterList->IsAddonAndNodesReady()) return;

                            NotifyHelper.Toast(Lang.Get("OptimizedLetter-Notification-RewardDeliveryRequested"));
                            InfoProxyLetter.Instance()->RequestRewardDelivery();
                        }
                    );

                    FireRequestEvent.Add(addon, (AtkResNode*)button->OwnerNode, AtkEventType.ButtonClick);
                }
            }

            var layoutNode = new VerticalListNode
            {
                Position    = ContentStartPosition + new Vector2(0, 2),
                ItemSpacing = 1,
                Size        = new(275, 28),
                FitContents = true
            };

            var deleteAllButton = new HoldButtonNode
            {
                UnlockAfterClick = true,
                Size             = new(layoutNode.Size.X - 10, 38),
                String           = Lang.Get("OptimizedLetter-Button-Delete-All"),
                OnClick = () =>
                {
                    if (!TryFindLetters(_ => true, out var letters)) return;

                    var info = InfoProxyLetter.Instance();
                    foreach (var (index, _) in letters)
                        InfoProxyLetter.Instance()->DeleteLetter((uint)index);
                    
                    info->RequestData();
                }
            };
            layoutNode.AddNode(deleteAllButton);
            layoutNode.AddDummy(5);

            var deleteNonPlayerButton = new HoldButtonNode
            {
                UnlockAfterClick = true,
                Size             = new(layoutNode.Size.X - 10, 38),
                String           = Lang.Get("OptimizedLetter-Button-Delete-AllButPlayers"),
                OnClick = () =>
                {
                    if (!TryFindLetters(x => x.SenderContentId < 100000000000, out var letters)) return;

                    var info = InfoProxyLetter.Instance();
                    foreach (var (index, _) in letters)
                        info->DeleteLetter((uint)index);

                    info->RequestData();
                }
            };
            layoutNode.AddNode(deleteNonPlayerButton);
            layoutNode.AddDummy(5);
            
            var claimAllButton = new HoldButtonNode
            {
                UnlockAfterClick = true,
                Size             = new(layoutNode.Size.X - 10, 38),
                String           = Lang.Get("OptimizedLetter-Button-Claim"),
                OnClick = () =>
                {
                    if (!TryFindLetters(x => x.Attachments.ToArray().Any(d => d.Count > 0), out var letters)) return;

                    var info = InfoProxyLetter.Instance();

                    foreach (var (index, _) in letters)
                    {
                        taskHelper.Enqueue(() => info->TakeAttachments((uint)index, -1));
                        taskHelper.Enqueue(() => AtkStage.Instance()->GetNumberArrayData(NumberArrayType.Letter)->IntArray[136] == 0);
                    }
                }
            };
            
            layoutNode.AddNode(claimAllButton);
            layoutNode.AttachNode(this);
        }

        protected override unsafe void OnAttachedAddonFinalize
        (
            AtkUnitBase* addon
        )
        {
            FireRequestEvent?.Dispose();
            FireRequestEvent = null;
        }

        private static unsafe bool TryFindLetters
        (
            Predicate<InfoProxyLetter.Letter>             predicate,
            out List<(int Index, InfoProxyLetter.Letter)> letters
        )
        {
            letters = [];

            var info = InfoProxyLetter.Instance();
            if (info == null) return false;

            for (var index = 0; index < info->Letters.Length; index++)
            {
                var letter = info->Letters[index];
                if (letter.Timestamp == 0) continue;
                if (!predicate(letter)) continue;

                letters.Add((index, letter));
            }

            return letters.Count > 0;
        }
    }

    #region IPC

    [IPCSubscriber("DailyRoutines.Modules.OptimizedFriendlist.GetRemarkByContentID", DefaultValue = "")]
    private IPCSubscriber<ulong, string> GetRemarkByContentID;

    [IPCSubscriber("DailyRoutines.Modules.OptimizedFriendlist.GetNicknameByContentID", DefaultValue = "")]
    private IPCSubscriber<ulong, string> GetNicknameByContentID;

    #endregion
}
