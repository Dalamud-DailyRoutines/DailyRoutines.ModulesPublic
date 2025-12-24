using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel.Sheets;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic;

public class BaitSwitchCommand : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("BaitSwitchCommandTitle"),
        Description = GetLoc("BaitSwitchCommandDescription"),
        Category    = ModuleCategories.Assist
    };
    
    private const string Command = "bait";

    private static readonly Dictionary<uint, (string NameLower, string NamePinyin)> Baits = 
        LuminaGetter.Get<Item>()
                    .Where(x => x.FilterGroup == 17 && !string.IsNullOrWhiteSpace(x.Name.ExtractText()))
                    .ToDictionary(x => x.RowId, x => (x.Name.ExtractText().ToLower(),
                                                         PinyinHelper.GetPinyin(x.Name.ExtractText(), string.Empty)));
    private static readonly Dictionary<uint, (string NameLower, string NamePinyin)> Fishes = 
        LuminaGetter.Get<Item>()
                    .Where(x => x.FilterGroup == 16 && !string.IsNullOrWhiteSpace(x.Name.ExtractText()))
                    .ToDictionary(x => x.RowId, x => (x.Name.ExtractText().ToLower(),
                                                         PinyinHelper.GetPinyin(x.Name.ExtractText(), string.Empty)));

    protected override void Init() => 
        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("BaitSwitchCommand-CommandHelp") });
    
    protected override void Uninit() => 
        CommandManager.RemoveSubCommand(Command);

    protected override void ConfigUI() => 
        ImGui.TextWrapped(GetLoc("BaitSwitchCommand-CommandHelpDetailed"));

    public static void OnCommand(string command, string arguments)
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
            ChatError(GetLoc("BaitSwitchCommand-Notice-NoMatchBait", itemName));
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
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Fish, 4, itemID);
        else if (swimBaitIndex != -1)
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.Fish, 25, (uint)swimBaitIndex);
    }

    private static bool TryFindItemByName(
        Dictionary<uint, (string NameLower, string NamePinyin)> source, string itemName, out uint item)
    {
        item = source
               .FirstOrDefault(x => x.Value.NameLower.Equals(itemName, StringComparison.OrdinalIgnoreCase) ||
                                    x.Value.NamePinyin.Equals(itemName, StringComparison.OrdinalIgnoreCase)).Key;
        
        if (item == 0)
        {
            var matchingItems = source
                                .Where(x => x.Value.NameLower.Contains(itemName, StringComparison.OrdinalIgnoreCase) ||
                                            (DService.ClientState.ClientLanguage == (ClientLanguage)4 &&
                                             x.Value.NamePinyin.Contains(itemName, StringComparison.OrdinalIgnoreCase)))
                                .OrderBy(x => x.Value.NameLower)
                                .ToList();

            item = matchingItems.FirstOrDefault().Key;
        }

        return item != 0;
    }

    private static unsafe bool IsAbleToSwitch(uint itemID, out bool isBait, out int swimBaitIndex)
    {
        isBait = true;
        swimBaitIndex = -1;

        if (itemID == 0 || (!Baits.ContainsKey(itemID) && !Fishes.ContainsKey(itemID)))
        {
            ChatError(GetLoc("BaitSwitchCommand-Notice-NoMatchBait", itemID));
            return false;
        }

        var itemName = LuminaGetter.GetRow<Item>(itemID)?.Name.ExtractText();

        if (Baits.ContainsKey(itemID))
        {
            if (InventoryManager.Instance()->GetInventoryItemCount(itemID) <= 0)
            {
                ChatError(GetLoc("BaitSwitchCommand-Notice-NoBait", itemName));
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
                ChatError(GetLoc("BaitSwitchCommand-Notice-NoBait", itemName));
                return false;
            }
        }

        if (DService.Condition[ConditionFlag.Fishing])
        {
            ChatError(GetLoc("BaitSwitchCommand-Notice-FishingNow"));
            return false;
        }

        return true;
    }

    private static unsafe List<uint> GetSwimBaitInfo()
    {
        var handler = EventFramework.Instance()->GetEventHandlerById(0x150001u);
        var itemArray = (uint*)((byte*)handler + 568);

        return [itemArray[0], itemArray[1], itemArray[2]];
    }
}
