using System.Numerics;
using DailyRoutines.Common.KamiToolKit.Addons;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWork
{
    private class DRAutoRetainerWork
    (
        AutoRetainerWork module
    ) : AttachedAddon("RetainerList")
    {
        private TreeListNode? treeListNode;

        protected override Vector2 PositionOffset =>
            new(0f, 6f);

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (WindowNode is WindowNode windowNode)
                windowNode.CloseButtonNode.IsVisible = false;

            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x4,  true);
            FlagHelper.UpdateFlag(ref addon->Flags1A0, 0x80, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x40, true);
            FlagHelper.UpdateFlag(ref addon->Flags1A3, 0x1,  true);

            var width = ContentSize.X;
            treeListNode = new()
            {
                IsVisible               = true,
                Position                = ContentStartPosition,
                Size                    = new(width, 0f),
                CategoryVerticalSpacing = 4f,
                OnLayoutUpdate = height =>
                {
                    SetWindowSize(Size.X, ContentStartPosition.Y + height + 16f);
                    if (treeListNode == null) return;

                    treeListNode.Position = ContentStartPosition;
                    treeListNode.Height   = height;
                }
            };

            foreach (var worker in module.workers)
            {
                var categoryNode = worker.CreateOverlayCategory(width);
                if (categoryNode == null) continue;

                treeListNode.AddCategoryNode(categoryNode);
            }

            treeListNode.AttachNode(addon);

            treeListNode.RefreshLayout();
        }

        protected override bool CanCloseHostAddon(AtkUnitBase* hostAddon) => false;

        protected override bool CanOpenAddon => !module.IsAnyWorkerBusy();
    }
}
