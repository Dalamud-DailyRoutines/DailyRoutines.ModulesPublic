using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Classes.Timelines;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;
using ActionKind = FFXIVClientStructs.FFXIV.Client.UI.Agent.ActionKind;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public partial class OccultCrescentHelper
{
    public unsafe class OthersManager(OccultCrescentHelper mainModule) : BaseIslandModule(mainModule)
    {
        private static Hook<AgentShowDelegate>? AgentMKDSupportJobShowHook;

        private static TextButtonNode? BuffButton;
        private static TextButtonNode? SettingButton;
        private static TextButtonNode? SupportJobChangeButton;
        private static IconButtonNode? MapButton;

        public static AddonDRMKDSupportJobChange? SupportJobChangeAddon;

        private static int DragDropJobIndex = -1;

        private static IDtrBarEntry? Entry;

        private static TaskHelper? OthersTaskHelper;

        private static bool IsJustLogin;

        public override void Init()
        {
            OthersTaskHelper ??= new() { TimeLimitMS = 30_000 };

            var addedJobs        = ModuleConfig.AddonSupportJobOrder.ToHashSet();
            var isAnyNewJobOrder = false;
            foreach (var job in LuminaGetter.Get<MKDSupportJob>())
            {
                if (addedJobs.Contains(job.RowId)) continue;
                ModuleConfig.AddonSupportJobOrder.Add(job.RowId);
                isAnyNewJobOrder = true;
            }

            if (isAnyNewJobOrder)
                ModuleConfig.Save(MainModule);

            DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "MKDInfo", OnAddon);
            DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MKDInfo", OnAddon);

            DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_CharaSelectListMenu", OnLogin);

            DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_ActionContents", OnActionContents);

            DService.ClientState.TerritoryChanged += OnZoneChanged;
            OnZoneChanged(0);

            SupportJobChangeAddon ??= new()
            {
                InternalName          = "DRMKDSupportJobChange",
                Title                 = LuminaWrapper.GetAddonText(16658),
                Size                  = new(500f, 490f),
                RememberClosePosition = true
            };

            AgentMKDSupportJobShowHook ??= DService.Hook.HookFromAddress<AgentShowDelegate>(
                GetVFuncByName(AgentMKDSupportJob.Instance()->VirtualTable, "Show"), AgentMKDSupportJobShowDetour);
            AgentMKDSupportJobShowHook.Enable();
        }

        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("OthersManager");
            
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("OccultCrescentHelper-OthersManager-IslandID"));
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-OthersManager-IslandID-Help"), 20f * GlobalFontScale);

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-OthersManager-IslandIDInDtr")}", ref ModuleConfig.IsEnabledIslandIDDTR))
                    ModuleConfig.Save(MainModule);

                if (ImGui.Checkbox($"{GetLoc("OccultCrescentHelper-OthersManager-IslandIDInChat")}", ref ModuleConfig.IsEnabledIslandIDChat))
                    ModuleConfig.Save(MainModule);
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("OccultCrescentHelper-OthersManager-ModifyInfoHUD"));
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-OthersManager-ModifyInfoHUD-Help"), 20f * GlobalFontScale);

            ImGui.SameLine(0, 8f * GlobalFontScale);
            if (ImGui.Checkbox("###ModifyInfoHUD", ref ModuleConfig.IsEnabledModifyInfoHUD))
            {
                ModuleConfig.Save(MainModule);

                if (!ModuleConfig.IsEnabledModifyInfoHUD)
                    OnAddon(AddonEvent.PreFinalize, null);
            }

            if (ModuleConfig.IsEnabledModifyInfoHUD)
            {
                using (ImRaii.PushIndent())
                {
                    ImGui.TextColored(KnownColor.Orange.ToVector4(), GetLoc("OccultCrescentHelper-OthersManager-ModifySupportJobOrder"));
                    ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-OthersManager-ModifySupportJobOrder-Help", LuminaWrapper.GetAddonText(16658)),
                                       30f * GlobalFontScale);

                    ImGui.SameLine();
                    if (ImGui.SmallButton(GetLoc("Save")))
                        ModuleConfig.Save(MainModule);

                    ImGui.SameLine();
                    if (ImGui.SmallButton(GetLoc("Reset")))
                    {
                        ModuleConfig.AddonSupportJobOrder = ModuleConfig.AddonSupportJobOrder.Order().ToList();
                        ModuleConfig.Save(MainModule);
                    }

                    using (ImRaii.PushIndent())
                    {
                        var longestJobName = ModuleConfig.AddonSupportJobOrder.Select(LuminaWrapper.GetMKDSupportJobName).MaxBy(x => x.Length);

                        for (var i = 0; i < ModuleConfig.AddonSupportJobOrder.Count; i++)
                        {
                            var supportJob = ModuleConfig.AddonSupportJobOrder[i];
                            var name       = LuminaWrapper.GetMKDSupportJobName(supportJob);

                            ImGui.Button(name, new(ImGui.CalcTextSize(longestJobName).X * 2, ImGui.GetTextLineHeightWithSpacing()));
                            using (var source = ImRaii.DragDropSource())
                            {
                                if (source)
                                {
                                    if (ImGui.SetDragDropPayload("JobReorder", []))
                                        DragDropJobIndex = i;
                                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), name);
                                }
                            }

                            using (var target = ImRaii.DragDropTarget())
                            {
                                if (target)
                                {
                                    if (DragDropJobIndex                               >= 0 ||
                                        ImGui.AcceptDragDropPayload("JobReorder").Data != null)
                                    {
                                        (ModuleConfig.AddonSupportJobOrder[DragDropJobIndex], ModuleConfig.AddonSupportJobOrder[i]) =
                                            (ModuleConfig.AddonSupportJobOrder[i], ModuleConfig.AddonSupportJobOrder[DragDropJobIndex]);

                                        DragDropJobIndex = -1;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("OccultCrescentHelper-OthersManager-ModifyDefaultEnterZonePosition")} ({LuminaWrapper.GetAddonText(16586)})");
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-OthersManager-ModifyDefaultEnterZonePosition-Help"), 20f * GlobalFontScale);

            ImGui.SameLine(0, 8f * GlobalFontScale);
            if (ImGui.Checkbox("###ModifyDefaultEnterZonePositionSouthHorn", ref ModuleConfig.IsEnabledModifyDefaultPositionEnterZoneSouthHorn))
                ModuleConfig.Save(MainModule);

            if (ModuleConfig.IsEnabledModifyDefaultPositionEnterZoneSouthHorn)
            {
                using (ImRaii.PushIndent())
                {
                    ImGui.SetNextItemWidth(200f * GlobalFontScale);
                    ImGui.InputFloat3("###DefaultPositionEnterZoneSouthHornInput", ref ModuleConfig.DefaultPositionEnterZoneSouthHorn);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        ModuleConfig.Save(MainModule);

                    ImGui.SameLine();
                    if (ImGui.Button($"{GetLoc("Current")}##SetDefaultPositionEnterZoneSouthHorn"))
                    {
                        ModuleConfig.DefaultPositionEnterZoneSouthHorn = DService.ObjectTable.LocalPlayer?.Position ?? default;
                        ModuleConfig.Save(MainModule);
                    }

                    var isFirst = true;
                    foreach (var aetheryte in CrescentAetheryte.SouthHornAetherytes)
                    {
                        if (!isFirst)
                            ImGui.SameLine();
                        isFirst = false;

                        if (ImGui.Button($"{aetheryte.Name}##SetDefaultPositionEnterZoneSouthHorn"))
                        {
                            ModuleConfig.DefaultPositionEnterZoneSouthHorn = aetheryte.Position;
                            ModuleConfig.Save(MainModule);
                        }
                    }
                }
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("OccultCrescentHelper-OthersManager-AutoEnableDisablePlugins")}");
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-OthersManager-AutoEnableDisablePlugins-Help"), 20f * GlobalFontScale);

            ImGui.SameLine(0, 8f * GlobalFontScale);
            if (ImGui.Checkbox("###IsEnabledAutoEnableDisablePlugins", ref ModuleConfig.IsEnabledAutoEnableDisablePlugins))
                ModuleConfig.Save(MainModule);

            if (ModuleConfig.IsEnabledAutoEnableDisablePlugins)
            {
                using (ImRaii.PushIndent())
                {
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (20f * GlobalFontScale));
                    ImGui.InputText("###AutoEnableDisablePluginsInput", ref ModuleConfig.AutoEnableDisablePlugins, 1024);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        ModuleConfig.Save(MainModule);
                    ImGuiOm.TooltipHover(ModuleConfig.AutoEnableDisablePlugins);
                }
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("OccultCrescentHelper-OthersManager-HideDutyCommand"));
            ImGuiOm.HelpMarker(GetLoc("OccultCrescentHelper-OthersManager-HideDutyCommand-Help"), 20f * GlobalFontScale);

            ImGui.SameLine(0, 8f * GlobalFontScale);
            if (ImGui.Checkbox("###HideDutyCommand", ref ModuleConfig.IsEnabledHideDutyCommand))
                ModuleConfig.Save(MainModule);
        }

        public override void Uninit()
        {
            AgentMKDSupportJobShowHook?.Dispose();
            AgentMKDSupportJobShowHook = null;

            DService.AddonLifecycle.UnregisterListener(OnActionContents);

            DService.AddonLifecycle.UnregisterListener(OnLogin);

            DService.ClientState.TerritoryChanged -= OnZoneChanged;
            DService.AddonLifecycle.UnregisterListener(OnAddon);
            OnAddon(AddonEvent.PreFinalize, null);

            OthersTaskHelper?.Abort();
            OthersTaskHelper?.Dispose();
            OthersTaskHelper = null;

            Entry?.Remove();
            Entry = null;

            SupportJobChangeAddon?.Dispose();
            SupportJobChangeAddon = null;

            IsJustLogin = false;
        }

        private static void AgentMKDSupportJobShowDetour(AgentInterface* agent)
        {
            if (!ModuleConfig.IsEnabledModifyInfoHUD)
            {
                AgentMKDSupportJobShowHook.Original(agent);
                return;
            }

            if (agent->IsAgentActive())
                agent->Hide();

            SupportJobChangeAddon.Toggle();
        }

        private static void OnZoneChanged(ushort zone)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
            {
                IsJustLogin = false;

                Entry?.Remove();
                Entry = null;

                if (GameState.TerritoryType == 1278 && ModuleConfig.IsEnabledAutoEnableDisablePlugins)
                {
                    var pluginsNames = ModuleConfig.AutoEnableDisablePlugins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var plugin in pluginsNames)
                    {
                        if (string.IsNullOrWhiteSpace(plugin)) continue;
                        if (!IsPluginEnabled(plugin)) continue;

                        ChatManager.SendMessage($"/xldisableplugin {plugin}");
                    }
                }

                return;
            }

            var islandID = GetIslandID();
            if (ModuleConfig.IsEnabledIslandIDChat)
            {
                var message = new SeStringBuilder()
                              .AddText($"{GetLoc("OccultCrescentHelper-OthersManager-IslandID")}: ")
                              .AddUiForeground(45)
                              .AddText(islandID.ToString())
                              .AddUiForegroundOff()
                              .Build();
                Chat(message);
            }

            Entry       ??= DService.DtrBar.Get("DailyRoutines-OccultCrescentHelper-IslandID");
            Entry.Text  =   $"{GetLoc("OccultCrescentHelper-OthersManager-IslandID")}: {islandID}";
            Entry.Shown =   ModuleConfig.IsEnabledIslandIDDTR;

            if (!IsJustLogin                                                  &&
                ModuleConfig.IsEnabledModifyDefaultPositionEnterZoneSouthHorn &&
                BetweenAreas)
            {
                OthersTaskHelper.Abort();
                OthersTaskHelper.Enqueue(() =>
                {
                    if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return false;
                    if (localPlayer.IsDead) return true;

                    MovementManager.TPPlayerAddress(ModuleConfig.DefaultPositionEnterZoneSouthHorn);
                    return true;
                });
            }

            if (ModuleConfig.IsEnabledAutoEnableDisablePlugins)
            {
                var pluginsNames = ModuleConfig.AutoEnableDisablePlugins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var plugin in pluginsNames)
                {
                    if (string.IsNullOrWhiteSpace(plugin)) continue;
                    if (IsPluginEnabled(plugin)) continue;

                    ChatManager.SendMessage($"/xlenableplugin {plugin}");
                }
            }
        }

        private static void OnActionContents(AddonEvent type, AddonArgs args)
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;
            if (!Throttler.Throttle("OccultCrescentHelper-OthersManager-ActionDetail")) return;

            if (ActionContents == null) return;

            var resNode = ActionContents->GetNodeById(17);
            if (resNode == null) return;

            resNode->ToggleVisibility(!ModuleConfig.IsEnabledHideDutyCommand);
        }

        // 避免登入进来被重定向了
        private static void OnLogin(AddonEvent type, AddonArgs args) =>
            IsJustLogin = true;

        private void OnAddon(AddonEvent type, AddonArgs args)
        {
            switch (type)
            {
                case AddonEvent.PostDraw:
                    if (MKDInfo == null) return;

                    if (ModuleConfig.IsEnabledModifyInfoHUD && BuffButton == null)
                    {
                        BuffButton = new()
                        {
                            Position  = new(-1f, 32f),
                            Size      = new(48, 24),
                            IsVisible = true,
                            SeString  = new SeStringBuilder().AddIcon(BitmapFontIcon.ElementalLevel).Build().Encode(),
                            Tooltip   = GetLoc("OccultCrescentHelper-Command-PBuff-Help"),
                            OnClick   = () => ChatManager.SendMessage("/pdr pbuff")
                        };

                        BuffButton.AddColor      = new(0, 0.1254902f, 0.5019608f);
                        BuffButton.MultiplyColor = new(0.39215687f);

                        BuffButton.BackgroundNode.IsVisible = false;

                        BuffButton.AttachNode(MKDInfo->GetNodeById(20));

                        var jobButton = MKDInfo->GetComponentButtonById(34);
                        if (jobButton != null)
                            jobButton->OwnerNode->SetPositionFloat(0, 56);

                        var starButton = MKDInfo->GetImageNodeById(33);
                        if (starButton != null)
                            starButton->SetPositionFloat(0, 12);

                        var newJobNotifyButton = MKDInfo->GetImageNodeById(24);
                        if (newJobNotifyButton != null)
                        {
                            newJobNotifyButton->ToggleVisibility(false);
                            newJobNotifyButton->SetAlpha(0);
                        }
                    }

                    if (Throttler.Throttle("OccultCrescentHelper-OthersManager-RefreshBuffButton"))
                        BuffButton.Alpha = !DService.Condition[ConditionFlag.InCombat] && CrescentSupportJob.TryFindKnowledgeCrystal(out _) ? 1f : 0.6f;

                    if (ModuleConfig.IsEnabledModifyInfoHUD && SettingButton == null)
                    {
                        SettingButton = new()
                        {
                            Position  = new(41, 94),
                            Size      = new(40f, 32f),
                            IsVisible = true,
                            SeString  = new SeStringBuilder().AddIcon(BitmapFontIcon.ExclamationRectangle).Build().Encode(),
                            Tooltip   = MainModule.Info.Title,
                            OnClick   = () => MainModule.Overlay.IsOpen ^= true
                        };

                        SettingButton.AddColor                 = new(0, 0.1254902f, 0.5019608f);
                        SettingButton.MultiplyColor            = new(0.39215687f);
                        SettingButton.BackgroundNode.IsVisible = false;

                        SettingButton.AttachNode(MKDInfo->GetNodeById(20));
                    }

                    if (ModuleConfig.IsEnabledModifyInfoHUD && MapButton == null)
                    {
                        MapButton = new()
                        {
                            Position  = new(40, 60),
                            Size      = new(35f, 32f),
                            IsVisible = true,
                            IconId    = 60561,
                            Tooltip   = LuminaWrapper.GetAddonText(8441),
                            OnClick = () =>
                            {
                                var agent = AgentMap.Instance();
                                if (agent == null) return;

                                if (!agent->IsAgentActive())
                                    agent->OpenMap(GameState.Map, GameState.TerritoryType);
                                else
                                    agent->Hide();
                            }
                        };
                        MapButton.ImageNode.Scale          *= 1.2f;
                        MapButton.ImageNode.Position       -= new Vector2(10, 0);
                        MapButton.BackgroundNode.IsVisible =  false;

                        MapButton.AttachNode(MKDInfo->GetNodeById(20));
                    }

                    if (ModuleConfig.IsEnabledModifyInfoHUD && SupportJobChangeButton == null)
                    {
                        SupportJobChangeButton = new()
                        {
                            Position  = new(18, 4),
                            Size      = new(200, 32f),
                            IsVisible = true,
                            Tooltip   = LuminaWrapper.GetAddonText(16647),
                            OnClick   = () => SupportJobChangeAddon.Toggle()
                        };
                        SupportJobChangeButton.BackgroundNode.IsVisible = false;

                        SupportJobChangeButton.AttachNode(MKDInfo->GetNodeById(20));
                    }

                    if (Throttler.Throttle("OthersManager-OthersManager-IslandID-DTR"))
                    {
                        var islandID = GetIslandID();
                        Entry       ??= DService.DtrBar.Get("DailyRoutines-OccultCrescentHelper-IslandID");
                        Entry.Text  =   $"{GetLoc("OccultCrescentHelper-OthersManager-IslandID")}: {islandID}";
                        Entry.Shown =   ModuleConfig.IsEnabledIslandIDDTR;
                    }

                    break;
                case AddonEvent.PreFinalize:
                    BuffButton?.DetachNode();
                    BuffButton = null;

                    SettingButton?.DetachNode();
                    SettingButton = null;

                    MapButton?.DetachNode();
                    MapButton = null;

                    SupportJobChangeButton?.DetachNode();
                    SupportJobChangeButton = null;

                    Entry?.Remove();
                    Entry = null;
                    break;
            }
        }
        
        public class AddonDRMKDSupportJobChange : NativeAddon
        {
            private const float LerpSpeed = 0.2f;

            private bool IsFocused;

            public readonly Dictionary<uint, TextureButtonNode> SupportJobButtons = [];

            private List<SupportJobActionListNode> JobActionsContainer = [];

            private VerticalListNode JobContainer;

            private SimpleNineGridNode BackgroundNode;
            private SimpleNineGridNode BorderNode;

            private SimpleNineGridNode MoonPatternNode;
            private SimpleNineGridNode PatternLeftNode;
            private SimpleNineGridNode PatternRightNode;

            private TextureButtonNode CloseButtonNode;

            public bool PressedButtonOnce { get; set; }

            protected override void OnSetup(AtkUnitBase* addon)
            {
                PressedButtonOnce = false;

                RootNode.Size += new Vector2(200, 0);

                var windowNode = (WindowNode)WindowNode;
                
                windowNode.CloseButtonNode.IsVisible = false;
                windowNode.BackgroundNode.IsVisible  = false;
                windowNode.BorderNode.Alpha          = 0f;
                windowNode.TitleNode.IsVisible       = false;

                JobActionsContainer.Clear();

                CreateWindowStyle();

                CreateJobContainer();

                CreateWindowControll();
            }

            protected override void OnUpdate(AtkUnitBase* addon)
            {
                if (MKDInfo == null || DService.KeyState[VirtualKey.ESCAPE])
                {
                    Close();
                    return;
                }

                IsFocused = ((WindowNode)WindowNode).BorderNode.IsVisible;

                if (!Throttler.Throttle("OccultCrescentHelper-OthersManager-UpdateAddon", 10)) return;

                foreach (var node in JobActionsContainer)
                {
                    if (!node.IsVisible) continue;

                    if (node.BorderNode != null)
                    {
                        Vector3 targetColor = IsFocused ? new(0.19607843f) : new(-0.19607843f);
                        node.BorderNode.AddColor = Vector3.Lerp(node.BorderNode.AddColor, targetColor, LerpSpeed);
                    }

                    if (node.BackgroundNode != null)
                    {
                        var targetAlpha = IsFocused ? 0.9f : 0.6f;
                        node.BackgroundNode.Alpha = float.Lerp(node.BackgroundNode.Alpha / 255f, targetAlpha, LerpSpeed);
                    }
                }

                if (BorderNode != null)
                {
                    Vector3 targetColor = IsFocused ? new(0.19607843f) : new(-0.19607843f);
                    BorderNode.AddColor = Vector3.Lerp(BorderNode.AddColor, targetColor, LerpSpeed);
                }

                if (BackgroundNode != null)
                {
                    var targetAlpha = IsFocused ? 0.9f : 0.6f;
                    BackgroundNode.Alpha = float.Lerp(BackgroundNode.Alpha / 255f, targetAlpha, LerpSpeed);
                }

                if (MoonPatternNode != null)
                {
                    var targetAlpha = IsFocused ? 0.9f : 0.6f;
                    MoonPatternNode.Alpha = float.Lerp(MoonPatternNode.Alpha / 255f, targetAlpha, LerpSpeed);
                }

                if (PatternLeftNode != null)
                {
                    var targetAlpha = IsFocused ? 0.3f : 0.2f;
                    PatternLeftNode.Alpha = float.Lerp(PatternLeftNode.Alpha / 255f, targetAlpha, LerpSpeed);
                }

                if (PatternRightNode != null)
                {
                    var targetAlpha = IsFocused ? 0.3f : 0.2f;
                    PatternRightNode.Alpha = float.Lerp(PatternRightNode.Alpha / 255f, targetAlpha, LerpSpeed);
                }
            }

            private void CreateJobContainer()
            {
                const int   maxRowsPerPage = 4;
                const int   maxItemsPerRow = 5;
                const float rowHeight      = 53f;
                const float containerWidth = 500f;
                const float rowSpacing     = 30f;

                JobContainer = new VerticalListNode
                {
                    Position  = new(0, 0),
                    Size      = new(containerWidth, 478),
                    IsVisible = true,
                };

                JobContainer.AddDummy(65);

                var rows = new List<HorizontalFlexNode>();
                for (var i = 0; i < maxRowsPerPage; i++)
                {
                    var row = new HorizontalFlexNode
                    {
                        Position       = new(10, 0),
                        Size           = new(containerWidth, rowHeight),
                        IsVisible      = true,
                        AlignmentFlags = FlexFlags.CenterHorizontally | FlexFlags.FitContentHeight
                    };
                    rows.Add(row);
                }

                var counter = -1;
                foreach (var data in LuminaGetter.Get<MKDSupportJob>().OrderBy(x => ModuleConfig.AddonSupportJobOrder.IndexOf(x.RowId)))
                {
                    counter++;

                    var rowIndex = counter / maxItemsPerRow;
                    if (rowIndex >= maxRowsPerPage) continue;

                    var presetJob = CrescentSupportJob.AllJobs[(int)data.RowId];

                    // 预览用的
                    var jobActionContainer = new SupportJobActionListNode
                    {
                        Position = new(500, 0),
                        Size     = new(200, BackgroundNode.Height),
                    };
                    jobActionContainer.AttachNode(this);
                    JobActionsContainer.Add(jobActionContainer);

                    var unlockLink = string.Empty;
                    if (presetJob.UnlockType != CrescentSupportJobUnlockType.None)
                    {
                        unlockLink = $"{GetLoc("OccultCrescentHelper-OthersManager-SupportJobUnlockLink")}:\n" +
                                     $"{presetJob.UnlockLinkName}\n"                                           +
                                     $"[{presetJob.UnlockTypeName}]";
                    }

                    var iconButton = new TextureButtonNode
                    {
                        Size               = new(77f),
                        IsVisible          = true,
                        IsEnabled          = true,
                        TextureCoordinates = new((int)(data.RowId % 5) * 28, (int)(data.RowId / 5) * 28),
                        TexturePath        = "ui/uld/MKDSupportJobIcon_hr1.tex",
                        TextureSize        = new(28, 28),
                        OnClick = () =>
                        {
                            if (DService.Condition[ConditionFlag.InCombat] || presetJob.IsThisJob() || presetJob.CurrentLevel == 0) return;
                            presetJob.ChangeTo();
                            Close();
                        },
                        Tooltip = !string.IsNullOrEmpty(unlockLink) && presetJob.CurrentLevel == 0
                                      ? unlockLink
                                      : LuminaWrapper.GetMKDSupportJobDescription(presetJob.DataID),
                    };
                    SupportJobButtons[data.RowId] = iconButton;

                    if (presetJob.IsThisJob())
                        iconButton.AddColor = new(0.5882353f);
                    else
                    {
                        iconButton.AddTimeline(new TimelineBuilder()
                                               .BeginFrameSet(1, 59)
                                               .AddLabelPair(1,  9,  1)
                                               .AddLabelPair(10, 19, 2)
                                               .AddLabelPair(20, 29, 3)
                                               .AddLabelPair(30, 39, 7)
                                               .AddLabelPair(40, 49, 6)
                                               .AddLabelPair(50, 59, 4)
                                               .EndFrameSet()
                                               .Build());

                        iconButton.ImageNode.AddTimeline(new TimelineBuilder()
                                                         .AddFrameSetWithFrame(1, 9, 1,
                                                                               position: Vector2.Zero,
                                                                               alpha: 255,
                                                                               multiplyColor: new(100),
                                                                               scale: new(1f))
                                                         .BeginFrameSet(10, 19)
                                                         .AddFrame(10,
                                                                   position: Vector2.Zero,
                                                                   alpha: 255,
                                                                   multiplyColor: new(100),
                                                                   scale: new(1f))
                                                         .AddFrame(12,
                                                                   position: new(-1),
                                                                   alpha: 255,
                                                                   multiplyColor: new(100),
                                                                   addColor: new(50),
                                                                   scale: new(1.05f))
                                                         .EndFrameSet()
                                                         .AddFrameSetWithFrame(20, 29, 20,
                                                                               position: new(-1),
                                                                               alpha: 255,
                                                                               multiplyColor: new(100),
                                                                               addColor: new(50),
                                                                               scale: new(1.05f))
                                                         .AddFrameSetWithFrame(30, 39, 30,
                                                                               position: Vector2.Zero,
                                                                               alpha: 178,
                                                                               multiplyColor: new(50),
                                                                               scale: new(1f))
                                                         .AddFrameSetWithFrame(40, 49, 40,
                                                                               position: new(-1),
                                                                               alpha: 255,
                                                                               multiplyColor: new(100),
                                                                               addColor: new(50),
                                                                               scale: new(1.05f))
                                                         .BeginFrameSet(50, 59)
                                                         .AddFrame(50,
                                                                   position: new(-1),
                                                                   alpha: 255,
                                                                   multiplyColor: new(100),
                                                                   addColor: new(50),
                                                                   scale: new(1.05f))
                                                         .AddFrame(52,
                                                                   position: Vector2.Zero,
                                                                   alpha: 255,
                                                                   multiplyColor: new(100),
                                                                   scale: new(1f))
                                                         .EndFrameSet()
                                                         .AddFrameSetWithFrame(130, 139, 130,
                                                                               position: new(-1),
                                                                               alpha: 255,
                                                                               addColor: new(50),
                                                                               multiplyColor: new(100),
                                                                               scale: new(1.05f))
                                                         .AddFrameSetWithFrame(140, 149, 140,
                                                                               position: Vector2.Zero,
                                                                               alpha: 255,
                                                                               multiplyColor: new(100),
                                                                               scale: new(1f))
                                                         .AddFrameSetWithFrame(150, 159, 150,
                                                                               position: Vector2.Zero,
                                                                               alpha: 255,
                                                                               multiplyColor: new(100),
                                                                               scale: new(1f))
                                                         .Build());
                    }

                    iconButton.AddEvent(AtkEventType.MouseOver, () =>
                    {
                        if (PressedButtonOnce) return;

                        for (var index = 0; index < JobActionsContainer.Count; index++)
                        {
                            var node = JobActionsContainer[index];
                            node.IsVisible = index == (int)data.RowId;
                            if (node is { IsVisible: true, BackgroundNode: null })
                                node.LoadNodes(presetJob, IsFocused);
                        }

                        WindowNode.CollisionNode.Size = WindowNode.CollisionNode.Size with { X = 750 };
                        WindowNode.Size               = WindowNode.Size with { X = 750 };
                    });

                    iconButton.AddEvent(AtkEventType.ButtonPress, () =>
                    {
                        PressedButtonOnce = true;

                        for (var index = 0; index < JobActionsContainer.Count; index++)
                        {
                            var node = JobActionsContainer[index];
                            node.IsVisible = index == (int)data.RowId;
                            if (node is { IsVisible: true, BackgroundNode: null })
                                node.LoadNodes(presetJob, IsFocused);
                        }

                        WindowNode.CollisionNode.Size = WindowNode.CollisionNode.Size with { X = 750 };
                        WindowNode.Size               = WindowNode.Size with { X = 750 };
                    });

                    if (presetJob.CurrentLevel == 0)
                        iconButton.Alpha = 0.5f;

                    iconButton.ImageNode.Size = new(53);

                    var textNode = new TextNode
                    {
                        SeString      = new SeStringBuilder().AddUiGlow(32).Append($"{data.Name}").AddUiGlowOff().Build().Encode(),
                        FontSize      = 12,
                        IsVisible     = true,
                        Size          = new(53f, 24),
                        Position      = new(0, 50),
                        AlignmentType = AlignmentType.Center,
                        TextFlags     = TextFlags.Glare
                    };
                    textNode.AttachNode(iconButton);

                    var imageFullLevelNode = new SimpleNineGridNode
                    {
                        TextureCoordinates = new(64, 62),
                        TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                        TextureSize        = new(32, 20),
                        IsVisible          = presetJob.CurrentLevel == presetJob.MaxLevel,
                        Size               = new(32, 20),
                        Position           = new(10.5f, -15f),
                        AddColor           = presetJob.IsThisJob() ? new(-0.39215687f) : new()
                    };
                    imageFullLevelNode.AttachNode(iconButton);

                    var maxLevelText = presetJob.MaxLevel == 0 ? "∞" : $"{presetJob.MaxLevel}";
                    var currentLevelNode = new TextNode
                    {
                        SeString = new SeStringBuilder()
                                   .AddUiGlow(34)
                                   .Append($"{presetJob.CurrentLevel} / {maxLevelText}")
                                   .AddUiGlowOff()
                                   .Build()
                                   .Encode(),
                        FontSize      = 12,
                        IsVisible     = presetJob.CurrentLevel > 0 && presetJob.CurrentLevel != presetJob.MaxLevel,
                        Size          = new(53f, 24),
                        Position      = new(0, -17),
                        AlignmentType = AlignmentType.Center,
                        FontType      = FontType.JupiterLarge
                    };
                    currentLevelNode.AttachNode(iconButton);

                    rows[rowIndex].AddNode(iconButton);
                }

                for (var i = 0; i < rows.Count; i++)
                {
                    JobContainer.AddNode(rows[i]);
                    if (i < rows.Count - 1)
                        JobContainer.AddDummy(rowSpacing);
                }

                JobContainer.AttachNode(this);
            }

            private void CreateWindowStyle()
            {
                BackgroundNode = new SimpleNineGridNode
                {
                    TextureCoordinates = new(0),
                    TextureSize        = new(500, 490),
                    TexturePath        = "ui/uld/MKDWallPaper_hr1.tex",
                    IsVisible          = true,
                    Size               = new(502, 483),
                    Position           = new(-2),
                    Alpha              = 0.9f,
                };
                BackgroundNode.AttachNode(this);

                MoonPatternNode = new SimpleNineGridNode
                {
                    TextureCoordinates = new(0),
                    TextureSize        = new(190),
                    TexturePath        = "ui/uld/MKDWallMoon_hr1.tex",
                    IsVisible          = true,
                    Size               = new(190),
                    Position           = new(310, 183),
                    Alpha              = 0.9f,
                };
                MoonPatternNode.AttachNode(this);

                PatternLeftNode = new SimpleNineGridNode
                {
                    TextureCoordinates = new(349, 140),
                    TextureSize        = new(98, 132),
                    TexturePath        = "ui/uld/MKDWindowPattern_hr1.tex",
                    IsVisible          = true,
                    Size               = new(128, 132),
                    Position           = new(0, 40),
                    Alpha              = 0.3f,
                };
                PatternLeftNode.AttachNode(this);

                PatternRightNode = new SimpleNineGridNode
                {
                    TextureCoordinates = new(0, 45),
                    TextureSize        = new(176, 125),
                    TexturePath        = "ui/uld/MKDWindowPattern_hr1.tex",
                    IsVisible          = true,
                    Size               = new(236, 125),
                    Position           = new(260, 5),
                    Alpha              = 0.3f,
                };
                PatternRightNode.AttachNode(this);

                var anotherWindowTitleNode = new TextNode
                {
                    LineSpacing      = 23,
                    AlignmentType    = AlignmentType.Left,
                    FontSize         = 23,
                    FontType         = FontType.TrumpGothic,
                    NodeFlags        = NodeFlags.AnchorTop | NodeFlags.AnchorLeft | NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.EmitsEvents,
                    TextColor        = ColorHelper.GetColor(50),
                    TextOutlineColor = ColorHelper.GetColor(7),
                    Size             = new(86f, 31f),
                    Position         = new(12f, 7f),
                    IsVisible        = true,
                    SeString         = Title
                };
                anotherWindowTitleNode.AttachNode(this);

                BorderNode = new SimpleNineGridNode
                {
                    TextureCoordinates = new(1, 0),
                    TextureSize        = new(60, 70),
                    TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                    IsVisible          = true,
                    Size               = new(515f, 497f),
                    Position           = new(-8, -5),
                    Alpha              = 0.9f,
                    Offsets            = new(24),
                    AddColor           = new(0.19607843f)
                };
                BorderNode.AttachNode(this);
            }

            private void CreateWindowControll()
            {
                CloseButtonNode = new TextureButtonNode
                {
                    Size               = new(28.0f, 28.0f),
                    Position           = new(458.0f, 6.0f),
                    IsVisible          = true,
                    TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
                    TextureCoordinates = new(0.0f, 0.0f),
                    TextureSize        = new(28.0f, 28.0f),
                    OnClick            = Close
                };

                CloseButtonNode.ImageNode.AddColor = new(-0.19607843f);

                CloseButtonNode.AddTimeline(new TimelineBuilder()
                                            .BeginFrameSet(1, 20)
                                            .AddLabel(1,  1, AtkTimelineJumpBehavior.Start,    0)
                                            .AddLabel(10, 0, AtkTimelineJumpBehavior.PlayOnce, 1)
                                            .AddLabel(11, 2, AtkTimelineJumpBehavior.Start,    0)
                                            .AddLabel(20, 0, AtkTimelineJumpBehavior.PlayOnce, 2)
                                            .EndFrameSet()
                                            .Build());

                CloseButtonNode.ImageNode.AddTimeline(new TimelineBuilder()
                                                      .BeginFrameSet(1, 10)
                                                      .AddFrame(1, addColor: new(0))
                                                      .AddFrame(4, addColor: new(-50))
                                                      .EndFrameSet()
                                                      .BeginFrameSet(11, 20)
                                                      .AddFrame(11, addColor: new(0))
                                                      .AddFrame(14, addColor: new(50))
                                                      .EndFrameSet()
                                                      .Build());
                CloseButtonNode.AttachNode(this);
            }

            private class SupportJobActionListNode : SimpleComponentNode
            {
                public SimpleNineGridNode      BackgroundNode      { get; private set; }
                public SimpleNineGridNode      BorderNode          { get; private set; }
                public VerticalListNode        ActionListNode      { get; private set; }
                public TextureButtonNode       CloseButtonNode     { get; private set; }
                public TextureButtonNode       SettingButtonNode   { get; private set; }
                public CheckboxNode            IsRealActionNode    { get; private set; }
                public List<SupportActionNode> ActionDragDropNodes { get; private set; } = [];

                public void LoadNodes(CrescentSupportJob presetJob, bool isCurrentFoucused)
                {
                    BackgroundNode = new SimpleNineGridNode
                    {
                        TextureCoordinates = new(0),
                        TextureSize        = new(500, 380),
                        TexturePath        = "ui/uld/MKDWallPaper_hr1.tex",
                        IsVisible          = true,
                        Size               = this.Size + new Vector2(50, 0),
                        Position           = new(-2),
                        Alpha              = isCurrentFoucused ? 0.9f : 0.6f,
                    };
                    BackgroundNode.AttachNode(this);

                    BorderNode = new SimpleNineGridNode
                    {
                        TextureCoordinates = new(1, 0),
                        TextureSize        = new(60, 70),
                        TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                        IsVisible          = true,
                        Size               = this.Size + new Vector2(64, 14),
                        Position           = new(-8, -5),
                        Alpha              = 0.9f,
                        Offsets            = new(24),
                        AddColor           = isCurrentFoucused ? new(0.19607843f) : new(-0.19607843f)
                    };
                    BorderNode.AttachNode(this);

                    ActionListNode = new VerticalListNode
                    {
                        Size      = this.Size + new Vector2(50, 0),
                        IsVisible = true,
                        Position  = new(10)
                    };
                    ActionListNode.AttachNode(this);

                    ActionListNode.AddDummy(25f);

                    foreach (var (jobAction, jobLevel) in presetJob.Actions)
                    {
                        if (!LuminaGetter.TryGetRow<Action>(jobAction, out var action)) continue;

                        var row = new HorizontalListNode
                        {
                            IsVisible = true,
                            Size      = new(40f)
                        };

                        var dragDropNode = new SupportActionNode(presetJob, this, action.RowId, ActionDragDropNodes.Count, ModuleConfig.AddonIsDragRealAction)
                        {
                            Size         = new(40f),
                            IsVisible    = true,
                            IconId       = action.Icon,
                            AcceptedType = DragDropType.Nothing,
                            IsDraggable  = true,
                            IsClickable  = true,
                        };
                        ActionDragDropNodes.Add(dragDropNode);

                        row.AddNode(dragDropNode);
                        row.AddDummy(10);

                        var actionTextNode = new TextNode
                        {
                            SeString         = $"\ue06a {ToSENumberSmall(jobLevel)}: {action.Name.ExtractText()}",
                            FontSize         = 14,
                            IsVisible        = true,
                            Size             = new(Size.X - 20f, 40f),
                            AlignmentType    = AlignmentType.Left,
                            TextOutlineColor = ColorHelper.GetColor((uint)(presetJob.CurrentLevel >= jobLevel ? 32 : 4)),
                            TextFlags        = TextFlags.Glare
                        };
                        row.AddNode(actionTextNode);

                        while (actionTextNode.FontSize > 1 && actionTextNode.GetTextDrawSize(actionTextNode.SeString).X > actionTextNode.Size.X)
                            actionTextNode.FontSize--;

                        ActionListNode.AddNode(row);
                        ActionListNode.AddDummy(10f);
                    }

                    ActionListNode.AddDummy(20f);

                    foreach (var (trait, jobLevel) in presetJob.Traits)
                    {
                        if (!LuminaGetter.TryGetRow<MKDTrait>(trait, out var traitRow)) continue;

                        var row = new HorizontalListNode
                        {
                            IsVisible = true,
                            Size      = new(44f)
                        };

                        var dragDropNode = new DragDropNode
                        {
                            Size         = new(44f),
                            IsVisible    = true,
                            IconId       = (uint)traitRow.Unknown2,
                            AcceptedType = DragDropType.Nothing,
                            IsDraggable  = false,
                            Payload = new()
                            {
                                Type = DragDropType.ActionBar_Action,
                                Int2 = (int)trait,
                            },
                            IsClickable = false,
                            OnRollOver  = node => node.ShowTooltip(AtkTooltipManager.AtkTooltipType.Action, ActionKind.MKDTrait),
                            OnRollOut   = node => node.HideTooltip(),
                        };

                        row.AddNode(dragDropNode);
                        row.AddDummy(10);

                        var traitTextNode = new TextNode
                        {
                            SeString         = $"\ue06a {ToSENumberSmall(jobLevel)}: {traitRow.Unknown0.ExtractText()}",
                            FontSize         = 14,
                            IsVisible        = true,
                            Size             = new(Size.X - 20f, 44f),
                            AlignmentType    = AlignmentType.Left,
                            TextOutlineColor = ColorHelper.GetColor((uint)(presetJob.CurrentLevel >= jobLevel ? 32 : 4)),
                            TextFlags        = TextFlags.Glare
                        };
                        row.AddNode(traitTextNode);

                        while (traitTextNode.FontSize > 1 && traitTextNode.GetTextDrawSize(traitTextNode.SeString).X > traitTextNode.Size.X)
                            traitTextNode.FontSize--;

                        ActionListNode.AddNode(row);
                        ActionListNode.AddDummy(10f);
                    }

                    CloseButtonNode = new TextureButtonNode
                    {
                        Size               = new(28),
                        Position           = new(220, 6),
                        IsVisible          = true,
                        TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
                        TextureCoordinates = new(0),
                        TextureSize        = new(28),
                        OnClick = () =>
                        {
                            IsVisible                               = false;
                            SupportJobChangeAddon.PressedButtonOnce = false;

                            SupportJobChangeAddon.WindowNode.CollisionNode.Size = SupportJobChangeAddon.WindowNode.CollisionNode.Size with { X = 500 };
                            SupportJobChangeAddon.WindowNode.Size = SupportJobChangeAddon.WindowNode.Size with { X = 500 };
                        }
                    };

                    CloseButtonNode.ImageNode.AddColor = new();

                    CloseButtonNode.AddTimeline(new TimelineBuilder()
                                                .BeginFrameSet(1, 20)
                                                .AddLabel(1,  1, AtkTimelineJumpBehavior.Start,    0)
                                                .AddLabel(10, 0, AtkTimelineJumpBehavior.PlayOnce, 1)
                                                .AddLabel(11, 2, AtkTimelineJumpBehavior.Start,    0)
                                                .AddLabel(20, 0, AtkTimelineJumpBehavior.PlayOnce, 2)
                                                .EndFrameSet()
                                                .Build());

                    CloseButtonNode.ImageNode.AddTimeline(new TimelineBuilder()
                                                          .BeginFrameSet(1, 10)
                                                          .AddFrame(1, addColor: new(0))
                                                          .AddFrame(4, addColor: new(-50))
                                                          .EndFrameSet()
                                                          .BeginFrameSet(11, 20)
                                                          .AddFrame(11, addColor: new(0))
                                                          .AddFrame(14, addColor: new(50))
                                                          .EndFrameSet()
                                                          .Build());
                    CloseButtonNode.AttachNode(this);

                    SettingButtonNode = new TextureButtonNode
                    {
                        Size               = new(16),
                        Position           = new(202, 12f),
                        IsVisible          = true,
                        TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
                        TextureCoordinates = new(44, 0),
                        TextureSize        = new(16),
                        OnClick            = () => AgentModule.Instance()->GetAgentByInternalId(AgentId.MKDSettings)->Show()
                    };

                    SettingButtonNode.ImageNode.AddColor = new(-0.19607843f);

                    SettingButtonNode.AddTimeline(new TimelineBuilder()
                                                  .BeginFrameSet(1, 20)
                                                  .AddLabel(1,  1, AtkTimelineJumpBehavior.Start,    0)
                                                  .AddLabel(10, 0, AtkTimelineJumpBehavior.PlayOnce, 1)
                                                  .AddLabel(11, 2, AtkTimelineJumpBehavior.Start,    0)
                                                  .AddLabel(20, 0, AtkTimelineJumpBehavior.PlayOnce, 2)
                                                  .EndFrameSet()
                                                  .Build());

                    SettingButtonNode.ImageNode.AddTimeline(new TimelineBuilder()
                                                            .BeginFrameSet(1, 10)
                                                            .AddFrame(1, addColor: new(0))
                                                            .AddFrame(4, addColor: new(-50))
                                                            .EndFrameSet()
                                                            .BeginFrameSet(11, 20)
                                                            .AddFrame(11, addColor: new(0))
                                                            .AddFrame(14, addColor: new(150))
                                                            .EndFrameSet()
                                                            .Build());
                    SettingButtonNode.AttachNode(this);

                    IsRealActionNode = new()
                    {
                        IsVisible = true,
                        Position  = new(10, 10),
                        Size      = new(Width, 28),
                        SeString  = GetLoc("OccultCrescentHelper-OthersManager-DragRealActionIcon"),
                        Tooltip = new SeStringBuilder()
                                  .AddIcon(BitmapFontIcon.ExclamationRectangle)
                                  .AddText($" {GetLoc("OccultCrescentHelper-OthersManager-DragRealActionIcon-Help")}")
                                  .Build()
                                  .Encode(),
                        IsChecked = ModuleConfig.AddonIsDragRealAction,
                        IsEnabled = true,
                        OnClick = value =>
                        {
                            ModuleConfig.AddonIsDragRealAction = value;
                            ModuleConfig.Save(ModuleManager.GetModule<OccultCrescentHelper>());

                            ActionDragDropNodes.ForEach(x => x.Toggle(value));
                        }
                    };
                    IsRealActionNode.AttachNode(this);
                }

                public class SupportActionNode : DragDropNode
                {
                    public CrescentSupportJob       Job  { get; private set; }
                    public SupportJobActionListNode List { get; private set; }

                    public bool IsRealAction { get; private set; }
                    public int  ActionIndex  { get; private set; }
                    public uint ActionID     { get; private set; }

                    public bool IsDefault { get; private set; }
                    public bool IsHidden  { get; private set; }

                    public SimpleNineGridNode DefaultIconNode { get; private set; }
                    public SimpleNineGridNode HiddenIconNode  { get; private set; }

                    public static byte                 DefaultAction     { get; private set; }
                    public static ActionSlotHiddenFlag ActionHiddenFlags { get; private set; }
                    public static HashSet<byte>        HiddenActions     { get; private set; } = [];

                    public SupportActionNode(CrescentSupportJob job, SupportJobActionListNode list, uint actionID, int actionIndex, bool isRealAction = false)
                    {
                        Job  = job;
                        List = list;

                        IsRealAction = isRealAction;
                        ActionIndex  = actionIndex;
                        ActionID     = actionID;

                        DefaultIconNode = new SimpleNineGridNode
                        {
                            TexturePath        = "ui/uld/ContentsReplaySetting_hr1.tex",
                            TextureCoordinates = new(36, 44),
                            TextureSize        = new(36),
                            Size               = new(22),
                            Position           = new(22, 24)
                        };
                        DefaultIconNode.AttachNode(this);

                        HiddenIconNode = new SimpleNineGridNode
                        {
                            TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                            TextureCoordinates = new(64, 82),
                            TextureSize        = new(20),
                            Size               = new(22),
                            Position           = new(22, 24)
                        };
                        HiddenIconNode.AttachNode(this);

                        UpdateActionInfo();

                        Toggle(IsRealAction);
                    }

                    public void Toggle(bool isRealAction)
                    {
                        IsRealAction = isRealAction;

                        Payload = new()
                        {
                            Type = IsRealAction ? DragDropType.Action : DragDropType.GeneralAction,
                            Int2 = IsRealAction ? (int)ActionID : 31 + ActionIndex,
                        };

                        OnRollOver = node =>
                            node.ShowTooltip(AtkTooltipManager.AtkTooltipType.Action, IsRealAction ? ActionKind.Action : ActionKind.GeneralAction);
                        OnRollOut = node => node.HideTooltip();
                        OnClicked = _ =>
                        {
                            UpdateActionInfo();

                            // 当前是默认, 切换至隐藏
                            if (IsDefault)
                            {
                                // 不能全部技能都隐藏
                                if (HiddenActions.Count == Job.Actions.Count - 1)
                                    return;

                                // 找还有哪个其他技能能被设成默认
                                var actions = Job.Actions.Select(x => x.Key).ToList();
                                for (var i = 0; i < actions.Count; i++)
                                {
                                    if (i == ActionIndex || HiddenActions.Contains((byte)i)) continue;

                                    // 当前技能变成隐藏, 找到的技能变成新默认
                                    var newFlags = ActionHiddenFlags | IndexToHiddenFlag(ActionIndex);
                                    AgentMKDSupportJob.UpdateJobSettings(Job.DataID, (byte)i, (byte)newFlags);
                                    UpdateActionInfo();
                                    break;
                                }

                                return;
                            }

                            // 当前是隐藏, 变成非隐藏
                            if (IsHidden)
                            {
                                var newFlags = ActionHiddenFlags & ~IndexToHiddenFlag(ActionIndex);
                                AgentMKDSupportJob.UpdateJobSettings(Job.DataID, DefaultAction, (byte)newFlags);
                                UpdateActionInfo();

                                return;
                            }

                            // 当前啥都不是, 变成默认
                            AgentMKDSupportJob.UpdateJobSettings(Job.DataID, (byte)ActionIndex, (byte)ActionHiddenFlags);
                            UpdateActionInfo();
                        };
                    }

                    public void UpdateActionInfo(bool updateOthers = true)
                    {
                        var defaultAction     = stackalloc byte[1];
                        var actionHiddenFlags = stackalloc byte[1];
                        
                        AgentMKDSupportJob.GetJobSettings(Job.DataID, defaultAction, actionHiddenFlags);

                        DefaultAction     = *defaultAction;
                        ActionHiddenFlags = (ActionSlotHiddenFlag)(*actionHiddenFlags);

                        HiddenActions.Clear();
                        for (byte i = 0; i < 5; i++)
                        {
                            if (ActionHiddenFlags.HasFlag(IndexToHiddenFlag(i)))
                                HiddenActions.Add(i);
                        }

                        IsDefault = DefaultAction == (byte)ActionIndex;
                        IsHidden  = HiddenActions.Contains((byte)ActionIndex);

                        DefaultIconNode.IsVisible = IsDefault;
                        HiddenIconNode.IsVisible  = IsHidden;

                        if (updateOthers)
                        {
                            foreach (var node in List.ActionDragDropNodes)
                            {
                                if (node.ActionID == ActionID) continue;
                                node.UpdateActionInfo(false);
                            }
                        }
                    }

                    [Flags]
                    public enum ActionSlotHiddenFlag : byte
                    {
                        Action0 = 1 << 0,
                        Action1 = 1 << 1,
                        Action2 = 1 << 2,
                        Action3 = 1 << 3,
                        Action4 = 1 << 4,
                    }

                    public static ActionSlotHiddenFlag IndexToHiddenFlag(int index) => index switch
                    {
                        0 => ActionSlotHiddenFlag.Action0,
                        1 => ActionSlotHiddenFlag.Action1,
                        2 => ActionSlotHiddenFlag.Action2,
                        3 => ActionSlotHiddenFlag.Action3,
                        4 => ActionSlotHiddenFlag.Action4,
                        _ => ActionSlotHiddenFlag.Action0
                    };
                }
            }
        }
    }
}
