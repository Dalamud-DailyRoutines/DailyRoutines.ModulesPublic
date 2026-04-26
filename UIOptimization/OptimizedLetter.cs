using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using TinyPinyin;
using AtkEventWrapper = OmenTools.OmenService.AtkEventWrapper;

namespace DailyRoutines.ModulesPublic;

public class OptimizedLetter : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedLetterTitle"),
        Description = Lang.Get("OptimizedLetterDescription"),
        Category    = ModuleCategory.UIOptimization
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
            Size         = new(290f, 200f)
        };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonSelectYesNo);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "LetterAddress", OnAddonLetterAddress);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LetterAddress", OnAddonLetterAddress);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "LetterList", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSelectYesNo);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonLetterAddress);
        OnAddonLetterAddress(AddonEvent.PreFinalize, null);

        addon?.Dispose();
        addon = null;
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        if (addon.IsOpen || !LetterList->IsAddonAndNodesReady()) return;
        addon.Open();
    }

    private unsafe void OnAddonLetterAddress(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                textInputButton?.Dispose();
                textInputButton = null;

                listNode?.Dispose();
                listNode = null;
                break;

            case AddonEvent.PostDraw:
                if (LetterAddress == null) return;

                if (textInputButton == null)
                {
                    textInputButton = new()
                    {
                        IsVisible = true,
                        Size      = new(200, 30),
                        Position  = new(18, 38),
                        OnInputReceived = name =>
                        {
                            listNode?.Dispose();
                            listNode = null;

                            List<string> names = [];

                            foreach (var chara in InfoProxyFriendList.Instance()->CharDataSpan)
                            {
                                if (chara.HomeWorld != GameState.HomeWorld) continue;

                                var remark   = GetRemarkByContentID.TryInvokeFunc(chara.ContentId)   ?? string.Empty;
                                var nickname = GetNicknameByContentID.TryInvokeFunc(chara.ContentId) ?? string.Empty;

                                if (chara.NameString.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase)                                       ||
                                    PinyinHelper.GetPinyin(chara.NameString, string.Empty).Contains(name.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                    remark.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase)                                                 ||
                                    PinyinHelper.GetPinyin(remark, string.Empty).Contains(name.ToString(), StringComparison.OrdinalIgnoreCase)           ||
                                    nickname.Contains(name.ToString(), StringComparison.OrdinalIgnoreCase)                                               ||
                                    PinyinHelper.GetPinyin(nickname, string.Empty).Contains(name.ToString(), StringComparison.OrdinalIgnoreCase))
                                    names.Add(chara.NameString);
                            }

                            if (names.Count == 0) return;

                            listNode = new()
                            {
                                IsVisible  = true,
                                Position   = new(16, 68),
                                MaxButtons = (int)MathF.Min(names.Count, 8),
                                Options    = names,
                                Size       = new(300, 192),
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

                            if (names.Count <= 8)
                                listNode.ScrollBarNode.IsVisible = false;

                            listNode.AttachNode(LetterAddress->RootNode);
                        }
                    };
                    textInputButton.AttachNode(LetterAddress->RootNode);
                }

                if (listNode != null)
                {
                    var shouldDisplay = !string.IsNullOrWhiteSpace(textInputButton.String.ToString());
                    listNode.IsVisible = shouldDisplay;
                }

                break;
        }
    }

    private void OnAddonSelectYesNo(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        AddonSelectYesnoEvent.ClickYes();
    }

    private class AddonDROptimizedLetter
    (
        TaskHelper taskHelper
    ) : NativeAddon
    {
        private static AtkEventWrapper? FireRequestEvent;

        protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
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

                            var buttonNode = LetterList->GetComponentButtonById(4);

                            if (buttonNode != null)
                            {
                                AgentId.LetterList.SendEvent(9, 0);
                                buttonNode->SetEnabledState(false);

                                taskHelper.Abort();
                                taskHelper.DelayNext(200);
                                taskHelper.Enqueue
                                (() =>
                                    {
                                        if (buttonNode == null) return;
                                        buttonNode->SetEnabledState(true);
                                    }
                                );
                            }
                        }
                    );

                    FireRequestEvent.Add(addon, (AtkResNode*)button->OwnerNode, AtkEventType.ButtonClick);
                }
            }

            var layoutNode = new VerticalListNode
            {
                IsVisible   = true,
                Position    = ContentStartPosition + new Vector2(0, 2),
                ItemSpacing = 1,
                Size        = new(275, 28),
                FitContents = true
            };

            var deleteAllButton = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(layoutNode.Size.X - 10, 38),
                String    = $"{Lang.Get("OptimizedLetter-DeleteMails")} ({Lang.Get("All")})",
                OnClick = () =>
                {
                    if (!TryFindLetters(_ => true, out var letters)) return;

                    foreach (var (index, _) in letters)
                    {
                        AgentId.LetterList.SendEvent(0, 0, index, 0, 1);
                        AgentId.LetterList.SendEvent(4, 0);
                    }
                }
            };
            layoutNode.AddNode(deleteAllButton);
            layoutNode.AddDummy(5);

            var deleteNonPlayerButton = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(layoutNode.Size.X - 10, 38),
                String    = $"{Lang.Get("OptimizedLetter-DeleteMails")} ({Lang.Get("OptimizedLetter-DeleteMails-ExceptPlayers")})",
                OnClick = () =>
                {
                    if (!TryFindLetters(x => x.SenderContentId < 100000000000, out var letters)) return;

                    foreach (var (index, _) in letters)
                    {
                        AgentId.LetterList.SendEvent(0, 0, index, 0, 1);
                        AgentId.LetterList.SendEvent(4, 0);
                    }
                }
            };
            layoutNode.AddNode(deleteNonPlayerButton);
            layoutNode.AddDummy(5);

            layoutNode.AddDummy(5);

            var claimAllButton = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(layoutNode.Size.X - 10, 38),
                String    = Lang.Get("OptimizedLetter-ClaimMails"),
                OnClick = () =>
                {
                    if (!TryFindLetters(x => x.Attachments.ToArray().Any(d => d.Count > 0), out var letters)) return;

                    foreach (var (index, _) in letters)
                    {
                        taskHelper.Enqueue(() => AgentId.LetterList.SendEvent(0, 0, index, 0,  1));
                        taskHelper.Enqueue(() => AgentId.LetterList.SendEvent(1, 0, 0,     0U, 0, 0));
                        taskHelper.Enqueue(() => LetterViewer->IsAddonAndNodesReady());
                        taskHelper.Enqueue(() => AgentId.LetterView.SendEvent(0, 1));
                        taskHelper.Enqueue(() => AtkStage.Instance()->GetNumberArrayData(NumberArrayType.Letter)->IntArray[136] == 0);
                        taskHelper.Enqueue
                        (() =>
                            {
                                LetterViewer->Close(true);
                                AgentId.LetterView.SendEvent(0, -1);
                            }
                        );
                    }
                }
            };
            layoutNode.AddNode(claimAllButton);
            layoutNode.AttachNode(this);
        }

        protected override unsafe void OnUpdate(AtkUnitBase* addon)
        {
            if (LetterList == null)
            {
                Close();
                return;
            }

            SetWindowPosition
            (
                new
                (
                    LetterList->RootNode->ScreenX - addon->GetScaledWidth(true),
                    LetterList->RootNode->ScreenY
                )
            );
        }

        protected override unsafe void OnFinalize(AtkUnitBase* addon)
        {
            FireRequestEvent?.Dispose();
            FireRequestEvent = null;

            if (LetterList == null) return;
            LetterList->Close(true);
        }

        private static unsafe bool TryFindLetters(Predicate<InfoProxyLetter.Letter> predicate, out List<(int Index, InfoProxyLetter.Letter)> letters)
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
