using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes.ComponentNode;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class AutoRetainerWork
{
    private class DRAutoRetainerWork
    (
        AutoRetainerWork module
    ) : AttachedAddon("RetainerList")
    {
        private CollaspingNode? treeListNode;

        protected override Vector2 PositionOffset =>
            new(0f, 6f);

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
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

            ApplyControllerNavigation(addon);
        }

        protected override bool CanCloseHostAddon
        (
            AtkUnitBase* hostAddon
        ) => false;

        protected override bool CanOpenAddon => !module.IsAnyWorkerBusy();

        private void ApplyControllerNavigation
        (
            AtkUnitBase* addon
        )
        {
            if (treeListNode == null) return;

            List<ComponentNode> navigationNodes = [];

            foreach (var categoryNode in treeListNode.CategoryNodes)
            {
                var headerNode = new NavFocusNode
                {
                    Position     = new(2f, 14f),
                    OnSelected   = () => categoryNode.IsCollapsed = !categoryNode.IsCollapsed,
                    OnHoverStart = () => categoryNode.Timeline?.PlayAnimation(categoryNode.IsCollapsed ? 2 : 9),
                    OnHoverEnd   = () => categoryNode.Timeline?.PlayAnimation(categoryNode.IsCollapsed ? 1 : 8)
                };
                headerNode.AttachNode(categoryNode);
                navigationNodes.Add(headerNode);

                foreach (var contentNode in categoryNode.Children.OfType<VerticalListNode>().SelectMany(x => x.Nodes))
                {
                    switch (contentNode)
                    {
                        case CheckboxNode checkboxNode:
                            navigationNodes.Add(checkboxNode);
                            break;
                        case HorizontalFlexNode flexNode:
                        {
                            var buttonNodes = flexNode.Nodes.OfType<TextButtonNode>().ToList();
                            var rowStart    = navigationNodes.Count;

                            navigationNodes.AddRange(buttonNodes);

                            for (var index = 0; index < buttonNodes.Count; index++)
                            {
                                buttonNodes[index].NavLeft  = rowStart + (index == 0 ? buttonNodes.Count : index);
                                buttonNodes[index].NavRight = rowStart + (index == buttonNodes.Count - 1 ? 1 : index + 2);
                            }

                            break;
                        }
                    }
                }
            }

            if (navigationNodes.Count == 0) return;

            for (var index = 0; index < navigationNodes.Count; index++)
            {
                navigationNodes[index].NavIndex = index + 1;
                navigationNodes[index].NavUp    = index == 0 ? navigationNodes.Count : index;
                navigationNodes[index].NavDown  = index == navigationNodes.Count - 1 ? 1 : index + 2;
            }

            addon->FocusNode = navigationNodes[0];
        }
    }
}
