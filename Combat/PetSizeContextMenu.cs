using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace DailyRoutines.Modules;

public unsafe class PetSizeContextMenu : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("PetSizeContextMenuTitle"),
        Description = GetLoc("PetSizeContextMenuDescription"),
        Category    = ModuleCategories.Combat,
    };

    private static UpperContainerItem ContainerItem = new();

    public override void Init()
    {
        DService.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!ContainerItem.IsDisplay(args)) return;
        args.AddMenuItem(ContainerItem.Get());
    }
    
    public override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnMenuOpened;
    }

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
                Name      = $"{GetLoc("Adjust")}: {LuminaCache.GetRow<Addon>(6371)!.Value.Text.ExtractText()}",
                OnClicked = _ => ChatHelper.Instance.SendMessage("/petsize all large")
            },
            new()
            {
                Name      = $"{GetLoc("Adjust")}: {LuminaCache.GetRow<Addon>(6372)!.Value.Text.ExtractText()}",
                OnClicked = _ => ChatHelper.Instance.SendMessage("/petsize all medium")
            },
            new()
            {
                Name      = $"{GetLoc("Adjust")}: {LuminaCache.GetRow<Addon>(6373)!.Value.Text.ExtractText()}",
                OnClicked = _ => ChatHelper.Instance.SendMessage("/petsize all small")
            }
        ];

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if (args.Target is not MenuTargetDefault defautTarget) return false;
            if (DService.ClientState.LocalPlayer is not { } localPlayer) return false;

            var pet = CharacterManager.Instance()->LookupPetByOwnerObject(localPlayer.ToBCStruct());
            if (pet == null || defautTarget.TargetObjectId != pet->GetGameObjectId()) return false;
            
            return true;
        }
    }
}
