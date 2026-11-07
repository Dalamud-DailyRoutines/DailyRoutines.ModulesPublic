using DailyRoutines.Common.Extensions;
using DailyRoutines.Extensions;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private void DrawTabItemSettings()
    {
        using var itemWidth = ImRaii.ItemWidth(500f * GlobalUIScale);

        using var tabSettings = ImRaii.TabItem(Lang.Get("Settings"));
        if (!tabSettings) return;

        using (ImRaii.Heading1(Lang.Get("AutoMarksFinder-RelayChatTypes")))
        using (var combo = ImRaii.Combo
               (
                   "###RelayCommandCombo",
                   Lang.Get
                   (
                       "AutoMarksFinder-Relay-SelectedChannels",
                       new Dictionary<string, object>
                       {
                           ["count"] = config.RelayCommands.Count
                       }
                   ),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                foreach (var command in SUPPORTED_RELAY_COMMANDS)
                {
                    if (ImGui.Selectable(command, config.RelayCommands.Contains(command), ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (!config.RelayCommands.Remove(command))
                            config.RelayCommands.Add(command);
                        config.Save(this);
                    }
                }
            }
        }

        ImGui.NewLine();

        DrawRelaySettings(RelayType.Position, ref config.RelayPositionPattern);

        ImGui.NewLine();

        DrawRelaySettings(RelayType.WaitAetheryte, ref config.RelayWaitAetherytePattern);

        ImGui.NewLine();

        DrawRelaySettings(RelayType.BeforeStart, ref config.AutoRelayBeforeStartPattern);

        ImGui.NewLine();

        DrawRelaySettings(RelayType.AfterEnd, ref config.AutoRelayAfterEndPattern);

        ImGui.NewLine();

        using (ImRaii.Heading1(Lang.Get("Command")))
            ImGui.TextUnformatted($"{COMMAND} {Lang.Get("AutoMarksFinder-HelpMessage")}");

        ImGui.NewLine();

        using (ImRaii.Heading1(Lang.Get("AutoMarksFinder-Relay-Placeholders")))
        {
            ImGui.TextWrapped
            (
                Lang.Get
                (
                    "AutoMarksFinder-Relay-PlaceholderHelp",
                    new Dictionary<string, object>
                    {
                        ["name"]              = "{name}",
                        ["position"]          = "{position}",
                        ["instance"]          = "{instance}",
                        ["instanceText"]      = "{instanceText}",
                        ["routeName"]         = "{routeName}",
                        ["routeMonsterCount"] = "{routeMonsterCount}"
                    }
                )
            );
        }
    }

    private void DrawRelaySettings
    (
        RelayType  type,
        ref string content
    )
    {
        var title = type switch
        {
            RelayType.Position      => "AutoMarksFinder-Relay-Pattern-Position",
            RelayType.WaitAetheryte => "AutoMarksFinder-Relay-Pattern-WaitAetheryte",
            RelayType.BeforeStart   => "AutoMarksFinder-RelayAutoBroadcastStart",
            RelayType.AfterEnd      => "AutoMarksFinder-RelayAutoBroadcastEnd",
            _                       => throw new ArgumentOutOfRangeException(nameof(type))
        };

        using var heading = ImRaii.Heading1(Lang.Get(title));
        using var id      = ImRaii.PushId(type.ToString());

        var isMultiline = type is RelayType.BeforeStart or RelayType.AfterEnd;
        if (isMultiline)
            ImGui.InputTextMultiline("###Content", ref content, 16_384, ScaledVector2(500f, 120f));
        else
            ImGui.InputText("###Content", ref content, 1024);

        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        var lineCount    = content.AsSpan().Count('\n') + 1;
        var tooManyLines = isMultiline && lineCount > MAX_RELAY_LINES;

        if (isMultiline)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{lineCount}/{MAX_RELAY_LINES}");
        }

        using (ImRaii.Disabled
               (
                   string.IsNullOrWhiteSpace(content) ||
                   tooManyLines                       ||
                   relayTaskHelper.IsBusy             ||
                   autoBroadcast.On
               ))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.ShareSquare, Lang.Get("Preview")))
                Relay(type, preview: true);
        }
    }
}
