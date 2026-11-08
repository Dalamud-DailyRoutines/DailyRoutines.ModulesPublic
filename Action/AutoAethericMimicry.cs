using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

internal class AutoAethericMimicry : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoAethericMimicryTitle"),
        Description = Lang.Get("AutoAethericMimicryDescription"),
        Category    = ModuleCategory.Action
    };

    public override ModuleBehaviour Behaviour { get; } = new() { AllDefaultEnabled = true };

    public override string ModuleName => nameof(AutoAethericMimicry);

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
        if (actionType != ActionType.Action ||
            actionID   != AethericMimicryActionID)
            return;
        if (targetID != 0xE0000000 &&
            targetID != LocalPlayerState.EntityID)
            return;
        if (Status.Any(x => LocalPlayerState.HasStatus(x, out _)))
            return;

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
                InternalName     = "DRAutoAethericMimicry",
                Title            = Lang.Get("SelectTarget"),
                Size             = new(230f, 120f),
                CreateWindowNode = () => new WindowNode(),
            };

            Addon.SetWindowPosition(ImGui.GetMousePos() - Addon.Size with { X = Addon.Size.X / 1.5f });

            Addon.Open();
        }

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var rowOneContainer = new HorizontalListNode
            {
                Size        = new(ContentSize.X + 10f, 64f),
                Position    = ContentStartPosition,
                ItemSpacing = 16f
            };

            tankButton = CreateButton(62581, [1], 1082);
            rowOneContainer.AddNode(tankButton);

            healerButton = CreateButton(62582, [4], 1083);
            rowOneContainer.AddNode(healerButton);

            dpsButton = CreateButton(62583, [2, 3], 1084);
            rowOneContainer.AddNode(dpsButton);

            tankButton.IsEnabled   = TryGetChara([1],    out _);
            healerButton.IsEnabled = TryGetChara([4],    out _);
            dpsButton.IsEnabled    = TryGetChara([2, 3], out _);

            rowOneContainer.AttachNode(this);
            return;

            IconButtonNode CreateButton(uint iconID, byte[] roles, uint addonTextID)
            {
                var button = new IconButtonNode
                {
                    Size   = new(58f),
                    IconId = iconID,
                    OnClick = () =>
                    {
                        if (TryGetChara(roles, out var chara))
                            UseActionManager.Instance().UseActionLocation(ActionType.Action, AethericMimicryActionID, chara.EntityID);

                        Notify(chara);
                        Addon.Close();
                    },
                    TextTooltip = $"{LuminaWrapper.GetActionName(AethericMimicryActionID)}：{LuminaWrapper.GetAddonText(addonTextID)}"
                };

                using var rented  = new RentedSeStringBuilder();
                var       builder = rented.Builder;

                builder.AppendIcon((uint)BitmapFontIcon.BlueMage);

                var iconText = new TextNode
                {
                    String   = builder.ToReadOnlySeString(),
                    Size     = new(16),
                    Position = new(0, button.Height - 16f)
                };
                iconText.AttachNode(button);

                return button;
            }
        }

        protected override unsafe void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (LocalPlayerState.ClassJob != 36 || IKeyState.Instance()[VirtualKey.ESCAPE])
            {
                Close();

                if (SystemMenu != null)
                    SystemMenu->Close(true);
                return;
            }

            if (!Throttler.Shared.Throttle("AutoAethericMimicry.OnUpdateButtons")) return;

            tankButton.IsEnabled   = TryGetChara([1],    out _);
            healerButton.IsEnabled = TryGetChara([4],    out _);
            dpsButton.IsEnabled    = TryGetChara([2, 3], out _);
        }

        private static bool TryGetChara
        (
            byte[]                roles,
            out IPlayerCharacter? chara
        )
        {
            chara = null;

            chara = IObjectTable.Instance()
                                .Where
                                (x => x is IPlayerCharacter player                 &&
                                      player.EntityID != LocalPlayerState.EntityID &&
                                      roles.Contains(player.ClassJob.Value.Role)
                                )
                                .Where(x => x is { Distance: <= 25 })
                                .OrderBy(x => x.Distance)
                                .OfType<IPlayerCharacter>()
                                .FirstOrDefault();
            return chara != null;
        }

        private static unsafe void Notify
        (
            IPlayerCharacter? chara
        )
        {
            if (chara == null)
            {
                // 无法指定目标。
                RaptureLogModule.Instance()->ShowLogMessage(563);
                return;
            }

            using var rented  = new RentedSeStringBuilder();
            var       builder = rented.Builder;

            builder.AppendIcon((uint)chara.ClassJob.Value.ToBitmapFontIcon())
                   .Append(chara.Name);

            if (chara.HomeWorld.RowId != GameState.HomeWorld)
            {
                builder.AppendIcon((uint)BitmapFontIcon.CrossWorld)
                       .Append(chara.HomeWorld.Value.Name);
            }

            NotifyHelper.Toast
            (
                Lang.GetSe
                (
                    "AutoAethericMimicry-Notification-MimickedTarget",
                    builder.ToReadOnlySeString()
                )
            );
        }
    }

    #region 常量

    private static readonly uint[] Status = [2124, 2125, 2126];

    private const uint AethericMimicryActionID = 18322;

    #endregion
}
