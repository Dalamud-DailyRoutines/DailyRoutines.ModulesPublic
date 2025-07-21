using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DailyRoutines.ModulesPublic;

public unsafe class PetSizeContextMenu : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("PetSizeContextMenuTitle"),
        Description = GetLoc("PetSizeContextMenuDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static readonly UpperContainerItem ContainerItem = new();

    protected override void Init() => 
        DService.ContextMenu.OnMenuOpened += OnMenuOpened;

    private static void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!ContainerItem.IsDisplay(args)) return;
        args.AddMenuItem(ContainerItem.Get());
    }

    protected override void Uninit() => 
        DService.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private class UpperContainerItem : MenuItemBase
    {
        public override    string Name { get; protected set; } = GetLoc("PetSizeContextMenu-MenuName");
        protected override bool   IsSubmenu { get; set; } = true;
        
        protected override void OnClicked(IMenuItemClickedArgs args) 
            => args.OpenSubmenu(Name, ProcessMenuItems());

        private static List<MenuItem> ProcessMenuItems() =>
        [
            new()
            {
                Name      = $"{GetLoc("Adjust")}: {LuminaWrapper.GetAddonText(6371)}",
                OnClicked = _ => ChatHelper.SendMessage("/petsize all large")
            },
            new()
            {
                Name      = $"{GetLoc("Adjust")}: {LuminaWrapper.GetAddonText(6372)}",
                OnClicked = _ => ChatHelper.SendMessage("/petsize all medium")
            },
            new()
            {
                Name      = $"{GetLoc("Adjust")}: {LuminaWrapper.GetAddonText(6373)}",
                OnClicked = _ => ChatHelper.SendMessage("/petsize all small")
            }
        ];

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if (args.Target is not MenuTargetDefault defautTarget) return false;
            if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return false;

            var pet = CharacterManager.Instance()->LookupPetByOwnerObject(localPlayer.ToStruct());
            if (pet == null || defautTarget.TargetObjectId != pet->GetGameObjectId()) return false;
            
            return true;
        }
    }
}
