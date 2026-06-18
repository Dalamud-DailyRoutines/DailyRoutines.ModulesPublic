using System.Collections.Frozen;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using Action = System.Action;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class FieldEntryCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("FieldEntryCommandTitle"),
        Description         = Lang.Get("FieldEntryCommandDescription", COMMAND),
        Category            = ModuleCategory.Assist,
        ModulesPrerequisite = ["AutoTalkSkip"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private readonly FrozenDictionary<string, (Action EnqueueAction, uint Content)> commandArgs = new Dictionary<string, (Action EnqueueAction, uint Content)>
    {
        ["bozja"]   = (EnqueueBozja, 735),
        ["zadnor"]  = (EnqueueZadonor, 778),
        ["anemos"]  = (EnqueueAnemos, 283),
        ["pagos"]   = (EnqueuePagos, 581),
        ["pyros"]   = (EnqueuePyros, 598),
        ["hydatos"] = (EnqueueHydatos, 639),
        ["diadem"]  = (EnqueueDiadem, 753),
        ["island"]  = (EnqueueIsland, 1),
        ["ardorum"] = (EnqueueArdorum, 2),
        ["phaenna"] = (EnqueuePhaenna, 3),
        ["oizys"]   = (EnqueueOizys, 4),
        ["auxesia"] = (EnqueueAuxesia, 5),
        ["ocs"]     = (EnqueueSouthHorn, 1018)
    }.ToFrozenDictionary();
    
    private uint redirectTargetZoneInMoon;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        GamePacketManager.Instance().RegPreSendPacket(OnPreSendPacket);

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("FieldEntryCommand-CommandHelp") });
    }
    
    protected override void Uninit()
    {
        GamePacketManager.Instance().Unreg(OnPreSendPacket);

        CommandManager.Instance().RemoveCommand(COMMAND);

        redirectTargetZoneInMoon = 0;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}");

        using var indent = ImRaii.PushIndent();

        ImGui.TextUnformatted($"{COMMAND} → {Lang.Get("FieldEntryCommand-CommandHelp")}");

        ImGui.Spacing();

        using var table = ImRaii.Table
        (
            "ArgsTable",
            2,
            ImGuiTableFlags.Borders,
            (ImGui.GetContentRegionAvail() / 2) with { Y = 0 }
        );
        if (!table) return;

        ImGui.TableSetupColumn(Lang.Get("Argument"),              ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(14098), ImGuiTableColumnFlags.WidthStretch, 20);

        ImGui.TableHeadersRow();

        foreach (var command in commandArgs)
        {
            if (!LuminaGetter.TryGetRow<ContentFinderCondition>(command.Value.Content, out var data)) continue;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(command.Key);

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText($"{command.Key}");
                NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {command.Key}");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ContentToPlaceName.TryGetValue(command.Value.Content, out var placeName) ? placeName : data.Name.ToString());
        }
    }

    private void OnCommand(string command, string args)
    {
        TaskHelper.Abort();

        args = args.Trim().ToLowerInvariant();

        foreach (var commandPair in commandArgs)
        {
            if (!LuminaGetter.TryGetRow<ContentFinderCondition>(commandPair.Value.Content, out var data)) continue;

            var contentName = ContentToPlaceName.TryGetValue(commandPair.Value.Content, out var placeName) ? placeName : data.Name.ToString();

            if (args == commandPair.Key || contentName.Contains(args, StringComparison.OrdinalIgnoreCase))
            {
                commandPair.Value.EnqueueAction();
                NotifyHelper.Instance().NotificationInfo(Lang.Get("FieldEntryCommand-Notification", contentName));
                return;
            }
        }
    }

    public void OnPreSendPacket(ref bool isPrevented, int opcode, ref nint packet, ref bool isPrioritize)
    {
        if (redirectTargetZoneInMoon == 0 || GameState.TerritoryType != 959) return;

        if (opcode == UpstreamOpcode.EventCompleteOpcode)
        {
            var data = (EventCompletePackt*)packet;
            if (data->EventID != 0x500AF) return;

            if (data->Category == 0x2000000)
                data->Param1 = redirectTargetZoneInMoon;

            if (data->Category == 0x1000064)
            {
                data->Param0             = redirectTargetZoneInMoon;
                redirectTargetZoneInMoon = 0;
            }
        }
    }

    // 开拓无人岛
    private static void EnqueueIsland() =>
        MovementManager.Instance().TPSmart_BetweenZone(1055, new(-269.4f, 40.0f, 227.8f));

    // 云冠群岛
    private static void EnqueueDiadem() =>
        MovementManager.Instance().TPSmart_BetweenZone(939);

    // 常风之地
    private static void EnqueueAnemos() =>
        MovementManager.Instance().TPSmart_BetweenZone(732);

    // 恒冰之地
    private static void EnqueuePagos() =>
        MovementManager.Instance().TPSmart_BetweenZone(763);

    // 涌火之地
    private static void EnqueuePyros() =>
        MovementManager.Instance().TPSmart_BetweenZone(795);

    // 丰水之地
    private static void EnqueueHydatos() =>
        MovementManager.Instance().TPSmart_BetweenZone(827);
    
    // 博兹雅
    private static void EnqueueBozja() =>
        MovementManager.Instance().TPSmart_BetweenZone(920);

    // 扎杜诺尔
    private static void EnqueueZadonor() =>
        MovementManager.Instance().TPSmart_BetweenZone(975);
    
    // 蜃景幻界新月岛 南征之章
    private static void EnqueueSouthHorn() =>
        MovementManager.Instance().TPSmart_BetweenZone(1252);
    
    // 憧憬湾
    private static void EnqueueArdorum() =>
        MovementManager.Instance().TPSmart_BetweenZone(1237);

    // 法恩娜
    private static void EnqueuePhaenna() =>
        MovementManager.Instance().TPSmart_BetweenZone(1291);

    // 俄匊斯
    private static void EnqueueOizys() =>
        MovementManager.Instance().TPSmart_BetweenZone(1310);
    
    // 奥克塞西亚
    private static void EnqueueAuxesia() =>
        MovementManager.Instance().TPSmart_BetweenZone(1319);
    
    #region 常量

    private const string COMMAND = "/pdrfe";
    
    private static readonly FrozenDictionary<uint, string> ContentToPlaceName = new Dictionary<uint, string>
    {
        // 开拓无人岛
        [1] = LuminaWrapper.GetPlaceName(2566),
        // 憧憬湾
        [2] = LuminaWrapper.GetPlaceName(5219),
        // 法恩娜
        [3] = LuminaWrapper.GetPlaceName(5301),
        // 俄匊斯
        [4] = LuminaWrapper.GetPlaceName(5406),
        // 奥克塞西亚
        [5] = LuminaWrapper.GetPlaceName(5551),
    }.ToFrozenDictionary();

    #endregion
}
