namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    protected override void ConfigUI()
    {
        var fieldW  = 230f                            * GlobalUIScale;
        var promptH = 200f                            * GlobalUIScale;
        var promptW = ImGui.GetContentRegionAvail().X * 0.9f;

        using var tab = ImRaii.TabBar("###Config", ImGuiTabBarFlags.Reorderable);
        if (!tab) return;

        using (var generalTab = ImRaii.TabItem(Lang.Get("General")))
        {
            if (generalTab)
                DrawGeneralTab(fieldW);
        }

        using (var apiTab = ImRaii.TabItem("API"))
        {
            if (apiTab)
                DrawAPITab(fieldW);
        }

        using (var filterTab = ImRaii.TabItem(Lang.Get("AutoReplyChatBot-FilterSettings")))
        {
            if (filterTab)
                DrawFilterTab(fieldW, promptW, promptH);
        }

        using (var systemPromptTab = ImRaii.TabItem(Lang.Get("AutoReplyChatBot-SystemPrompt")))
        {
            if (systemPromptTab)
                DrawSystemPromptTab(fieldW, promptW, promptH);
        }

        using (var worldBookTab = ImRaii.TabItem(Lang.Get("AutoReplyChatBot-WorldBook")))
        {
            if (worldBookTab)
                DrawWorldBookTab(fieldW, promptW);
        }

        using (var testChatTab = ImRaii.TabItem(Lang.Get("AutoReplyChatBot-TestChat")))
        {
            if (testChatTab)
                DrawTestChatTab();
        }

        using (var historyTab = ImRaii.TabItem(Lang.Get("AutoReplyChatBot-HistoryPreview")))
        {
            if (historyTab)
                DrawHistoryTab(fieldW, promptW, promptH);
        }

        using (var gameContextTab = ImRaii.TabItem(Lang.Get("AutoReplyChatBot-GameContext")))
        {
            if (gameContextTab)
                DrawGameContextTab();
        }
    }
}
