using System.Collections.Frozen;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TinyPinyin;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public class BaitSwitchCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BaitSwitchCommandTitle"),
        Description = Lang.Get("BaitSwitchCommandDescription"),
        Category    = ModuleCategory.Assist
    };

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BaitSwitchCommand-CommandHelp") });

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    protected override void ConfigUI() =>
        ImGui.TextWrapped(Lang.Get("BaitSwitchCommand-CommandHelpDetailed"));

    private static void OnCommand(string command, string arguments)
    {
        arguments = arguments.Trim();
        if (string.IsNullOrWhiteSpace(arguments)) return;
        if (!uint.TryParse(arguments, out var itemID))
            SwitchBaitByName(arguments);
        else
            SwitchBaitByID(itemID);
    }

    private static void SwitchBaitByName(string itemName)
    {
        itemName = itemName.ToLower();

        var resultBait = TryFindItemByName(Baits, itemName, out var itemID);
        var resultFish = false;
        if (!resultBait)
            resultFish = TryFindItemByName(Fishes, itemName, out itemID);

        // 要么都没找到 要么都找到了
        if (resultBait == resultFish)
        {
            NotifyHelper.Instance().ChatError(Lang.Get("BaitSwitchCommand-Notice-NoMatchBait", itemName));
            return;
        }

        SwitchBaitByID(itemID);
    }

    private static void SwitchBaitByID(uint itemID)
    {
        if (!IsAbleToSwitch(itemID, out var isBait, out var swimBaitIndex)) return;
        SwitchBait(itemID, isBait, swimBaitIndex);
    }

    private static void SwitchBait(uint itemID, bool isBait, int swimBaitIndex = -1)
    {
        if (isBait)
            FishingCommand.ChangeBait(itemID);
        else if (swimBaitIndex != -1)
            FishingCommand.SwimBait((uint)swimBaitIndex);
    }

    private static bool TryFindItemByName
    (
        IDictionary<uint, (string NameLower, string NamePinyin)> source,
        string                                                   itemName,
        out uint                                                 item
    )
    {
        item = source
               .FirstOrDefault
               (x => x.Value.NameLower.Equals(itemName, StringComparison.OrdinalIgnoreCase) ||
                     x.Value.NamePinyin.Equals(itemName, StringComparison.OrdinalIgnoreCase)
               ).Key;

        if (item == 0)
        {
            var matchingItems = source
                                .Where
                                (x => x.Value.NameLower.Contains(itemName, StringComparison.OrdinalIgnoreCase) ||
                                      DService.Instance().ClientState.ClientLanguage == (ClientLanguage)4 &&
                                      x.Value.NamePinyin.Contains(itemName, StringComparison.OrdinalIgnoreCase)
                                )
                                .OrderBy(x => x.Value.NameLower)
                                .ToList();

            item = matchingItems.FirstOrDefault().Key;
        }

        return item != 0;
    }

    private static unsafe bool IsAbleToSwitch(uint itemID, out bool isBait, out int swimBaitIndex)
    {
        isBait        = true;
        swimBaitIndex = -1;

        if (itemID == 0 || !Baits.ContainsKey(itemID) && !Fishes.ContainsKey(itemID))
        {
            NotifyHelper.Instance().ChatError(Lang.Get("BaitSwitchCommand-Notice-NoMatchBait", itemID));
            return false;
        }

        var itemName = LuminaGetter.GetRow<Item>(itemID)?.Name.ToString();

        if (Baits.ContainsKey(itemID))
        {
            if (InventoryManager.Instance()->GetInventoryItemCount(itemID) <= 0)
            {
                NotifyHelper.Instance().ChatError(Lang.Get("BaitSwitchCommand-Notice-NoBait", itemName));
                return false;
            }
        }
        else
        {
            isBait = false;
            var info = GetSwimBaitInfo();
            swimBaitIndex = info.IndexOf(itemID);

            if (swimBaitIndex == -1)
            {
                NotifyHelper.Instance().ChatError(Lang.Get("BaitSwitchCommand-Notice-NoBait", itemName));
                return false;
            }
        }

        if (DService.Instance().Condition[ConditionFlag.Fishing])
        {
            NotifyHelper.Instance().ChatError(Lang.Get("BaitSwitchCommand-Notice-FishingNow"));
            return false;
        }

        return true;
    }

    private static unsafe List<uint> GetSwimBaitInfo()
    {
        var handler   = EventFramework.Instance()->GetEventHandlerById(0x150001);
        var itemArray = (uint*)((byte*)handler + 568);

        return [itemArray[0], itemArray[1], itemArray[2]];
    }

    #region 常量

    private const string COMMAND = "bait";

    private static readonly FrozenDictionary<uint, (string NameLower, string NamePinyin)> Baits =
        LuminaGetter.Get<Item>()
                    .Where(x => x.FilterGroup == 17 && !string.IsNullOrWhiteSpace(x.Name.ToString()))
                    .ToFrozenDictionary
                    (
                        x => x.RowId,
                        x => (x.Name.ToString().ToLower(),
                                 PinyinHelper.GetPinyin(x.Name.ToString(), string.Empty))
                    );

    private static readonly FrozenDictionary<uint, (string NameLower, string NamePinyin)> Fishes =
        LuminaGetter.Get<Item>()
                    .Where(x => x.FilterGroup == 16 && !string.IsNullOrWhiteSpace(x.Name.ToString()))
                    .ToFrozenDictionary
                    (
                        x => x.RowId,
                        x => (x.Name.ToString().ToLower(),
                                 PinyinHelper.GetPinyin(x.Name.ToString(), string.Empty))
                    );

    #endregion
}
