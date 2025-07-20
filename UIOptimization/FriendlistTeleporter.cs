using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DailyRoutines.Modules;

public class FriendlistTeleporter : DailyModuleBase
{
    private static readonly TeleportMenuItem TeleportItem = new();
    private static readonly CrossWorldMenuItem CrossWorldItem = new();

    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("FriendlistTeleporterTitle"),
        Description = GetLoc("FriendlistTeleporterDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Xww", "KirisameVanilla"],
        ModulesPrerequisite = ["WorldTravelCommand"]
    };

    public override void Init()
    {
        DService.ContextMenu.OnMenuOpened += OnMenuOpen;
    }

    public override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnMenuOpen;
        base.Uninit();
    }

    private static void OnMenuOpen(IMenuOpenedArgs args)
    {
        try
        {
            if (TeleportItem.IsDisplay(args))
                args.AddMenuItem(TeleportItem.Get());
        } catch { }
        try
        {
            if (ModuleManager.IsModuleEnabled("WorldTravelCommand") == true && CrossWorldItem.IsDisplay(args))
                args.AddMenuItem(CrossWorldItem.Get());
        } catch { }
        
    }

    private class TeleportMenuItem : MenuItemBase
    {
        private uint aetheryteID;

        public override string Name { get; protected set; } = GetLoc("FriendlistTeleporter-MenuItemTeleport");

        protected override unsafe void OnClicked(IMenuItemClickedArgs args) => Telepo.Instance()->Teleport(aetheryteID, 0);

        public override bool IsDisplay(IMenuOpenedArgs args) =>
            args is { AddonName : "FriendList", Target: MenuTargetDefault { TargetCharacter: not null } target } &&
            GetAetheryteId(target.TargetCharacter.Location.Value.RowId, out aetheryteID);

        private static bool GetAetheryteId(uint zoneID, out uint aetheryteID)
        {
            var localZoneID = (uint)DService.ClientState.TerritoryType;
            aetheryteID = 0;
            if (zoneID == 0 || zoneID == localZoneID) return false;
            zoneID = zoneID switch
            {
                128 => 129,
                133 => 132,
                131 => 130,
                399 => 478,
                _ => zoneID
            };
            if (zoneID == localZoneID) return false;
            aetheryteID = DService.AetheryteList
                                  .Where(aetheryte => aetheryte.TerritoryId == zoneID)
                                  .Select(aetheryte => aetheryte.AetheryteId)
                                  .FirstOrDefault();

            return aetheryteID > 0;
        }
    }

    private class CrossWorldMenuItem : MenuItemBase
    {
        private uint targetWorldID;
        public override string Name { get; protected set; } = GetLoc("FriendlistTeleporter-MenuItemCrossWorld");

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            try
            {
                var targetWorld = PresetSheet.Worlds[targetWorldID].Name.ExtractText();
                ChatHelper.SendMessage($"/pdr worldtravel {targetWorld}");
            }
            catch
            {
                // ignored
            }
        }
        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if (args.AddonName != "FriendList") return false;
            try
            {
                if (args.Target is MenuTargetDefault { TargetCharacter.CurrentWorld.RowId: var _targetWorldID } &&
                    _targetWorldID !=DService.ObjectTable.LocalPlayer.CurrentWorld.RowId)
                {
                    targetWorldID = _targetWorldID;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
