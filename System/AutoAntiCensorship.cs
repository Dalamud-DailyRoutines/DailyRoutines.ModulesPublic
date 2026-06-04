using System.Runtime.InteropServices;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using TinyPinyin;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAntiCensorship : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动反屏蔽词",
        Description = "发送消息/编辑招募描述时, 自动在屏蔽词内部加点, 或是将其转成拼音以防止屏蔽\n接收消息时, 自动阻止屏蔽词系统工作, 显示消息原文",
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true, CNDefaultEnabled = true };

    private static readonly CompSig GetFilteredUtf8StringSig = new("48 89 74 24 ?? 57 48 83 EC ?? 48 83 79 ?? ?? 48 8B FA 48 8B F1 0F 84 ?? ?? ?? ?? 48 89 5C 24");
    private delegate void GetFilteredUtf8StringDelegate(nint vulgarInstance, Utf8String* str);
    private GetFilteredUtf8StringDelegate? GetFilteredUtf8String;

    private static readonly CompSig VulgarInstanceOffsetBaseSig = new("48 8B 81 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B D3");
    private                 nint    VulgarInstanceOffset;

    private static readonly CompSig PartyFinderOriginalMessageOffsetBaseSig = new("48 8D 99 ?? ?? ?? ?? 48 8B F9 48 8B CB E8 ?? ?? ?? ?? 48 8B 0D");
    private                 nint    PartyFinderOriginalMessageOffset;
    
    private static readonly CompSig LocalMessageDisplaySig = new("40 53 48 83 EC ?? 48 8D 99 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B 0D");
    private delegate Utf8String* LocalMessageDisplayDelegate(nint a1, Utf8String* source);
    private Hook<LocalMessageDisplayDelegate>? LocalMessageDisplayHook;

    private static readonly CompSig PartyFinderMessageDisplaySig = new("48 89 5C 24 ?? 57 48 83 EC ?? 48 8D 99 ?? ?? ?? ?? 48 8B F9 48 8B CB E8");
    private delegate        Utf8String* PartyFinderMessageDisplayDelegate(nint a1, Utf8String* source);
    private                 Hook<PartyFinderMessageDisplayDelegate>? PartyFinderMessageDisplayHook;

    private static readonly CompSig LookingForGroupConditionReceiveEventSig = new
    (
        "E8 ?? ?? ?? ?? 0F B6 F8 E9 ?? ?? ?? ?? 45 8B C2 48 8B D6 48 8B CB E8 ?? ?? ?? ?? 0F B6 F8 E9 ?? ?? ?? ?? 45 8B C2 48 8B D6 48 8B CB E8 ?? ?? ?? ?? 0F B6 F8 E9 ?? ?? ?? ?? 48 8B CE"
    );
    private delegate byte LookingForGroupConditionReceiveEventDelegate(nint a1, AtkValue* a2);
    private Hook<LookingForGroupConditionReceiveEventDelegate>? LookingForGroupConditionReceiveEventHook;

    private static readonly CompSig TextInputReceiveEventSig =
        new("4C 8B DC 55 53 57 41 54 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 9D");
    private delegate void TextInputReceiveDelegate
    (
        AtkComponentTextInput* textInput,
        AtkEventType           eventType,
        int                    eventParam,
        AtkEvent*              atkEvent,
        AtkEventData*          atkEventData
    );
    private Hook<TextInputReceiveDelegate>? TextInputReceiveEventHook;

    private Config config = null!;

    protected override void Init()
    {
        config ??= Config.Load(this) ?? new();

        // mov rax, [rcx + XXXX], 因为是四字节所以用 uint
        if (VulgarInstanceOffset == nint.Zero)
            VulgarInstanceOffset = VulgarInstanceOffsetBaseSig.GetStatic();
        DLog.Debug($"[{nameof(AutoAntiCensorship)}] 屏蔽词系统偏移量: {VulgarInstanceOffset}");

        // lea rbx, [rcx+XXXX], 因为是四字节所以用 uint
        if (PartyFinderOriginalMessageOffset == nint.Zero)
            PartyFinderOriginalMessageOffset = PartyFinderOriginalMessageOffsetBaseSig.GetStatic();
        DLog.Debug($"[{nameof(AutoAntiCensorship)}] 招募信息原始字符串偏移量: {PartyFinderOriginalMessageOffset}");

        GetFilteredUtf8String ??= GetFilteredUtf8StringSig.GetDelegate<GetFilteredUtf8StringDelegate>();

        LocalMessageDisplayHook ??= LocalMessageDisplaySig.GetHook<LocalMessageDisplayDelegate>(LocalMessageDisplayDetour);
        LocalMessageDisplayHook.Enable();

        TextInputReceiveEventHook ??= TextInputReceiveEventSig.GetHook<TextInputReceiveDelegate>(TextInputReceiveEventDetour);
        TextInputReceiveEventHook.Enable();

        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);

        PartyFinderMessageDisplayHook ??= PartyFinderMessageDisplaySig.GetHook<PartyFinderMessageDisplayDelegate>(PartyFinderMessageDisplayDetour);
        PartyFinderMessageDisplayHook.Enable();

        LookingForGroupConditionReceiveEventHook ??=
            LookingForGroupConditionReceiveEventSig.GetHook<LookingForGroupConditionReceiveEventDelegate>(LookingForGroupConditionReceiveEventDetour);
        LookingForGroupConditionReceiveEventHook.Enable();
    }

    protected override void Uninit() =>
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox("显示屏蔽文本", ref config.DisplayCensoredText))
            config.Save(this);
        ImGuiOm.HelpMarker("接收到含屏蔽词的文本时, 自动将其还原为原始文本, 并高亮显示其中包含的屏蔽文本");

        if (config.DisplayCensoredText)
        {
            using var indent = ImRaii.PushIndent();

            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            ImGui.InputInt("高亮颜色###HighlightColorInput", ref config.HighlightColor, 1, 1);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
            ImGuiOm.TooltipHover("设置为 -1 以禁用高亮");

            if (config.HighlightColor >= 0)
            {
                if (!LuminaGetter.TryGetRow<UIColor>((uint)config.HighlightColor, out var unitColorRow))
                {
                    config.HighlightColor = 17;
                    config.Save(this);
                    return;
                }

                ImGui.SameLine();
                ImGui.ColorButton("###HighlightColorPreview", unitColorRow.ToVector4());
            }

            ImGui.SameLine(0, 8f * GlobalUIScale);
            if (ImGui.Button($"{FontAwesomeIcon.Palette.ToIconChar()} 参考颜色表"))
                ChatManager.Instance().SendCommand("/xldata uicolor");
        }

        ImGui.NewLine();

        if (ImGui.Checkbox("处理屏蔽文本", ref config.HandleCensoredText))
            config.Save(this);
        ImGuiOm.HelpMarker("在发送聊天消息 / 编辑招募留言时, 自动检测其中可能包含的屏蔽文本, 并将其自动处理为未被屏蔽的文本");

        if (config.HandleCensoredText)
        {
            using var indent = ImRaii.PushIndent();

            var seperator = config.Seperator.ToString();
            ImGui.SetNextItemWidth(150f * GlobalUIScale);

            if (ImGui.InputText("分隔符###SeperatorInput", ref seperator, 4))
            {
                seperator = seperator.Trim();

                // 我觉得真有人会输入 * 号来看看会发生什么
                if (string.IsNullOrWhiteSpace(seperator) || seperator == "*")
                    seperator = ".";

                config.Seperator = seperator[0];
                config.Save(this);
            }

            ImGui.NewLine();

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("自定义替换规则");

            ImGui.SameLine();

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, "添加"))
            {
                config.CustomReplacements[string.Empty] = string.Empty;
                config.Save(this);
            }

            ImGui.Spacing();

            if (config.CustomReplacements.Count > 0)
            {
                using var table = ImRaii.Table("###CustomReplacementsTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);

                if (table)
                {
                    ImGui.TableSetupColumn("原始文本", ImGuiTableColumnFlags.WidthStretch, 0.3f);
                    ImGui.TableSetupColumn("替换文本", ImGuiTableColumnFlags.WidthStretch, 0.3f);
                    ImGui.TableSetupColumn("状态",   ImGuiTableColumnFlags.WidthStretch, 0.2f);
                    ImGui.TableSetupColumn("操作",   ImGuiTableColumnFlags.WidthFixed,   80f * GlobalUIScale);

                    ImGui.TableHeadersRow();

                    var counter = 0;

                    foreach (var (originalWord, replacement) in config.CustomReplacements.ToList())
                    {
                        using var id = ImRaii.PushId(counter);
                        counter++;

                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        var originalWordInput = originalWord;
                        ImGui.SetNextItemWidth(-1);
                        ImGui.InputText("###Original", ref originalWordInput, 256);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                        {
                            if (string.IsNullOrWhiteSpace(originalWord))
                                config.CustomReplacements.Remove(originalWord);
                            config.CustomReplacements[originalWordInput] = replacement;
                            config.Save(this);
                        }

                        ImGui.TableNextColumn();
                        var replacementInput = replacement;
                        ImGui.SetNextItemWidth(-1);
                        ImGui.InputText("###Replacement", ref replacementInput, 256);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                        {
                            config.CustomReplacements[originalWord] = replacementInput;
                            config.Save(this);
                        }

                        ImGui.TableNextColumn();

                        switch (string.IsNullOrWhiteSpace(replacement))
                        {
                            case false when ValidateCustomReplacement(replacement):
                                ImGui.TextColored(KnownColor.GreenYellow.ToVector4(), "有效");
                                break;
                            case false:
                                ImGui.TextColored(KnownColor.Red.ToVector4(), "无效");
                                ImGuiOm.TooltipHover($"替换词包含屏蔽内容:\n{replacement}: {GetFilteredString(replacement)}");
                                break;
                            default:
                                ImGui.TextColored(KnownColor.Gray.ToVector4(), "无");
                                break;
                        }

                        ImGui.TableNextColumn();

                        if (ImGuiOm.ButtonIcon($"Delete_{originalWord.GetHashCode()}", FontAwesomeIcon.TrashAlt, "删除"))
                        {
                            config.CustomReplacements.Remove(originalWord);
                            config.Save(this);
                        }
                    }
                }
            }
        }
    }

    #region 事件

    // 聊天消息编辑
    private void TextInputReceiveEventDetour
    (
        AtkComponentTextInput* textInput,
        AtkEventType           eventType,
        int                    eventParam,
        AtkEvent*              atkEvent,
        AtkEventData*          atkEventData
    )
    {
        TextInputReceiveEventHook.Original(textInput, eventType, eventParam, atkEvent, atkEventData);

        if (!config.HandleCensoredText || eventType != AtkEventType.FocusStop || textInput == null) return;

        var addon = textInput->OwnerAddon;
        if (addon == null)
            addon = textInput->ContainingAddon2;
        if (addon == null)
            addon = RaptureAtkUnitManager.Instance()->GetAddonByNode((AtkResNode*)textInput->OwnerNode);

        if (addon == null || addon->NameString != "ChatLog") return;

        var origText = new ReadOnlySeString(textInput->EvaluatedString);
        if (origText.IsEmpty) return;

        var handleText = new ReadOnlySeString(origText.AsSpan());
        BypassCensorship(ref handleText);

        if (handleText.ToString() == origText.ToString()) return;

        NotifyBypassResult(origText, handleText);
        textInput->SetText(handleText);
    }

    // 消息发送
    private void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        if (!config.HandleCensoredText || string.IsNullOrWhiteSpace(message.ToString())) return;
        BypassCensorship(ref message);
    }

    // 编辑招募
    private byte LookingForGroupConditionReceiveEventDetour(nint a1, AtkValue* values)
    {
        if (!config.HandleCensoredText) return InvokeOriginal();

        try
        {
            if (values == null || values->Int != 15) return InvokeOriginal();

            var managedString = values[1].String;
            if (managedString.Value == null) return InvokeOriginal();

            var origText = managedString.AsReadOnlySeString();
            if (origText.IsEmpty || string.IsNullOrWhiteSpace(origText.ToString())) return InvokeOriginal();

            var handleText = new ReadOnlySeString(origText.AsSpan());
            BypassCensorship(ref handleText);

            if (handleText.ToString() == origText.ToString()) return InvokeOriginal();

            values[1].SetManagedString(handleText);

            var textInputComponent = (AtkComponentTextInput*)LookingForGroupCondition->GetComponentByNodeId(22);
            if (textInputComponent != null)
                textInputComponent->SetText(handleText);

            NotifyBypassResult(origText, handleText);
        }
        catch
        {
            // ignored
        }

        return InvokeOriginal();

        byte InvokeOriginal() => LookingForGroupConditionReceiveEventHook.Original(a1, values);
    }

    // 聊天信息显示
    private Utf8String* LocalMessageDisplayDetour(nint a1, Utf8String* source)
    {
        if (!config.DisplayCensoredText)
            return LocalMessageDisplayHook.Original(a1, source);

        var seString = source->StringPtr.AsReadOnlySeString();
        HighlightCensorship(ref seString);

        source->SetString(seString);
        var target = (Utf8String*)(a1 + 1096);
        target->Copy(source);
        return target;
    }

    // 招募信息显示
    private Utf8String* PartyFinderMessageDisplayDetour(nint a1, Utf8String* source)
    {
        if (!config.DisplayCensoredText)
            return PartyFinderMessageDisplayHook.Original(a1, source);

        var seString = source->StringPtr.AsReadOnlySeString();
        HighlightCensorship(ref seString);

        source->SetString(seString);
        var target = (Utf8String*)(a1 + PartyFinderOriginalMessageOffset);
        target->Copy(source);
        return target;
    }

    #endregion

    private void NotifyBypassResult(ReadOnlySeString original, ReadOnlySeString handled)
    {
        var highlighted = new ReadOnlySeString(original.AsSpan());
        HighlightCensorship(ref highlighted);

        using var rented = new RentedSeStringBuilder();
        NotifyHelper.Instance().Chat
        (
            rented.Builder
                .PushColorType(28)
                .Append("[自动反屏蔽词]")
                .PopColorType()
                .AppendNewLine()
                .Append(highlighted)
                .AppendNewLine()
                .Append("   ↓   ")
                .AppendNewLine()
                .Append(handled)
                .ToReadOnlySeString()
        );
    }

    private void BypassCensorship(ref ReadOnlySeString seString)
    {
        var text = seString.ToString();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('/')) return;

        using var rented = new RentedSeStringBuilder();
        var builder = rented.Builder;

        foreach (var payload in seString)
        {
            // 不处理非文本
            if (payload.Type != ReadOnlySePayloadType.Text)
            {
                builder.Append(payload);
                continue;
            }

            // payload.ToString() 返回宏字符串格式（\< 代替 <），会导致反斜杠累积
            // 需通过原始字节构造临时 ReadOnlySeString 来获取无转义的原始文本
            var payloadText = new ReadOnlySeString(payload.AsSpan().Body).ToString();
            if (string.IsNullOrEmpty(payloadText.Replace('*', ' ').Trim())) continue;
            
            builder.Append(BypassCensorship(payloadText));
        }

        seString = builder.ToReadOnlySeString();
    }

    private string BypassCensorship(string originalText)
    {
        if (string.IsNullOrEmpty(originalText)) return originalText;

        var result = ApplyCustomReplacements(originalText);
        var filtered = GetFilteredString(result);

        // 记录已处理过的文本, 防止无限循环
        var processedTexts = new HashSet<string>();

        while (filtered != result && processedTexts.Add(result))
        {
            var newResult = new StringBuilder();
            var resultRunes = result.EnumerateRunes().ToList();
            var filteredRunes = filtered.EnumerateRunes().ToList();

            var (i, j) = (0, 0);
            var insideTag = false;

            while (i < resultRunes.Count)
            {
                var resultRune = resultRunes[i];

                if (resultRune.Value == '<') insideTag = true;

                if (insideTag)
                {
                    newResult.Append(resultRune.ToString());
                    if (j < filteredRunes.Count && filteredRunes[j] == resultRune) j++;
                    if (resultRune.Value == '>') insideTag = false;
                    i++;
                    continue;
                }

                Rune? filteredRune = j < filteredRunes.Count ? filteredRunes[j] : null;

                if (filteredRune.HasValue && filteredRune.Value == resultRune)
                {
                    newResult.Append(resultRune.ToString());
                    i++;
                    j++;
                }
                else if (filteredRune is { Value: '*' })
                {
                    var nextClearFilteredIndex = j;
                    while (nextClearFilteredIndex < filteredRunes.Count && filteredRunes[nextClearFilteredIndex].Value == '*')
                        nextClearFilteredIndex++;

                    if (nextClearFilteredIndex >= filteredRunes.Count)
                    {
                        var count = resultRunes.Count - i;
                        var sb = new StringBuilder(count);
                        for (var k = 0; k < count; k++)
                            sb.Append(resultRunes[i + k].ToString());
                        var censoredWord = sb.ToString();

                        ProcessCensoredWord(newResult, censoredWord);
                        i = resultRunes.Count;
                        j = filteredRunes.Count;
                    }
                    else
                    {
                        var anchorRune = filteredRunes[nextClearFilteredIndex];
                        var nextClearResultIndex = -1;

                        for (var idx = i; idx < resultRunes.Count; idx++)
                        {
                            if (resultRunes[idx] == anchorRune)
                            {
                                nextClearResultIndex = idx;
                                break;
                            }
                        }

                        if (nextClearResultIndex >= 0)
                        {
                            var count = nextClearResultIndex - i;
                            var sb = new StringBuilder(count);
                            for (var k = 0; k < count; k++)
                                sb.Append(resultRunes[i + k].ToString());
                            var censoredWord = sb.ToString();

                            ProcessCensoredWord(newResult, censoredWord);
                            i = nextClearResultIndex;
                            j = nextClearFilteredIndex;
                        }
                        else
                        {
                            newResult.Append(resultRune.ToString());
                            i++;
                            j++;
                        }
                    }
                }
                else
                {
                    newResult.Append(resultRune.ToString());
                    i++;
                    if (j < filteredRunes.Count) j++;
                }
            }

            result = newResult.ToString();
            filtered = GetFilteredString(result);
        }

        return result;
    }

    private void ProcessCensoredWord(StringBuilder builder, string censoredWord)
    {
        var censoredRunes = censoredWord.EnumerateRunes().ToList();

        // 单个中文字符转拼音
        if (censoredRunes.Count == 1 && string.IsChineseRune(censoredRunes[0]))
        {
            builder.Append(PinyinHelper.GetPinyin(censoredWord).ToLowerInvariant());
            return;
        }

        // 其他内容加分隔符
        for (var j = 0; j < censoredRunes.Count; j++)
        {
            builder.Append(censoredRunes[j].ToString());
            if (j < censoredRunes.Count - 1)
                builder.Append(config.Seperator);
        }
    }

    private void HighlightCensorship(ref ReadOnlySeString seString)
    {
        var text = seString.ToString();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('/')) return;

        using var rented = new RentedSeStringBuilder();
        var builder = rented.Builder;

        foreach (var payload in seString)
        {
            // 不处理非文本
            if (payload.Type != ReadOnlySePayloadType.Text)
            {
                builder.Append(payload);
                continue;
            }

            // payload.ToString() 返回宏字符串格式（\< 代替 <），会导致反斜杠累积
            var payloadText = new ReadOnlySeString(payload.AsSpan().Body).ToString();
            if (string.IsNullOrEmpty(payloadText.Replace('*', ' ').Trim())) continue;

            builder.Append(HighlightCensorship(payloadText));
        }

        seString = builder.ToReadOnlySeString();
    }

    private ReadOnlySeString HighlightCensorship(string originalText)
    {
        if (config.HighlightColor < 0 || string.IsNullOrEmpty(originalText)) return originalText;

        var filtered = GetFilteredString(originalText);

        // 如果没有被屏蔽的内容, 直接返回原文
        if (filtered == originalText) return originalText;

        using var rented = new RentedSeStringBuilder();
        var builder = rented.Builder;

        var (insideTag, insideCensored) = (false, false);

        for (var i = 0; i < originalText.Length; i++)
        {
            var currentChar = originalText[i];

            // 检查是否进入或离开标签
            if (currentChar == '<') insideTag = true;

            if (insideTag)
            {
                builder.Append(currentChar.ToString());
                if (currentChar == '>') insideTag = false;
                continue;
            }

            // 处理非标签内容
            var isCensoredChar = i < filtered.Length && filtered[i] == '*' && currentChar != '*';

            if (isCensoredChar && !insideCensored)
            {
                // 屏蔽词开始, 添加染色
                builder.PushColorType((ushort)config.HighlightColor);
                insideCensored = true;
            }
            else if (!isCensoredChar && insideCensored)
            {
                // 屏蔽词结束, 结束染色
                builder.PopColorType();
                insideCensored = false;
            }

            builder.Append(currentChar.ToString());
        }

        // 字符串结束了仍然在屏蔽词里, 结束染色
        if (insideCensored) builder.PopColorType();

        return builder.ToReadOnlySeString();
    }

    private string GetFilteredString(string str)
    {
        var utf8String = Utf8String.FromString(str);
        GetFilteredUtf8String(Marshal.ReadIntPtr((nint)Framework.Instance() + VulgarInstanceOffset), utf8String);
        var result = utf8String->ToString();

        utf8String->Dtor(true);
        return result;
    }

    private string ApplyCustomReplacements(string text)
    {
        if (string.IsNullOrEmpty(text) || config.CustomReplacements.Count == 0) return text;

        var result = text;
        var sortedReplacements = config.CustomReplacements
                                       .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) &&
                                                     !string.IsNullOrWhiteSpace(kvp.Value) &&
                                                     ValidateCustomReplacement(kvp.Value)) // 只使用有效的替换词
                                       .OrderByDescending(kvp => kvp.Key.Length);

        foreach (var (originalWord, replacement) in sortedReplacements)
        {
            if (result.Contains(originalWord))
                result = result.Replace(originalWord, replacement);
        }

        return result;
    }

    private bool ValidateCustomReplacement(string replacement) => 
        !string.IsNullOrWhiteSpace(replacement) && GetFilteredString(replacement) == replacement;

    private class Config : ModuleConfig
    {
        public Dictionary<string, string> CustomReplacements  = new();
        public bool                       DisplayCensoredText = true;
        public bool                       HandleCensoredText  = true;
        public int                        HighlightColor      = 17;

        public char Seperator = '.';
    }
}
