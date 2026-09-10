using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface;

public partial class OptimizedFriendList
{
    private class DRFriendlistSearchSetting
    (
        OptimizedFriendList instance,
        TaskHelper          taskHelper
    ) : NativeAddon
    {
        private OptimizedFriendList Instance   { get; init; } = instance;
        private TaskHelper          TaskHelper { get; init; } = taskHelper;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var searchTypeTitleNode = new TextNode
            {
                String    = Lang.Get("OptimizedFriendList-SearchType"),
                FontSize  = 16,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                Position  = new(10f, 42f)
            };
            searchTypeTitleNode.AttachNode(this);

            var searchTypeLayoutNode = new VerticalListNode
            {
                Position  = new(20f, searchTypeTitleNode.Position.Y + 28f),
                Alignment = VerticalListAlignment.Left
            };

            var nameCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsChecked = Instance.config.SearchName,
                IsEnabled = true,
                String    = Lang.Get("Name"),
                OnClick = newState =>
                {
                    Instance.config.SearchName = newState;
                    Instance.config.Save(Instance);

                    Instance.ApplySearchFilter(Instance.searchString, TaskHelper);
                }
            };
            searchTypeLayoutNode.Height += searchTypeTitleNode.Height;

            var nicknameCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsChecked = Instance.config.SearchNickname,
                IsEnabled = true,
                String    = LuminaWrapper.GetAddonText(15207),
                OnClick = newState =>
                {
                    Instance.config.SearchNickname = newState;
                    Instance.config.Save(Instance);

                    Instance.ApplySearchFilter(Instance.searchString, TaskHelper);
                }
            };
            searchTypeLayoutNode.Height += nicknameCheckboxNode.Height;

            var remarkCheckboxNode = new CheckboxNode
            {
                Size      = new(80f, 20f),
                IsChecked = Instance.config.SearchRemark,
                IsEnabled = true,
                String    = LuminaWrapper.GetAddonText(13294).TrimEnd(':'),
                OnClick = newState =>
                {
                    Instance.config.SearchRemark = newState;
                    Instance.config.Save(Instance);

                    Instance.ApplySearchFilter(Instance.searchString, TaskHelper);
                }
            };
            searchTypeLayoutNode.Height += remarkCheckboxNode.Height;

            searchTypeLayoutNode.AddNode([nameCheckboxNode, nicknameCheckboxNode, remarkCheckboxNode]);
            searchTypeLayoutNode.AttachNode(this);

            var searchGroupIgnoreTitleNode = new TextNode
            {
                String    = Lang.Get("OptimizedFriendList-SearchIgnoreGroup"),
                FontSize  = 16,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                Position  = new(10f, searchTypeLayoutNode.Position.Y + searchTypeLayoutNode.Height + 12f)
            };
            searchGroupIgnoreTitleNode.AttachNode(this);

            var searchGroupIgnoreLayoutNode = new VerticalListNode
            {
                Position  = new(20f, searchGroupIgnoreTitleNode.Position.Y + 28f),
                Alignment = VerticalListAlignment.Left
            };


            for (var i = 0; i < 8; i++)
            {
                var index = i;

                var groupFormatText = ISeStringEvaluator.Instance().EvaluateFromAddon(12925, [index + 1]);
                var groupCheckboxNode = new CheckboxNode
                {
                    Size      = new(80f, 20f),
                    IsChecked = Instance.config.IgnoredGroup[i],
                    IsEnabled = true,
                    String    = groupFormatText,
                    OnClick = newState =>
                    {
                        Instance.config.IgnoredGroup[index] = newState;
                        Instance.config.Save(Instance);

                        Instance.ApplySearchFilter(Instance.searchString, TaskHelper);
                    }
                };

                searchGroupIgnoreLayoutNode.Height += groupCheckboxNode.Height;
                searchGroupIgnoreLayoutNode.AddNode(groupCheckboxNode);
            }

            searchGroupIgnoreLayoutNode.AttachNode(this);
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (FriendList == null)
                Close();
        }
    }
}
