using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class CopyItemNameContextMenu : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CopyItemNameContextMenuTitle"),
        Description = Lang.Get("CopyItemNameContextMenuDescription"),
        Category    = ModuleCategory.System,
        Author      = ["Nukoooo"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private CopyItemNameMenuItem menuItem        = null!;
    private CopyItemNameMenuItem glamourMenuItem = null!;

    protected override void Init()
    {
        var name        = LuminaWrapper.GetAddonText(159);
        var glamourName = LuminaGetter.GetRowOrDefault<CircleActivity>(18).Name.ToString();
        menuItem        = new(name, name, false);
        glamourMenuItem = new($"{name} ({glamourName})", name, true);

        ContextMenuManager.Instance().Reg(menuItem);
        ContextMenuManager.Instance().Reg(glamourMenuItem);
    }

    protected override void Uninit()
    {
        ContextMenuManager.Instance().Unreg(menuItem);
        ContextMenuManager.Instance().Unreg(glamourMenuItem);
    }

    private sealed class CopyItemNameMenuItem
    (
        string name,
        string nativeName,
        bool   glamour
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(CopyItemNameContextMenu);
        
        public override unsafe ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.InventoryAgentContext == null)
            {
                if (string.IsNullOrWhiteSpace(args.AddonName) || args.AddonName == "FreeCompanyExchange")
                    return null;

                var agent = args.DefaultAgentContext;

                if (agent != null && agent->CurrentContextMenu != null)
                {
                    var values = agent->CurrentContextMenu->EventParams;
                    var count  = Math.Clamp(values[0].Int, 0, values.Length - 8);
                    for (var i = 8; i < 8 + count; i++)
                        if (string.Equals(values[i].GetValueAsString(), nativeName, StringComparison.OrdinalIgnoreCase))
                            return null;
                }
            }

            var item = glamour ?
                           args.TargetGlamourRow :
                           args.TargetItemRow;
            if (item.RowId == 0)
                return null;

            var itemName = !glamour && item.RowId >= 2_000_000 ?
                               item.RowId.ToLuminaRowRef<EventItem>().ValueNullable?.Singular.ToString() :
                               item.ValueNullable?.Name.ToString();
            if (string.IsNullOrWhiteSpace(itemName))
                return null;

            return new()
            {
                Name = name,
                OnClicked = _ =>
                {
                    RaptureLogModule.Instance()->ShowLogMessageUInt(1632, item.RowId);
                    ImGui.SetClipboardText(itemName);
                }
            };
        }
    }
}
