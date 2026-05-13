using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TinyPinyin;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public class UseItemCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UseItemCommandTitle"),
        Description = Lang.Get("UseItemCommandDescription", COMMAND),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("UseItemCommand-CommandHelp") });

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    private static unsafe void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrEmpty(args)) return;

        if (uint.TryParse(args, out var itemID))
        {
            UseItemByID(itemID);
            return;
        }

        if (!Inventories.Player.TryGetItems(_ => true, out var items))
        {
            NotifyHelper.Instance().ChatError(Lang.Get("UseItemCommand-Notice-NotFound", args));
            return;
        }

        args = args.ToLowerInvariant();
        foreach (var item in items)
        {
            if (!LuminaGetter.TryGetRow<Item>(item.GetBaseItemId(), out var itemRow)) continue;
            var name = itemRow.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (name.Contains(args, StringComparison.OrdinalIgnoreCase) ||
                PinyinHelper.GetPinyin(name, string.Empty).Contains(args, StringComparison.OrdinalIgnoreCase))
            {
                AgentInventoryContext.Instance()->UseItem(item.ItemId);
                return;
            }
        }

        NotifyHelper.Instance().ChatError(Lang.Get("UseItemCommand-Notice-NotFound", args));
    }

    private static unsafe void UseItemByID(uint itemID)
    {
        if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow))
        {
            NotifyHelper.Instance().ChatError(Lang.Get("UseItemCommand-Notice-NotFound", itemID));
            return;
        }

        if (!Inventories.Player.TryGetFirstItem(x => x.GetBaseItemId() == itemID, out var item))
        {
            NotifyHelper.Instance().ChatError(Lang.Get("UseItemCommand-Notice-NotInInventory", itemRow.Name.ToString()));
            return;
        }

        AgentInventoryContext.Instance()->UseItem(itemID, item->GetInventoryType(), item->GetSlot());
    }

    #region 常量

    private const string COMMAND = "item";

    #endregion
}
