using DailyRoutines.Common.RemoteInteraction.Helpers;
using DailyRoutines.RemoteInteraction.PlayerInfo;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class OptimizedFriendList
{
    private static void ReplaceAtkString
    (
        int              index,
        ReadOnlySeString str
    )
    {
        using var utf8String = new Utf8String(str);
        AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->SetValue(index, utf8String.StringPtr);
    }

    private void ApplyDisplayModification
    (
        TaskHelper? taskHelper
    )
    {
        var addon = FriendList;
        if (!addon->IsAddonAndNodesReady()) return;

        var info = InfoProxyFriendList.Instance();

        var isAnyUpdate = false;

        for (var i = 0; i < info->EntryCount; i++)
        {
            var data = info->CharDataSpan[i];

            var existedName = AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[0 + (5 * i)].ToString();

            if (existedName == LuminaWrapper.GetAddonText(964))
            {
                isAnyUpdate = true;
                RestoreEntryData(i, data.ContentId, taskHelper);
            }

            if (!config.PlayerInfos.TryGetValue(data.ContentId, out var configInfo)) continue;

            if (!string.IsNullOrWhiteSpace(configInfo.Nickname) && existedName != configInfo.Nickname)
            {
                isAnyUpdate = true;

                using var nicknameBuilder = new RentedSeStringBuilder();
                nicknameBuilder.Builder
                               .PushColorType(37)
                               .Append($"{configInfo.Nickname}")
                               .PopColorType();

                // 名字
                ReplaceAtkString(0 + (5 * i), nicknameBuilder.Builder.ToReadOnlySeString());
            }

            var existedRemark = AtkStage.Instance()->GetStringArrayData(StringArrayType.FriendList)->StringArray[3 + (5 * i)].ToString();

            if (!string.IsNullOrWhiteSpace(configInfo.Remark))
            {
                var remarkText = $"{LuminaWrapper.GetAddonText(13294).TrimEnd(':')}: {configInfo.Remark}" +
                                 (string.IsNullOrWhiteSpace(configInfo.Nickname) ?
                                      string.Empty :
                                      $"\n{LuminaWrapper.GetAddonText(9818)}: {data.NameString}");

                if (remarkText == existedRemark) continue;
                isAnyUpdate = true;

                // 在线状态
                ReplaceAtkString(3 + (5 * i), remarkText);
            }
        }

        if (!isAnyUpdate || taskHelper == null) return;

        RequestInfoUpdate(taskHelper);
    }

    private static void RequestInfoUpdate
    (
        TaskHelper taskHelper
    )
    {
        taskHelper.Abort();

        if (FriendList == null) return;

        taskHelper.Enqueue
        (() =>
            {
                if (FriendList == null) return;
                FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
            }
        );
        taskHelper.DelayNext(100);
        taskHelper.Enqueue
        (() =>
            {
                if (FriendList == null) return;
                FriendList->OnRequestedUpdate(AtkStage.Instance()->GetNumberArrayData(), AtkStage.Instance()->GetStringArrayData());
            }
        );
    }

    private bool MatchesSearch
    (
        string filter
    )
    {
        if (string.IsNullOrWhiteSpace(searchString))
            return true;

        if (string.IsNullOrWhiteSpace(filter))
            return false;

        if (searchString.StartsWith('^'))
            return filter.StartsWith(searchString[1..], StringComparison.InvariantCultureIgnoreCase);

        if (searchString.EndsWith('$'))
            return filter.EndsWith(searchString[..^1], StringComparison.InvariantCultureIgnoreCase);

        return filter.Contains(searchString, StringComparison.InvariantCultureIgnoreCase);
    }

    protected void ApplySearchFilter
    (
        string      filter,
        TaskHelper? taskHelper
    )
    {
        var info = InfoProxyFriendList.Instance();

        if (string.IsNullOrWhiteSpace(filter))
        {
            info->ApplyFilters();
            return;
        }

        var resets           = new Dictionary<ulong, uint>();
        var resetFilterGroup = info->FilterGroup;
        info->FilterGroup = InfoProxyCommonList.DisplayGroup.None;

        var entryCount = info->GetEntryCount();

        for (var i = 0; i < entryCount; i++)
        {
            var entry = info->GetEntry((uint)i);
            if (entry == null) continue;

            var data = info->CharDataSpan[i];
            resets.Add(entry->ContentId, entry->ExtraFlags);

            if (config.IgnoredGroup[(int)entry->Group])
            {
                entry->ExtraFlags = (entry->ExtraFlags & 0xFFFF) | ((uint)(1 & 0xFF) << 16); // 添加隐藏标记
                continue;
            }

            var        matchResult = false;
            PlayerInfo configInfo  = null;

            if (config.SearchName)
            {
                var entryNameString = entry->NameString;
                if (string.IsNullOrEmpty(entry->NameString)) // 搜索会导致非本大区角色被重新刷新为（无法获得角色情报） 需要重新配置
                    RestoreEntryData(i, data.ContentId, taskHelper, name => entryNameString = name);

                matchResult |= MatchesSearch(entryNameString);
            }

            if (config.SearchNickname)
            {
                if (config.PlayerInfos.TryGetValue(data.ContentId, out configInfo))
                    matchResult |= MatchesSearch(configInfo.Nickname);
            }

            if (config.SearchRemark)
            {
                if (config.PlayerInfos.TryGetValue(data.ContentId, out configInfo))
                    matchResult |= MatchesSearch(configInfo.Remark);
            }

            if ((resetFilterGroup == InfoProxyCommonList.DisplayGroup.All || entry->Group == resetFilterGroup) && matchResult)
                entry->ExtraFlags &= 0xFFFF; // 去除隐藏标记
            else
                entry->ExtraFlags = (entry->ExtraFlags & 0xFFFF) | ((uint)(1 & 0xFF) << 16);
        }

        info->ApplyFilters();
        info->FilterGroup = resetFilterGroup;

        foreach (var pair in resets)
        {
            var entry = info->GetEntryByContentId(pair.Key);
            entry->ExtraFlags = pair.Value;
        }
    }

    private void RestoreEntryData
    (
        int             index,
        ulong           contentID,
        TaskHelper?     taskHelper,
        Action<string>? onNameResolved = null
    )
    {
        var region = WorldRegionResolver.Resolve(GameState.HomeWorld);
        _ = RemotePlayerInfo.GetOrRequest(contentID, region);

        var observer = RemotePlayerInfo.Observe
        (
            contentID,
            region,
            snapshot =>
            {
                if (!snapshot.HasValue || snapshot.Value is not { } playerInfo)
                    return;

                if (FriendList == null) return;

                using var nameBuilder = new RentedSeStringBuilder();
                nameBuilder.Builder
                           .PushColorType(32)
                           .Append(playerInfo.Name)
                           .PopColorType();

                ReplaceAtkString(0 + (5 * index), nameBuilder.Builder.ToReadOnlySeString());

                using var worldBuilder = new RentedSeStringBuilder();
                worldBuilder.Builder
                            .Append(LuminaWrapper.GetWorldName(playerInfo.WorldID))
                            .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                            .Append(LuminaWrapper.GetWorldDCName(playerInfo.WorldID));

                ReplaceAtkString(1 + (5 * index), worldBuilder.Builder.ToReadOnlySeString());

                ReplaceAtkString(3 + (5 * index), LuminaWrapper.GetAddonText(1351));

                onNameResolved?.Invoke(playerInfo.Name);

                if (taskHelper != null)
                    RequestInfoUpdate(taskHelper);
            }
        );
        infoTokens.Add(observer);
    }
}
