using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic;

public class OptimizedLetter : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedLetterTitle"),
        Description = GetLoc("OptimizedLetterDescription"),
        Category    = ModuleCategories.UIOptimization,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    [IPCSubscriber("DailyRoutines.Modules.OptimizedFriendlist.GetRemarkByContentID", DefaultValue = "")]
    private static IPCSubscriber<ulong, string> GetRemarkByContentID;
    
    [IPCSubscriber("DailyRoutines.Modules.OptimizedFriendlist.GetNicknameByContentID", DefaultValue = "")]
    private static IPCSubscriber<ulong, string> GetNicknameByContentID;

    private static AddonDROptimizedLetter? Addon;
    
    private static TextInputNode? TextInputButton;
    private static TextListNode? ListNode;
    
    protected override void Init()
    {
        TaskHelper ??= new();
        Addon ??= new(TaskHelper)
        {
            InternalName = "DROptimizedLetter",
            Title        = Info.Title,
            Size         = new(290f, 200f),
        };

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonSelectYesNo);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "LetterAddress", OnAddonLetterAddress);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LetterAddress", OnAddonLetterAddress);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,  "LetterList",  OnAddon);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonSelectYesNo);
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        DService.AddonLifecycle.UnregisterListener(OnAddonLetterAddress);
        OnAddonLetterAddress(AddonEvent.PreFinalize, null);
        
        Addon?.Dispose();
        Addon = null;
    }
    
    private static unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        if (Addon.IsOpen || !IsAddonAndNodesReady(LetterList)) return;
        Addon.Open();
    }
    
    private static unsafe void OnAddonLetterAddress(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                TextInputButton?.DetachNode();
                TextInputButton = null;
                
                ListNode?.DetachNode();
                ListNode = null;
                break;
            
            case AddonEvent.PostDraw:
                if (LetterAddress == null) return;

                if (TextInputButton == null)
                {
                    TextInputButton = new()
                    {
                        IsVisible = true,
                        Size      = new(200, 30),
                        Position  = new(18, 38),
                        OnInputReceived = name =>
                        {
                            ListNode?.DetachNode();
                            ListNode = null;
                            
                            List<string> names = [];
                            foreach (var chara in InfoProxyFriendList.Instance()->CharDataSpan)
                            {
                                if (chara.HomeWorld != GameState.HomeWorld) continue;
                                
                                var remark   = GetRemarkByContentID.TryInvokeFunc(chara.ContentId)   ?? string.Empty;
                                var nickname = GetNicknameByContentID.TryInvokeFunc(chara.ContentId) ?? string.Empty;

                                if (chara.NameString.Contains(name.ExtractText(), StringComparison.OrdinalIgnoreCase)                                       ||
                                    PinyinHelper.GetPinyin(chara.NameString, string.Empty).Contains(name.ExtractText(), StringComparison.OrdinalIgnoreCase) ||
                                    remark.Contains(name.ExtractText(), StringComparison.OrdinalIgnoreCase)                                                 ||
                                    PinyinHelper.GetPinyin(remark, string.Empty).Contains(name.ExtractText(), StringComparison.OrdinalIgnoreCase)           ||
                                    nickname.Contains(name.ExtractText(), StringComparison.OrdinalIgnoreCase)                                               ||
                                    PinyinHelper.GetPinyin(nickname, string.Empty).Contains(name.ExtractText(), StringComparison.OrdinalIgnoreCase))
                                    names.Add(chara.NameString);
                            }
                            if (names.Count == 0) return;

                            ListNode = new()
                            {
                                IsVisible  = true,
                                Position   = new(16, 68),
                                MaxButtons = (int)MathF.Min(names.Count, 8),
                                Options    = names,
                                Size       = new(300, 192),
                                OnOptionSelected = option =>
                                {
                                    SendEvent(AgentId.LetterEdit, 8, 2, 1, option);
                                    SendEvent(AgentId.LetterEdit, 8, -1);

                                    if (LetterEditor != null)
                                        LetterEditor->GetComponentButtonById(3)->SetText(option);

                                    if (LetterAddress != null)
                                        LetterAddress->Close(true);
                                }
                            };

                            if (names.Count <= 8)
                                ListNode.ScrollBarNode.IsVisible = false;
                            
                            ListNode.AttachNode(LetterAddress->RootNode);
                        }
                    };
                    TextInputButton.AttachNode(LetterAddress->RootNode);
                }

                if (ListNode != null)
                {
                    var shouldDisplay = !string.IsNullOrWhiteSpace(TextInputButton.SeString.ExtractText());
                    ListNode.IsVisible = shouldDisplay;
                }
                
                break;
        }
    }
    
    private void OnAddonSelectYesNo(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        ClickSelectYesnoYes();
    }

    private class AddonDROptimizedLetter(TaskHelper TaskHelper) : NativeAddon
    {
        private static AtkEventWrapper? FireRequestEvent;
        
        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            if (IsAddonAndNodesReady(LetterList))
            {
                var button = LetterList->GetComponentButtonById(4);
                if (button != null)
                {
                    button->OwnerNode->ClearEvents();

                    FireRequestEvent ??= new AtkEventWrapper((_, _, _) =>
                    {
                        if (!IsAddonAndNodesReady(LetterList)) return;
                        
                        var buttonNode = LetterList->GetComponentButtonById(4);
                        if (buttonNode != null)
                        {
                            SendEvent(AgentId.LetterList, 9, 0);
                            buttonNode->SetEnabledState(false);
                            
                            TaskHelper.Abort();
                            TaskHelper.DelayNext(200);
                            TaskHelper.Enqueue(() =>
                            {
                                if (buttonNode == null) return;
                                buttonNode->SetEnabledState(true);
                            });
                        }
                    });
                    
                    FireRequestEvent.Add(addon, (AtkResNode*)button->OwnerNode, AtkEventType.ButtonClick);
                }
            }
            
            var layoutNode = new VerticalListNode
            {
                IsVisible   = true,
                Position    = ContentStartPosition + new Vector2(0, 2),
                ItemSpacing = 1,
                Size        = new(275, 28),
                FitContents = true,
            };

            var deleteAllButton = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(layoutNode.Size.X - 10, 38),
                SeString  = $"{GetLoc("OptimizedLetter-DeleteMails")} ({GetLoc("All")})",
                OnClick = () =>
                {
                    if (!TryFindLetters(_ => true, out var letters)) return;
                    foreach (var (index, _) in letters)
                    {
                        SendEvent(AgentId.LetterList, 0, 0, index, 0, 1);
                        SendEvent(AgentId.LetterList, 4, 0);
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
                SeString  = $"{GetLoc("OptimizedLetter-DeleteMails")} ({GetLoc("OptimizedLetter-DeleteMails-ExceptPlayers")})",
                OnClick = () =>
                {
                    if (!TryFindLetters(x => x.SenderContentId < 100000000000, out var letters)) return;
                    foreach (var (index, _) in letters)
                    {
                        SendEvent(AgentId.LetterList, 0, 0, index, 0, 1);
                        SendEvent(AgentId.LetterList, 4, 0);
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
                SeString  = GetLoc("OptimizedLetter-ClaimMails"),
                OnClick = () =>
                {
                    if (!TryFindLetters(x => x.Attachments.ToArray().Any(d => d.Count > 0), out var letters)) return;
                    foreach (var (index, _) in letters)
                    {
                        TaskHelper.Enqueue(() => SendEvent(AgentId.LetterList, 0, 0, index, 0, 1));
                        TaskHelper.Enqueue(() => SendEvent(AgentId.LetterList, 1, 0, 0, 0U, 0, 0));
                        TaskHelper.Enqueue(() => IsAddonAndNodesReady(LetterViewer));
                        TaskHelper.Enqueue(() => SendEvent(AgentId.LetterView, 0, 1));
                        TaskHelper.Enqueue(() => AtkStage.Instance()->GetNumberArrayData(NumberArrayType.Letter)->IntArray[136] == 0);
                        TaskHelper.Enqueue(() =>
                        {
                            LetterViewer->Close(true);
                            SendEvent(AgentId.LetterView, 0, -1);
                        });
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

            var position = new Vector2(LetterList->RootNode->ScreenX - addon->GetScaledWidth(true), LetterList->RootNode->ScreenY);
            SetPosition(addon,           position);
            SetPosition(addon->RootNode, position);
        }

        protected override unsafe void OnFinalize(AtkUnitBase* addon) 
        {
            FireRequestEvent?.Dispose();
            
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
}
