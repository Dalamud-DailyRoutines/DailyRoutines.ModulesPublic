using DailyRoutines.Common.Info;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Addons;

namespace DailyRoutines.ModulesPublic;

public partial class OptimizedSalvage
{
    private OperationAddon? addon;

    private class OperationAddon
    (
        OptimizedSalvage module
    ) : AttachedAddon("SalvageItemSelector")
    {
        public TextButtonNode? OperationButton { get; private set; }

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValueSpan
        )
        {
            var rootContainer = new VerticalListNode
            {
                FitContents = true,
                Width       = ContentSize.X,
                Position    = ContentStartPosition,
                ItemSpacing = 2f
            };
            rootContainer.AttachNode(this);

            rootContainer.AddDummy(5f);

            OperationButton = new TextButtonNode
            {
                TextureType = ButtonTextureType.ButtonB,
                String      = Lang.Get("OptimizedSalvage-BatchDesynthesize"),
                Size        = new(rootContainer.Width, 36),
                OnClick = () =>
                {
                    if (module.TaskHelper.IsBusy)
                        module.TaskHelper.Abort();
                    else
                        module.TaskHelper.Enqueue(() => module.EnqueueDesynthesize());
                }
            };
            rootContainer.AddNode(OperationButton);

            var skipHQCheckbox = new CheckboxNode
            {
                String    = Lang.Get("SkipHQItem"),
                Size      = new(rootContainer.Width, 28),
                IsChecked = module.config.SkipHQ,
                OnClick = value =>
                {
                    module.config.SkipHQ = value;
                    module.config.Save(module);
                }
            };
            rootContainer.AddNode(skipHQCheckbox);

            var skipHQHint = new TextNode
            {
                FontSize  = 12,
                TextFlags = TextFlags.AutoAdjustNodeSize | TextFlags.WordWrap | TextFlags.MultiLine,
                Width     = rootContainer.Width,
                String    = Lang.Get("OptimizedSalvage-Hint-OnlyBatch")
            };
            AtkColors.Hint.ApplyTo(skipHQHint);
            rootContainer.AddNode(skipHQHint);

            rootContainer.AddDummy(2f);

            var confirmWhenManualCheckbox = new CheckboxNode
            {
                String    = Lang.Get("OptimizedSalvage-ConfirmWhenManual"),
                Size      = new(rootContainer.Width, 28),
                IsChecked = module.config.ConfirmWhenManual,
                OnClick = value =>
                {
                    module.config.ConfirmWhenManual = value;
                    module.config.Save(module);
                }
            };
            rootContainer.AddNode(confirmWhenManualCheckbox);

            var confirmWhenManualHint = new TextNode
            {
                FontSize  = 12,
                Width     = rootContainer.Width,
                TextFlags = TextFlags.AutoAdjustNodeSize | TextFlags.WordWrap | TextFlags.MultiLine,
                String    = Lang.Get("OptimizedSalvage-Hint-OnlyManual")
            };
            AtkColors.Hint.ApplyTo(confirmWhenManualHint);
            rootContainer.AddNode(confirmWhenManualHint);

            rootContainer.RecalculateLayout();
            SetWindowSize(Size.X, rootContainer.Height + ContentStartPosition.Y + 24f);
            rootContainer.Position = ContentStartPosition;
        }
    }
}
