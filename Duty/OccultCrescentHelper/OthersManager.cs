using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;
using OmenTools.Info.Game;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic.Duty;

public partial class OccultCrescentHelper
{
    private unsafe class OthersManager
    (
        OccultCrescentHelper mainModule
    ) : BaseIslandModule(mainModule)
    {
        private TextButtonNode? settingButton;
        private IconButtonNode? mapButton;

        private OverlayController? overlayController;

        private TaskHelper? othersTaskHelper;

        private bool isJustLogin;

        public override void Init()
        {
            othersTaskHelper ??= new() { TimeoutMS = 30_000 };

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "MKDInfo", OnAddon);
            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MKDInfo", OnAddon);

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_CharaSelectListMenu", OnLogin);

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_ActionContents", OnActionContents);

            DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
            OnZoneChanged(0);
        }

        public override void Uninit()
        {
            DService.Instance().AddonLifecycle.UnregisterListener(OnActionContents);

            DService.Instance().AddonLifecycle.UnregisterListener(OnLogin);

            DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
            DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

            settingButton?.Dispose();
            settingButton = null;

            mapButton?.Dispose();
            mapButton = null;

            othersTaskHelper?.Abort();
            othersTaskHelper?.Dispose();
            othersTaskHelper = null;

            overlayController?.Dispose();
            overlayController = null;

            isJustLogin = false;
        }

        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("OthersManager");

            if (ImGui.Checkbox
                (
                    Lang.Get("OccultCrescentHelper-OthersManager-ModifyInfoHUD"),
                    ref MainModule.config.IsEnabledModifyInfoHUD
                ))
            {
                MainModule.config.Save(MainModule);

                if (!MainModule.config.IsEnabledModifyInfoHUD)
                {
                    settingButton?.Dispose();
                    settingButton = null;

                    mapButton?.Dispose();
                    mapButton = null;
                }
            }

            ImGui.NewLine();

            ImGui.TextColored
            (
                KnownColor.LightSkyBlue.ToVector4(),
                Lang.Get("OccultCrescentHelper-OthersManager-ModifyDefaultEnterZonePosition")
            );

            using (ImRaii.PushIndent())
            using (ImRaii.ItemWidth(250f * GlobalUIScale))
            {
                ImGui.TextUnformatted(LuminaWrapper.GetZonePlaceName(1252));

                ImGui.InputFloat3("###DefaultPositionEnterZoneSouthHornInput", ref MainModule.config.DefaultPositionEnterZoneSouthHorn);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    MainModule.config.Save(MainModule);

                ImGui.SameLine();

                if (ImGui.Button($"{Lang.Get("Current")}##SetDefaultPositionEnterZoneSouthHorn"))
                {
                    MainModule.config.DefaultPositionEnterZoneSouthHorn = DService.Instance().ObjectTable.LocalPlayer?.Position ?? default;
                    MainModule.config.Save(MainModule);
                }

                var isFirst = true;

                foreach (var aetheryte in CrescentAetheryte.SouthHornAetherytes)
                {
                    if (!isFirst)
                        ImGui.SameLine();
                    isFirst = false;

                    if (ImGui.Button($"{aetheryte.Name}##SetDefaultPositionEnterZoneSouthHorn"))
                    {
                        MainModule.config.DefaultPositionEnterZoneSouthHorn = aetheryte.Position;
                        MainModule.config.Save(MainModule);
                    }
                }

                ImGui.Spacing();

                ImGui.TextUnformatted(LuminaWrapper.GetZonePlaceName(1346));

                ImGui.InputFloat3("###DefaultPositionEnterZoneNorthHornInput", ref MainModule.config.DefaultPositionEnterZoneNorthHorn);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    MainModule.config.Save(MainModule);

                ImGui.SameLine();

                if (ImGui.Button($"{Lang.Get("Current")}##SetDefaultPositionEnterZoneNorthHorn"))
                {
                    MainModule.config.DefaultPositionEnterZoneNorthHorn = DService.Instance().ObjectTable.LocalPlayer?.Position ?? default;
                    MainModule.config.Save(MainModule);
                }

                for (var i = 0; i < CrescentAetheryte.NorthHornAetherytes.Count; i++)
                {
                    if (i != 0)
                        ImGui.SameLine();

                    var aetheryte = CrescentAetheryte.NorthHornAetherytes[i];

                    if (ImGui.Button($"{aetheryte.Name}##SetDefaultPositionEnterZoneNorthHorn"))
                    {
                        MainModule.config.DefaultPositionEnterZoneNorthHorn = aetheryte.Position;
                        MainModule.config.Save(MainModule);
                    }
                }
            }

