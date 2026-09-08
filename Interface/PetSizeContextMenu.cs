using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic.Interface;

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
            
            if (localPlayer->ClassJob is not (26 or 27 or 43))
                return null;

            var pet = CharacterManager.Instance()->LookupPetByOwnerObject(localPlayer);
            if (pet == null) 
                return null;

            string? name = null;
            
            if (args.TargetObjectID == (ulong)pet->GetGameObjectId())
                name = LuminaWrapper.GetAddonText(17709);

            if (args.TargetContentID == LocalPlayerState.ContentID)
            {
                name = Lang.Get
                (
                    LocalPlayerState.ClassJob == 43 ?
                        "PetSizeContextMenu-ContextMenu-Self-Beast" :
                        "PetSizeContextMenu-ContextMenu-Self-Pet"
                );
            }

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
        
        protected abstract uint BeastMasterAddonTextID { get; }

        protected abstract string TextCommandParam { get; }

        public override ContextMenuItem Create
        (
            ContextMenuOpenedArgs args
        )
        {
            var isBeastMaster = LocalPlayerState.ClassJob == 43;

            return new ContextMenuItem
            {
                Name = isBeastMaster ?
                           LuminaWrapper.GetAddonText(BeastMasterAddonTextID) :
                           Lang.Get("PetSizeContextMenu-ContextMenu-Sub-Pet", LuminaWrapper.GetAddonText(AddonTextID)),
                OnClicked = _ =>
                {
                    if (LocalPlayerState.ClassJob == 43)
                    {
                        ChatManager.Instance().SendMessage($"/beastsize all {TextCommandParam}");
                        return;
                    }

                    ChatManager.Instance().SendMessage($"/petsize all {TextCommandParam}");
                }
            };
        }
    }
    
    private sealed class PetSizeSmallItem : PetSizeAdjustItem
    {
        protected override uint   AddonTextID            => 6373;
        protected override uint   BeastMasterAddonTextID => 17689;
        protected override string TextCommandParam       => "small";
    }
    
    private sealed class PetSizeMediumItem : PetSizeAdjustItem
    {
        protected override uint   AddonTextID            => 6372;
        protected override uint   BeastMasterAddonTextID => 17690;
        protected override string TextCommandParam       => "medium";
    }
    
    private sealed class PetSizeLargeItem : PetSizeAdjustItem
    {
        protected override uint   AddonTextID            => 6371;
        protected override uint   BeastMasterAddonTextID => 17691;
        protected override string TextCommandParam       => "large";
    }
}
