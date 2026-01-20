namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    protected override void ConfigUI()
    {
        var fieldW  = 230f                            * GlobalFontScale;
        var promptH = 200f                            * GlobalFontScale;
        var promptW = ImGui.GetContentRegionAvail().X * 0.9f;

        using var tab = ImRaii.TabBar("###Config", ImGuiTabBarFlags.Reorderable);
        if (!tab) return;

        using (var generalTab = ImRaii.TabItem(GetLoc("General")))
            if (generalTab)
                DrawGeneralTab(fieldW);

        using (var apiTab = ImRaii.TabItem("API"))
            if (apiTab)
                DrawAPITab(fieldW);

        using (var filterTab = ImRaii.TabItem(GetLoc("AutoReplyChatBot-FilterSettings")))
            if (filterTab)
                DrawFilterTab(fieldW, promptW, promptH);

        using (var systemPromptTab = ImRaii.TabItem(GetLoc("AutoReplyChatBot-SystemPrompt")))
            if (systemPromptTab)
                DrawSystemPromptTab(fieldW, promptW, promptH);

        using (var worldBookTab = ImRaii.TabItem(GetLoc("AutoReplyChatBot-WorldBook")))
            if (worldBookTab)
                DrawWorldBookTab(fieldW, promptW);

        using (var testChatTab = ImRaii.TabItem(GetLoc("AutoReplyChatBot-TestChat")))
            if (testChatTab)
                DrawTestChatTab();

        using (var historyTab = ImRaii.TabItem(GetLoc("AutoReplyChatBot-HistoryPreview")))
            if (historyTab)
                DrawHistoryTab(fieldW, promptW, promptH);

        using (var gameContextTab = ImRaii.TabItem(GetLoc("AutoReplyChatBot-GameContext")))
            if (gameContextTab)
                DrawGameContextTab();
    }
}
