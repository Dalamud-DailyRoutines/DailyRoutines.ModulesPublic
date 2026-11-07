using System.Globalization;
using DailyRoutines.Common.Extensions;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.ImGuiOm.Widgets;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private readonly DatePicker departureDatePicker = new();

    protected override void OverlayUI()
    {
        DrawTrainsSelectCombo(300f * GlobalUIScale, selection);
        var selected = SelectedTrain;

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon("设置", FontAwesomeIcon.Cog, Lang.Get("Settings")))
            ChatManager.Instance().SendMessage($"/pdr search {GetType().Name}");

        if (movement.IsBusy &&
            scanner?.IsRunning != true)
        {
            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("###StopMovement", FontAwesomeIcon.Stop, Lang.Get("Stop")))
                movement.Stop();
        }

        ImGui.Spacing();

        DrawAutoBroadcastControls();

        ImGui.Separator();

        if (selected == null) return;

        var source = selected.MonstersFound;
        if (source.Count == 0) return;

        var monsterIndex = selection.MonsterIndex;

        using var table = ImRaii.Table("##SmallCardTable", 3, ImGuiTableFlags.Resizable);
        if (!table) return;

        ImGui.TableSetupColumn("Left",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mid",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        if (monsterIndex > 0)
        {
            if (ImGuiOm.ButtonIcon("##FormerMob", FontAwesomeIcon.ArrowLeft))
                selection.MonsterIndex--;
        }

        ImGui.TableNextColumn();
        ImGuiOm.Text($"{Lang.Get("Current")}");

        ImGui.TableNextColumn();

        if (monsterIndex < source.Count - 1)
        {
            if (ImGuiOm.ButtonIcon("##NextMob", FontAwesomeIcon.ArrowRight))
                selection.MonsterIndex++;
        }

        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(1);
        monsterIndex = selection.MonsterIndex;
        DrawOverlayMonsterCard(selected, source[monsterIndex], true);
        var cardHeight = ImGui.GetItemRectSize().Y;

        ImGui.TableSetColumnIndex(0);
        if (monsterIndex > 0)
            DrawOverlayMonsterCard(selected, source[monsterIndex - 1], false);
        else
            DrawOverlayRelayButton(RelayType.BeforeStart, cardHeight);

        ImGui.TableSetColumnIndex(2);
        if (monsterIndex < source.Count - 1)
            DrawOverlayMonsterCard(selected, source[monsterIndex + 1], true);
        else
            DrawOverlayRelayButton(RelayType.AfterEnd, cardHeight);
    }

    private void DrawAutoBroadcastControls()
    {
        using var heading = ImRaii.Heading1(Lang.Get("AutoMarksFinder-AutoBroadcast"));

        using (ImRaii.Disabled(autoBroadcast.IsRunning))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Lang.Get("AutoMarksFinder-AutoBroadcastStartTime"));

            ImGui.SameLine();
            var departure = autoBroadcast.LocalDeparture;
            if (ImGui.Button($"{departure.ToString("d", CultureInfo.CurrentCulture)}###DepartureDate"))
                ImGui.OpenPopup("DepartureDatePicker");

            using (var popup = ImRaii.Popup("DepartureDatePicker"))
            {
                if (popup.Success &&
                    departureDatePicker.Draw("DepartureDate", ref departure))
                {
                    autoBroadcast.LocalDeparture = departure.Date + autoBroadcast.LocalDeparture.TimeOfDay;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();
            var time = TimeOnly.FromDateTime(autoBroadcast.LocalDeparture);
            if (ImGuiOm.InputTime("DepartureTime", ref time))
                autoBroadcast.LocalDeparture = autoBroadcast.LocalDeparture.Date + time.ToTimeSpan();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("AutoMarksFinder-AutoBroadcastUseCurrentTime")))
                autoBroadcast.LocalDeparture = DateTimeOffset.FromUnixTimeSeconds(Framework.GetServerTime()).LocalDateTime;
        }

        using (ImRaii.Disabled
               (
                   autoBroadcast.IsRunning                           ||
                   SelectedTrain is not { MonstersFound.Count: > 0 } ||
                   config.RelayCommands.Count == 0                   ||
                   (SelectedTrain?.MonstersFound.Any(monster => monster.IsAlive) == true &&
                    string.IsNullOrWhiteSpace(config.RelayPositionPattern)) ||
                   scanner?.IsRunning == true                               ||
                   relay.IsBusy                                             ||
                   !GameState.IsLoggedIn
               ))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
                autoBroadcast.Start();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(!autoBroadcast.IsRunning))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
                autoBroadcast.Stop();
        }

        if (autoBroadcast.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(autoBroadcast.Status);
        }
    }

    private void DrawOverlayMonsterCard(NotoriousMonsterList train, NotoriousMonsterFound info, bool showPosition)
    {
        using var group = ImRaii.Group();

        using (FontManager.Instance().UIFont120.Push())
        {
            ImGui.TextColored
            (
                info.IsAlive ?
                    KnownColor.LightGreen.ToVector4() :
                    KnownColor.Red.ToVector4(),
                info.GetBNPCName()
            );
        }

        ImGui.SameLine();
        DrawMonsterStatusToggle(train, info);

        ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));

        if (!showPosition)
            return;

        DrawPositionColumn(info);
        DrawAetheryteColumn(info);
    }

    private void DrawOverlayRelayButton(RelayType type, float cardHeight)
    {
        var label = type == RelayType.BeforeStart ?
                        "AutoMarksFinder-RelayTrainStart" :
                        "AutoMarksFinder-RelayTrainEnd";
        var pattern = config.AutoRelayAfterEndPattern;

        if (SelectedTrain is { MonstersFound.Count: > 0 } train &&
            train.MonstersFound.Any(monster => monster.IsAlive))
        {
            pattern = config.RelayPositionPattern;
            if (type == RelayType.BeforeStart &&
                train.MonstersFound.All(monster => monster.IsAlive))
                pattern = config.AutoRelayBeforeStartPattern;
        }

        var text  = Lang.Get(label);
        var style = ImGui.GetStyle();
        var buttonWidth = ImGui.CalcTextSize(FontAwesomeIcon.Microphone.ToIconString()).X +
                          ImGui.CalcTextSize(text).X                                      +
                          (style.FramePadding.X * 2)                                      +
                          style.ItemSpacing.X;
        ImGuiOm.CenterAlignFor(buttonWidth);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0, (cardHeight - ImGui.GetFrameHeight()) / 2));

        using var disabled = ImRaii.Disabled
        (
            relay.IsBusy                    ||
            autoBroadcast.IsRunning         ||
            config.RelayCommands.Count == 0 ||
            string.IsNullOrWhiteSpace(pattern)
        );

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Microphone, text))
            relay.Send(type, SelectedTrain);
    }
}
