using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic.Interface;

// TODO: 支持驯兽师的魔物调整（/beastsize）
public unsafe class PetSizeContextMenu : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("PetSizeContextMenuTitle"),
        Description = Lang.Get("PetSizeContextMenuDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private UpperContainerItem containerItem = null!;

    protected override void Init()
    {
        containerItem = new();
        ContextMenuManager.Instance().Reg(containerItem);
    }

    protected override void Uninit() =>
        ContextMenuManager.Instance().Unreg(containerItem);

    private sealed class UpperContainerItem : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(PetSizeContextMenu);

        private readonly PetSizeAdjustItem[] subMenuItems =
        [
            new PetSizeSmallItem(),
            new PetSizeMediumItem(),
            new PetSizeLargeItem()
        ];

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            var localPlayer = Control.GetLocalPlayer();
            if (localPlayer == null) 
                return null;
            
            if (localPlayer->ClassJob is not (26 or 27))
                return null;

            var pet = CharacterManager.Instance()->LookupPetByOwnerObject(localPlayer);
            if (pet == null) 
                return null;

            string? name = null;
            
            if (args.TargetObjectID == (ulong)pet->GetGameObjectId())
                name = Lang.Get("PetSizeContextMenu-ContextMenu-Pet");

            if (args.TargetContentID == LocalPlayerState.ContentID)
                name = Lang.Get("PetSizeContextMenu-ContextMenu-Self");

            if (!string.IsNullOrEmpty(name))
            {
                return new()
                {
                    Name = name,
                    Submenu = new()
                    {
                        Title   = name,
                        Entries = subMenuItems
                    }
                };
            }

            return null;
        }
    }

    private abstract class PetSizeAdjustItem : ContextMenuEntry
    {
        public override string Identifier => 
            nameof(PetSizeContextMenu);

        protected abstract uint AddonTextID { get; }

        protected abstract string TextCommandParam { get; }

        public override ContextMenuItem Create
        (
            ContextMenuOpenedArgs args
        ) =>
            new()
            {
                Name      = Lang.Get("PetSizeContextMenu-ContextMenu-Sub", LuminaWrapper.GetAddonText(AddonTextID)),
                OnClicked = _ => ChatManager.Instance().SendMessage($"/petsize all {TextCommandParam}")
            };
    }
    
    private sealed class PetSizeSmallItem : PetSizeAdjustItem
    {
        protected override uint   AddonTextID      => 6373;
        protected override string TextCommandParam => "small";
    }
    
    private sealed class PetSizeMediumItem : PetSizeAdjustItem
    {
        protected override uint   AddonTextID      => 6372;
        protected override string TextCommandParam => "medium";
    }
    
    private sealed class PetSizeLargeItem : PetSizeAdjustItem
    {
        protected override uint   AddonTextID      => 6371;
        protected override string TextCommandParam => "large";
    }
}