            ImGui.NewLine();

            if (ImGui.Checkbox
                (
                    Lang.Get("OccultCrescentHelper-OthersManager-AutoEnableDisablePlugins"),
                    ref MainModule.config.IsEnabledAutoEnableDisablePlugins
                ))
                MainModule.config.Save(MainModule);
            ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-OthersManager-AutoEnableDisablePlugins-Help"), 20f * GlobalUIScale);

            if (MainModule.config.IsEnabledAutoEnableDisablePlugins)
            {
                using var indent = ImRaii.PushIndent();

                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (20f * GlobalUIScale));
                ImGui.InputText("###AutoEnableDisablePluginsInput", ref MainModule.config.AutoEnableDisablePlugins, 1024);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    MainModule.config.Save(MainModule);
                ImGuiOm.TooltipHover(MainModule.config.AutoEnableDisablePlugins);

            }

            ImGui.NewLine();

            if (ImGui.Checkbox
                (
                    Lang.Get("OccultCrescentHelper-OthersManager-HideDutyCommand"),
                    ref MainModule.config.IsEnabledHideDutyCommand
                ))
                MainModule.config.Save(MainModule);
            ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-OthersManager-HideDutyCommand-Help"), 20f * GlobalUIScale);

            ImGui.NewLine();

            if (ImGui.Checkbox
                (
                    Lang.Get("OccultCrescentHelper-OthersManager-FastUseKnowledgeCrystal"),
                    ref MainModule.config.IsEnabledKnowledgeCrystalFastUse
                ))
                MainModule.config.Save(MainModule);
            ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-OthersManager-FastUseKnowledgeCrystal-Help"), 20f * GlobalUIScale);
        }

        private void OnZoneChanged
        (
            uint u
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
            {
                overlayController?.Dispose();
                overlayController = null;

                isJustLogin = false;

                if (GameState.TerritoryType == 1278 && MainModule.config.IsEnabledAutoEnableDisablePlugins)
                {
                    var pluginsNames = MainModule.config.AutoEnableDisablePlugins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    foreach (var plugin in pluginsNames)
                    {
                        if (string.IsNullOrWhiteSpace(plugin)) continue;
                        if (!DService.Instance().PI.IsPluginEnabled(plugin)) continue;

                        ChatManager.Instance().SendMessage($"/xldisableplugin {plugin}");
                    }
                }

                return;
            }

            overlayController ??= new();
            overlayController.AddNode(new LongTimeBuffButton(this));

            if (!isJustLogin                         &&
                ICondition.Instance().IsBetweenAreas &&
                GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
            {
                var destination = GameState.TerritoryType == 1346 ?
                                      MainModule.config.DefaultPositionEnterZoneNorthHorn :
                                      MainModule.config.DefaultPositionEnterZoneSouthHorn;

                othersTaskHelper.Abort();
                othersTaskHelper.Enqueue
                (() =>
                    {
                        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;
                        if (localPlayer.IsDead) return true;

                        MovementManager.Instance().TPPlayerAddress(destination);
                        return true;
                    }
                );
            }

            if (MainModule.config.IsEnabledAutoEnableDisablePlugins)
            {
                var pluginsNames = MainModule.config.AutoEnableDisablePlugins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                foreach (var plugin in pluginsNames)
                {
                    if (string.IsNullOrWhiteSpace(plugin)) continue;
                    if (DService.Instance().PI.IsPluginEnabled(plugin)) continue;

                    ChatManager.Instance().SendMessage($"/xlenableplugin {plugin}");
                }
            }
        }

        private void OnActionContents
        (
            AddonEvent type,
            AddonArgs  args
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;
            if (!Throttler.Shared.Throttle("OccultCrescentHelper-OthersManager-ActionDetail")) return;

            if (ActionContents == null) return;

            var resNode = ActionContents->GetNodeById(17);
            if (resNode == null) return;

            resNode->ToggleVisibility(!MainModule.config.IsEnabledHideDutyCommand);
        }

        // 避免登入进来被重定向了
        private void OnLogin
        (
            AddonEvent type,
            AddonArgs  args
        ) =>
            isJustLogin = true;

        private void OnAddon
        (
            AddonEvent type,
            AddonArgs  args
        )
        {
            switch (type)
            {
                case AddonEvent.PostDraw:
                    if (MKDInfo == null) return;

                    if (MainModule.config.IsEnabledModifyInfoHUD && settingButton == null)
                    {
                        var newJobNotifyButton = MKDInfo->GetImageNodeById(24);

                        if (newJobNotifyButton != null)
                        {
                            newJobNotifyButton->ToggleVisibility(false);
                            newJobNotifyButton->SetAlpha(0);
                        }

                        settingButton = new()
                        {
                            Position    = new(58, 160),
                            Size        = new(40f, 32f),
                            IsVisible   = true,
                            String      = new SeStringBuilder().AddIcon(BitmapFontIcon.ExclamationRectangle).Build().Encode(),
                            TextTooltip = MainModule.Info.Title,
                            OnClick     = () => MainModule.Overlay.IsOpen ^= true
                        };

                        settingButton.AddColor                 = new(0, 0.1254902f, 0.5019608f);
                        settingButton.MultiplyColor            = new(0.39215687f);
                        settingButton.BackgroundNode.IsVisible = false;

                        settingButton.AttachNode(MKDInfo->GetNodeById(67));
                    }

                    if (MainModule.config.IsEnabledModifyInfoHUD && mapButton == null)
                    {
                        mapButton = new()
                        {
                            Position    = new(58, 128),
                            Size        = new(35f, 32f),
                            IsVisible   = true,
                            IconId      = 60561,
                            TextTooltip = LuminaWrapper.GetAddonText(8441),
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
                        mapButton.ImageNode.Scale          *= 1.2f;
                        mapButton.ImageNode.Position       -= new Vector2(10, 0);
                        mapButton.BackgroundNode.IsVisible =  false;

                        mapButton.AttachNode(MKDInfo->GetNodeById(67));
                    }

                    break;
                case AddonEvent.PreFinalize:
                    settingButton = null;
                    mapButton     = null;
                    break;
            }
        }

        private class LongTimeBuffButton : OverlayNode
        {
            private readonly OthersManager  manager;
            private readonly TextButtonNode buttonNode;

            private bool    isAnyNearby;
            private Vector3 nearbyPosition;

            public LongTimeBuffButton
            (
                OthersManager manager
            )
            {
                this.manager = manager;

                buttonNode = new()
                {
                    Size        = new(48, 24),
                    String      = new SeStringBuilder().AddIcon(BitmapFontIcon.ElementalLevel).Build().Encode(),
                    TextTooltip = Lang.Get("OccultCrescentHelper-Command-PBuff-Help"),
                    OnClick     = () => ChatManager.Instance().SendMessage("/pdr pbuff"),
                    Scale       = new(3)
                };
                buttonNode.AttachNode(this);
            }

            public override OverlayLayer OverlayLayer     => OverlayLayer.BehindUserInterface;
            public override bool         HideWithNativeUi => true;

            protected override void OnUpdate()
            {
                if (manager.MainModule.config.IsEnabledKnowledgeCrystalFastUse            &&
                    GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent &&
                    !DService.Instance().Condition.IsOccupiedInEvent)
                {
                    if (Throttler.Shared.Throttle("OccultCrescentHelper-OthersManager-LongTimeBuffButton"))
                    {
                        isAnyNearby = CrescentSupportJob.TryFindKnowledgeCrystal(out var gameObject);
                        nearbyPosition = isAnyNearby ?
                                             gameObject.Position :
                                             Vector3.Zero;
                    }

                    if (nearbyPosition != Vector3.Zero                                                  &&
                        GameViewHelper.WorldToScreen(nearbyPosition, out var screenPos, out var inView) &&
                        inView)
                    {
                        buttonNode.IsEnabled = LocalPlayerState.DistanceTo2DSquared(nearbyPosition.ToVector2()) <= 10;

                        buttonNode.IsVisible = true;
                        buttonNode.Position  = screenPos - (buttonNode.Node->GetNodeState().Size / 2);

                        IsVisible = true;
                    }
                    else
                    {
                        buttonNode.IsVisible = false;
                        IsVisible            = false;
                    }
                }
                else
                {
                    buttonNode.IsVisible = false;
                    IsVisible            = false;
                }
            }
        }
    }
}
