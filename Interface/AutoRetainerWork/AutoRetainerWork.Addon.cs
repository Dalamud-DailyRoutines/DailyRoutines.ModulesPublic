using DailyRoutines.Common.Info;
using DailyRoutines.Internal;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes.ComponentNode;
using KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Addons;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class AutoRetainerWork
{
    private class DRAutoRetainerWork
    (
        AutoRetainerWork module
    ) : AttachedAddon("RetainerList")
    {
        private readonly Dictionary<CollapsingHeaderNode, NavFocusNode> categoryFocusNodes = [];

        private VerticalListNode? treeListNode;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            categoryFocusNodes.Clear();

            var width = ContentSize.X;
            treeListNode = new()
            {
                Position    = ContentStartPosition,
                Size        = new(width, 0f),
                FitContents = true,
                FitWidth    = true,
                ItemSpacing = 4f,
                OnSizeUpdated = () =>
                {
                    SetWindowSize
                    (
                        Size.X,
                        ContentStartPosition.Y + treeListNode.Height + 16f
                    );
                }
            };

            foreach (var worker in module.workers)
            {
                var categoryNode = worker.CreateOverlayCategory(width);
                if (categoryNode == null) continue;

                var headerFocusNode = new NavFocusNode
                {
                    Position   = new(2f, 14f),
                    OnSelected = () => categoryNode.IsCollapsed = !categoryNode.IsCollapsed
                };
                headerFocusNode.AttachNode(categoryNode);
                categoryFocusNodes.Add(categoryNode, headerFocusNode);

                treeListNode.AddNode(categoryNode);

                var updateLayout = () =>
                {
                    treeListNode.RecalculateLayout();
                    ApplyControllerNavigation(addon);
                };
                categoryNode.OnCollapse   = updateLayout;
                categoryNode.OnUncollapse = updateLayout;
            }

            var hintTextNode = new TextNode
            {
                Width     = ContentSize.X,
                TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
                String = $"※{Lang.Get
                (
                    "Common-SupportConflictKeyToInterrupt",
                    new Dictionary<string, object>
                    {
                        ["conflictKey"] = PluginConfig.Instance().ConflictKeyBinding
                    }
                )}",
                FontSize = 12
            };
            AtkColors.Hint.ApplyTo(hintTextNode);
            hintTextNode.Height = hintTextNode.GetTextDrawSize(false).Y;

            treeListNode.AddNode(hintTextNode);

            treeListNode.AttachNode(addon);

            treeListNode.RecalculateLayout();

            ApplyControllerNavigation(addon);

            if (categoryFocusNodes.Count > 0)
                addon->FocusNode = categoryFocusNodes.Values.First();
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            foreach (var worker in module.workers)
                worker.UpdateOverlayActionButton();

            base.OnUpdate(addon);
        }

        private void ApplyControllerNavigation
        (
            AtkUnitBase* addon
        )
        {
            if (treeListNode == null) return;

            List<ComponentNode> navigationNodes = [];

            foreach (var (categoryNode, headerFocusNode) in categoryFocusNodes)
            {
                navigationNodes.Add(headerFocusNode);

                foreach (var contentNode in categoryNode.GetNodes<ComponentNode>())
                {
                    if (categoryNode.IsCollapsed)
                    {
                        contentNode.NavIndex = 0;
                        contentNode.NavUp    = 0;
                        contentNode.NavDown  = 0;

                        if (addon->FocusNode == contentNode.ResNode)
                            addon->FocusNode = headerFocusNode;
                    }
                    else
                        navigationNodes.Add(contentNode);
                }
            }

            if (navigationNodes.Count == 0) return;

            for (var index = 0; index < navigationNodes.Count; index++)
            {
                navigationNodes[index].NavIndex = index + 1;
                navigationNodes[index].NavUp = index == 0 ?
                                                   navigationNodes.Count :
                                                   index;
                navigationNodes[index].NavDown = index == navigationNodes.Count - 1 ?
                                                     1 :
                                                     index + 2;
            }

        }
    }
}
