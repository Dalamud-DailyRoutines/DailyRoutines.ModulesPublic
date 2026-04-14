using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class AutoAethericMimicry : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoAethericMimicryTitle"),
        Description = Lang.Get("AutoAethericMimicryDescription"),
        Category    = ModuleCategory.Action
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    protected override void Init() =>
        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);

    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPreUseAction);

        AddonDRAutoAethericMimicry.Addon?.Dispose();
        AddonDRAutoAethericMimicry.Addon = null;
    }

    private static void OnPreUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        if (actionType != ActionType.Action || actionID != 18322) return;
        if (targetID   != 0xE0000000 && targetID        != LocalPlayerState.EntityID) return;
        if (Status.Any(x => LocalPlayerState.HasStatus(x, out _))) return;

        AddonDRAutoAethericMimicry.OpenWithNewInstance();
        isPrevented = true;
    }

    private class AddonDRAutoAethericMimicry : NativeAddon
    {
        public static AddonDRAutoAethericMimicry? Addon;
        
        private IconButtonNode dpsButton;
        private IconButtonNode healerButton;

        private IconButtonNode tankButton;

        public static void OpenWithNewInstance()
        {
            Addon?.Dispose();

            Addon = new()
            {
                InternalName = "DRAutoAethericMimicry",
                Title        = string.Empty,
                Size         = new(180f, 50f)
            };
            Addon.SetWindowPosition(ImGui.GetMousePos() - Addon.Size with { X = Addon.Size.X / 1.5f });

            Addon.Open();
        }

        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            ((WindowNode)WindowNode).CloseButtonNode.IsVisible = false;

            var rowOneContainer = new HorizontalFlexNode
            {
                Size      = new(160, 50),
                Position  = new(10, 19),
                IsVisible = true
            };

            tankButton = new()
            {
                Size      = new(50f),
                IsVisible = true,
                IsEnabled = true,
                IconId    = 62581,
                OnClick = () =>
                {
                    if (TryGetChara([1], out var chara))
                        UseActionManager.Instance().UseActionLocation(ActionType.Action, 18322, chara.EntityID);

                    Notify(chara);
                    Addon.Close();
                },
                TextTooltip = $"{LuminaWrapper.GetActionName(18322)}: {LuminaWrapper.GetAddonText(1082)}"
            };
            rowOneContainer.AddNode(tankButton);

            healerButton = new()
            {
                Size      = new(53f),
                IsVisible = true,
                IsEnabled = true,
                IconId    = 62582,
                OnClick = () =>
                {
                    if (TryGetChara([4], out var chara))
                        UseActionManager.Instance().UseActionLocation(ActionType.Action, 18322, chara.EntityID);

                    Notify(chara);
                    Addon.Close();
                },
                TextTooltip = $"{LuminaWrapper.GetActionName(18322)}: {LuminaWrapper.GetAddonText(1083)}"
            };
            rowOneContainer.AddNode(healerButton);

            dpsButton = new()
            {
                Size      = new(53f),
                IsVisible = true,
                IsEnabled = true,
                IconId    = 62583,
                OnClick = () =>
                {
                    if (TryGetChara([2, 3], out var chara))
                        UseActionManager.Instance().UseActionLocation(ActionType.Action, 18322, chara.EntityID);

                    Notify(chara);
                    Addon.Close();
                },
                TextTooltip = $"{LuminaWrapper.GetActionName(18322)}: {LuminaWrapper.GetAddonText(1084)}"
            };
            rowOneContainer.AddNode(dpsButton);

            tankButton.IsEnabled   = TryGetChara([1],    out _);
            healerButton.IsEnabled = TryGetChara([4],    out _);
            dpsButton.IsEnabled    = TryGetChara([2, 3], out _);

            rowOneContainer.AttachNode(this);
        }

        protected override unsafe void OnUpdate(AtkUnitBase* addon)
        {
            if (LocalPlayerState.ClassJob != 36 || DService.Instance().KeyState[VirtualKey.ESCAPE])
            {
                Close();

                if (SystemMenu != null)
                    SystemMenu->Close(true);
                return;
            }

            if (!Throttler.Shared.Throttle("AutoAethericMimicry-OnUpdateButtons")) return;

            tankButton.IsEnabled   = TryGetChara([1],    out _);
            healerButton.IsEnabled = TryGetChara([4],    out _);
            dpsButton.IsEnabled    = TryGetChara([2, 3], out _);
        }

        private static bool TryGetChara(HashSet<byte> roles, out IPlayerCharacter? chara)
        {
            chara = null;

            chara = DService.Instance().ObjectTable
                            .Where
                            (x => x is IPlayerCharacter player                 &&
                                  player.EntityID != LocalPlayerState.EntityID &&
                                  roles.Contains(player.ClassJob.Value.Role)
                            )
                            .Where(x => x is { YalmDistanceX: <= 25, YalmDistanceZ: <= 25 })
                            .OrderBy(x => x.YalmDistanceX + x.YalmDistanceZ)
                            .OfType<IPlayerCharacter>()
                            .FirstOrDefault();
            return chara != null;
        }

        private static void Notify(IPlayerCharacter? chara)
        {
            if (chara == null)
            {
                NotifyHelper.Instance().Chat(Lang.Get("AutoAethericMimicry-NoAvailableTarget"));
                return;
            }

            NotifyHelper.Instance().Chat
            (
                Lang.GetSe
                (
                    "AutoAethericMimicry-MimicTarget",
                    chara.ClassJob.Value.ToBitmapFontIcon(),
                    new PlayerPayload(chara.Name.ToString(), chara.HomeWorld.RowId)
                )
            );
        }
    }
    
    #region 常量
    
    private static readonly FrozenSet<uint> Status = [2124, 2125, 2126];
    
    #endregion
}
