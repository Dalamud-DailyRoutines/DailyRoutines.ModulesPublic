using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Addon;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public class AutoAethericMimicry : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAethericMimicryTitle"),
        Description = GetLoc("AutoAethericMimicryDescription"),
        Category    = ModuleCategories.Action
    };

    private static readonly HashSet<uint> Status = [2124, 2125, 2126];
    
    private static AddonDRAutoAethericMimicry Addon;

    protected override void Init()
    {
        Addon ??= new()
        {
            InternalName     = "DRAutoAethericMimicry",
            Title            = string.Empty,
            Size             = new Vector2(180f, 50f),
            Position         = new Vector2(800f, 350f),
            NativeController = Service.AddonController,
        };

        UseActionManager.RegPreUseAction(OnPreUseAction);
    }

    protected override void Uninit()
    {
        UseActionManager.UnregPreUseAction(OnPreUseAction);
        
        Addon?.Dispose();
        Addon = null;
    }

    private static void OnPreUseAction(ref bool isPrevented, ref ActionType actionType, ref uint actionID, ref ulong targetID, ref uint extraParam, ref ActionManager.UseActionMode queueState, ref uint comboRouteID)
    {
        if (actionType != ActionType.Action || actionID != 18322) return;
        if (targetID   != 0xE0000000 && targetID        != LocalPlayerState.EntityID) return;
        if (Status.Any(x => LocalPlayerState.HasStatus(x, out _))) return;
        
        Addon.Toggle();
        isPrevented = true;
    }

    private class AddonDRAutoAethericMimicry : NativeAddon
    {
        private IconButtonNode TankButton;
        private IconButtonNode HealerButton;
        private IconButtonNode DPSButton;
        
        protected override unsafe void OnSetup(AtkUnitBase* addon)
        {
            WindowNode.CloseButtonNode.IsVisible = false;
            
            var rowOneContainer = new HorizontalFlexNode
            {
                Size      = new(160, 50),
                Position  = new(10, 19),
                IsVisible = true,
            };

            TankButton = new()
            {
                Size      = new(50f),
                IsVisible = true,
                IsEnabled = true,
                IconId    = 62581,
                OnClick = () =>
                {
                    if (TryGetChara([1], out var chara))
                        UseActionManager.UseActionLocation(ActionType.Action, 18322, chara.EntityId);
                    
                    Notify(chara);
                    Addon.Close();
                },
                Tooltip = $"{LuminaWrapper.GetActionName(18322)}: {LuminaWrapper.GetAddonText(1082)}",
            };
            rowOneContainer.AddNode(TankButton);
            
            HealerButton = new()
            {
                Size      = new(53f),
                IsVisible = true,
                IsEnabled = true,
                IconId    = 62582,
                OnClick = () =>
                {
                    if (TryGetChara([4], out var chara))
                        UseActionManager.UseActionLocation(ActionType.Action, 18322, chara.EntityId);
                    
                    Notify(chara);
                    Addon.Close();
                },
                Tooltip = $"{LuminaWrapper.GetActionName(18322)}: {LuminaWrapper.GetAddonText(1083)}",
            };
            rowOneContainer.AddNode(HealerButton);
            
            DPSButton = new()
            {
                Size      = new(53f),
                IsVisible = true,
                IsEnabled = true,
                IconId    = 62583,
                OnClick = () =>
                {
                    if (TryGetChara([2, 3], out var chara))
                        UseActionManager.UseActionLocation(ActionType.Action, 18322, chara.EntityId);
                    
                    Notify(chara);
                    Addon.Close();
                },
                Tooltip = $"{LuminaWrapper.GetActionName(18322)}: {LuminaWrapper.GetAddonText(1084)}",
            };
            rowOneContainer.AddNode(DPSButton);
            
            TankButton.IsEnabled   = TryGetChara([1],    out _);
            HealerButton.IsEnabled = TryGetChara([4],    out _);
            DPSButton.IsEnabled    = TryGetChara([2, 3], out _);
            
            AttachNode(rowOneContainer);
        }

        protected override unsafe void OnUpdate(AtkUnitBase* addon)
        {
            if (LocalPlayerState.ClassJob != 36)
            {
                Close();
                return;
            }
            
            if (!Throttler.Throttle("AutoAethericMimicry-OnUpdateButtons")) return;

            TankButton.IsEnabled   = TryGetChara([1],    out _);
            HealerButton.IsEnabled = TryGetChara([4],    out _);
            DPSButton.IsEnabled    = TryGetChara([2, 3], out _);
        }

        private static bool TryGetChara(HashSet<byte> roles, out IPlayerCharacter? chara)
        {
            chara = null;

            chara = DService.ObjectTable
                            .Where(x => x is IPlayerCharacter player                 &&
                                        player.EntityId != LocalPlayerState.EntityID &&
                                        roles.Contains(player.ClassJob.Value.Role))
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
                Chat(GetLoc("AutoAethericMimicry-NoAvailableTarget"));
                return;
            }

            Chat(GetSLoc("AutoAethericMimicry-MimicTarget", chara.ClassJob.Value.ToBitmapFontIcon(),
                         new PlayerPayload(chara.Name.ExtractText(), chara.HomeWorld.RowId)));
        }
    }
}
