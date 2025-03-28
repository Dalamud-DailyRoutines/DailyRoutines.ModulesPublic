using DailyRoutines.Abstracts;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Component.Shell;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using TinyPinyin;

namespace DailyRoutines.Modules;

public unsafe class AutoAntiCensorship : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = "自动反屏蔽词",
        Description = "发送消息/编辑招募描述时, 自动在屏蔽词内部加点, 或是将其转成拼音以防止屏蔽\n接收消息时, 自动阻止屏蔽词系统工作, 显示消息原文",
        Category    = ModuleCategories.System,
    };

    public override ModulePermission Permission => new() { CNOnly = true };

    private static readonly CompSig GetFilteredUtf8StringSig =
        new("48 89 74 24 ?? 57 48 83 EC ?? 48 83 79 ?? ?? 48 8B FA 48 8B F1 0F 84");
    private delegate void GetFilteredUtf8StringDelegate(nint vulgarInstance, Utf8String* str);
    private static GetFilteredUtf8StringDelegate? GetFilteredUtf8String;

    private static readonly CompSig Utf8StringCopySig = new("E8 ?? ?? ?? ?? 48 8D 4C 24 ?? E8 ?? ?? ?? ?? 48 85 ED 74");
    private delegate nint Utf8StringCopyDelegate(Utf8String* target, Utf8String* source);
    private static Utf8StringCopyDelegate? Utf8StringCopy;

    private static readonly CompSig LocalMessageDisplaySig =
        new("40 53 48 83 EC ?? 48 8D 99 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B 0D");
    private delegate nint LocalMessageDisplayDelegate(nint a1, Utf8String* source);
    private static Hook<LocalMessageDisplayDelegate>? LocalMessageDisplayHook;

    private static readonly CompSig ProcessSendedChatSig =
        new("E8 ?? ?? ?? ?? FE 86 ?? ?? ?? ?? C7 86 ?? ?? ?? ?? ?? ?? ?? ??");
    private delegate void ProcessSendedChatDelegate(ShellCommandModule* commandModule, Utf8String* message, UIModule* module);
    private static Hook<ProcessSendedChatDelegate>? ProcessSendedChatHook;

    private static readonly CompSig PartyFinderMessageDisplaySig =
        new("48 89 5C 24 ?? 57 48 83 EC ?? 48 8D 99 ?? ?? ?? ?? 48 8B F9 48 8B CB E8");
    private delegate nint PartyFinderMessageDisplayDelegate(nint a1, Utf8String* source);
    private static Hook<PartyFinderMessageDisplayDelegate>? PartyFinderMessageDisplayHook;

    private static readonly CompSig LookingForGroupConditionReceiveEventSig = new("48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 2B E0 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B D9");
    private delegate byte LookingForGroupConditionReceiveEventDelegate(nint a1, AtkValue* a2);
    private static Hook<LookingForGroupConditionReceiveEventDelegate>? LookingForGroupConditionReceiveEventHook;

    private static Config ModuleConfig = null!;
    
    public override void Init()
    {
        ModuleConfig ??= LoadConfig<Config>() ?? new();
        
        GetFilteredUtf8String ??= GetFilteredUtf8StringSig.GetDelegate<GetFilteredUtf8StringDelegate>();
        Utf8StringCopy        ??= Utf8StringCopySig.GetDelegate<Utf8StringCopyDelegate>();
        
        LocalMessageDisplayHook ??= LocalMessageDisplaySig.GetHook<LocalMessageDisplayDelegate>(LocalMessageDisplayDetour);
        LocalMessageDisplayHook.Enable();
        
        ProcessSendedChatHook ??= ProcessSendedChatSig.GetHook<ProcessSendedChatDelegate>(ProcessSendedChatDetour);
        ProcessSendedChatHook.Enable();
        
        PartyFinderMessageDisplayHook ??= PartyFinderMessageDisplaySig.GetHook<PartyFinderMessageDisplayDelegate>(PartyFinderMessageDisplayDetour);
        PartyFinderMessageDisplayHook.Enable();

        LookingForGroupConditionReceiveEventHook ??=
            LookingForGroupConditionReceiveEventSig.GetHook<LookingForGroupConditionReceiveEventDelegate>(LookingForGroupConditionReceiveEventDetour);
        LookingForGroupConditionReceiveEventHook.Enable();
    }

    public override void ConfigUI()
    {
        using (ImRaii.Group())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"屏蔽词处理分隔符:");
            
            ImGui.AlignTextToFramePadding();
            ImGui.Text("屏蔽词显示高亮颜色:");
        }
        
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            var seperator = ModuleConfig.Seperator.ToString();
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            if (ImGui.InputText("###SeperatorInput", ref seperator, 1))
            {
                seperator = seperator.Trim();
                
                // 我觉得真有人会输入 * 号来看看会发生什么
                if (string.IsNullOrWhiteSpace(seperator) || seperator == "*")
                    seperator = ".";
                
                ModuleConfig.Seperator = seperator[0];
                ModuleConfig.Save(this);
            }
            
            if (!LuminaGetter.TryGetRow<UIColor>(ModuleConfig.HighlightColor, out var unitColorRow))
            {
                ModuleConfig.HighlightColor = 17;
                ModuleConfig.Save(this);
                return;
            }
            
            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            if (ImGuiOm.InputUInt("###HighlightColorInput", ref ModuleConfig.HighlightColor, 1, 1))
                SaveConfig(ModuleConfig);
            
            ImGui.SameLine();
            ImGui.ColorButton("###HighlightColorPreview", UIColorToVector4Color(unitColorRow.UIForeground));
        }
        
        var sheet = LuminaGetter.Get<UIColor>();
        using (var node = ImRaii.TreeNode("参考颜色表"))
        {
            if (node)
            {
                using (var table = ImRaii.Table("###ColorTable", 6))
                {
                    if (table)
                    {
                        var counter = 0;
                        foreach (var row in sheet)
                        {
                            if (row.RowId        == 0) continue;
                            if (row.UIForeground == 0) continue;

                            if (counter % 5 == 0) ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            
                            counter++;

                            using (ImRaii.Group())
                            {
                                ImGui.ColorButton($"###ColorButtonTable{row.RowId}", UIColorToVector4Color(row.UIForeground));
                                
                                ImGui.SameLine();
                                ImGui.Text($"{row.RowId}");
                            }
                        }
                    }
                }
            }
        }
    }
    
    
    // 编辑招募
    private static byte LookingForGroupConditionReceiveEventDetour(nint a1, AtkValue* values)
    {
        try
        {
            if (values == null || values->Int != 15)
                return InvokeOriginal();

            var managedString = values[1].String;
            if (managedString == null)
                return InvokeOriginal();

            var origText = SeString.Parse(managedString);
            if (origText == null || string.IsNullOrWhiteSpace(origText.TextValue))
                return InvokeOriginal();

            var builderHandled = new SeStringBuilder();
            foreach (var payload in origText.Payloads)
            {
                // 不处理非文本
                if (payload is not TextPayload textPayload)
                {
                    builderHandled.Add(payload);
                    continue;
                }

                BypassCensorshipByTextPayload(ref textPayload);
                builderHandled.Add(textPayload);
            }

            var handledText = builderHandled.Build();

            if (handledText.TextValue != origText.TextValue)
            {
                var builderHighlight = new SeStringBuilder();
                foreach (var payload in origText.Payloads)
                {
                    // 不处理非文本
                    if (payload is not TextPayload textPayload)
                    {
                        builderHighlight.Add(payload);
                        continue;
                    }

                    builderHighlight.Append(HighlightCensorship(textPayload.Text));
                }

                var highlightedText = builderHighlight.Build();

                values[1].SetString(*(byte**)Utf8String.FromSequence(handledText.Encode()));
                Chat(new SeStringBuilder().Append("已对招募留言进行反屏蔽处理:\n").Append(highlightedText).Append("\n↓\n").Append(handledText).Build());
            }
        }
        catch
        {
            // ignored
        }
        
        return InvokeOriginal();
        
        byte InvokeOriginal() => LookingForGroupConditionReceiveEventHook.Original(a1, values);
    }

    // 消息发送
    private static void ProcessSendedChatDetour(ShellCommandModule* commandModule, Utf8String* message, UIModule* module)
    {
        var seString = SeString.Parse(*(byte**)message);
        // 信息为空或者为指令
        if (string.IsNullOrWhiteSpace(seString.TextValue) || seString.TextValue.StartsWith('/'))
        {
            InvokeOriginal();
            return;
        }
        
        var builder = new SeStringBuilder();
        foreach (var payload in seString.Payloads)
        {
            // 不处理非文本
            if (payload is not TextPayload textPayload)
            {
                builder.Add(payload);
                continue;
            }
            
            BypassCensorshipByTextPayload(ref textPayload);
            builder.Add(textPayload);
        }
        
        message->SetString(builder.Build().Encode());
        InvokeOriginal();

        void InvokeOriginal() => ProcessSendedChatHook.Original(commandModule, message, module);
    }

    // 聊天信息显示
    private static nint LocalMessageDisplayDetour(nint a1, Utf8String* source)
    {
        var seString = SeString.Parse(*(byte**)source);
        var builder  = new SeStringBuilder();
        foreach (var payload in seString.Payloads)
        {
            // 不处理非文本
            if (payload is not TextPayload textPayload)
            {
                builder.Add(payload);
                continue;
            }
            
            var result = HighlightCensorship(textPayload.Text);
            builder.Append(result);
        }
        
        source->SetString(builder.Build().Encode());
        
        return Utf8StringCopy((Utf8String*)(a1 + 1096), source);
    }

    // 招募信息显示
    private static nint PartyFinderMessageDisplayDetour(nint a1, Utf8String* source)
    {
        var seString = SeString.Parse(*(byte**)source);
        var builder  = new SeStringBuilder();
        foreach (var payload in seString.Payloads)
        {
            // 不处理非文本
            if (payload is not TextPayload textPayload)
            {
                builder.Add(payload);
                continue;
            }
            
            var result = HighlightCensorship(textPayload.Text);
            builder.Append(result);
        }
        
        source->SetString(builder.Build().Encode());
        
        return Utf8StringCopy((Utf8String*)(a1 + 11288), source);
    }
    
    
    private static void BypassCensorshipByTextPayload(ref TextPayload payload)
    {
        // 非国服或只有星号
        if (DService.ClientState.ClientLanguage != (ClientLanguage)4 ||
            string.IsNullOrWhiteSpace(payload.Text?.Replace('*', ' ').Trim() ?? string.Empty)) 
            return;

        var bypassed = BypassCensorship(payload.Text);
        if (bypassed != payload.Text)
            payload = new TextPayload(bypassed);
    }

    public static string BypassCensorship(string originalText)
    {
        if (string.IsNullOrEmpty(originalText)) return originalText;

        var result   = originalText;
        var filtered = GetFilteredString(result);

        // 记录已处理过的文本, 防止无限循环
        var processedTexts = new HashSet<string>();

        while (filtered != result && !processedTexts.Contains(result))
        {
            processedTexts.Add(result);
            var newResult = new StringBuilder();

            // 跳过 <> 标签内容
            var insideTag = false;

            for (var i = 0; i < result.Length; i++)
            {
                // 检查是否进入或离开标签
                if (result[i] == '<')
                    insideTag = true;

                if (insideTag)
                {
                    newResult.Append(result[i]);
                    if (result[i] == '>') insideTag = false;

                    continue;
                }

                // 处理非标签内容
                if (i < filtered.Length && filtered[i] == '*' && result[i] != '*')
                {
                    // 找出连续被屏蔽的部分
                    var startPos = i;
                    while (i + 1 < filtered.Length && filtered[i + 1] == '*' && result[i + 1] != '*') i++;

                    // 截取被屏蔽的词
                    var censoredWord = result.Substring(startPos, i - startPos + 1);

                    if (censoredWord.Length == 1 && IsChineseCharacter(censoredWord[0]))
                    {
                        // 单个汉字转拼音
                        newResult.Append(PinyinHelper.GetPinyin(censoredWord).ToLowerInvariant());
                    }
                    else if (IsChineseString(censoredWord))
                    {
                        // 汉字词组加分隔符
                        for (var j = 0; j < censoredWord.Length; j++)
                        {
                            newResult.Append(censoredWord[j]);
                            if (j < censoredWord.Length - 1) newResult.Append(ModuleConfig.Seperator);
                        }
                    }
                    else
                    {
                        // 其他内容加分隔符
                        for (var j = 0; j < censoredWord.Length; j++)
                        {
                            newResult.Append(censoredWord[j]);
                            if (j < censoredWord.Length - 1) newResult.Append(ModuleConfig.Seperator);
                        }
                    }
                }
                else
                {
                    // 未被屏蔽的部分保持原样
                    newResult.Append(result[i]);
                }
            }

            result   = newResult.ToString();
            filtered = GetFilteredString(result);
        }

        return result;
    }
    
    public static SeString HighlightCensorship(string originalText)
    {
        if (string.IsNullOrEmpty(originalText)) return originalText;

        var filtered = GetFilteredString(originalText);
        
        // 如果没有被屏蔽的内容，直接返回原文
        if (filtered == originalText) return originalText;
        
        var result = new SeStringBuilder();
        
        var insideTag = false;
        var insideCensored = false;
        
        for (var i = 0; i < originalText.Length; i++)
        {
            // 检查是否进入或离开标签
            if (originalText[i] == '<')
                insideTag = true;
            
            if (insideTag)
            {
                result.Append(originalText[i].ToString());
                if (originalText[i] == '>') insideTag = false;
                continue;
            }
            
            // 处理非标签内容
            if (i < filtered.Length && filtered[i] == '*' && originalText[i] != '*')
            {
                // 屏蔽词开始, 添加染色
                if (!insideCensored)
                {
                    result.Add(new UIForegroundPayload((ushort)ModuleConfig.HighlightColor));
                    insideCensored = true;
                }
                
                result.Append(originalText[i].ToString());
            }
            else
            {
                // 屏蔽词结束, 结束染色
                if (insideCensored)
                {
                    result.Add(UIForegroundPayload.UIForegroundOff);
                    insideCensored = false;
                }
                
                result.Append(originalText[i].ToString());
            }
        }
        
        // 字符串结束了仍然在屏蔽词里, 结束染色
        if (insideCensored)
            result.Add(UIForegroundPayload.UIForegroundOff);
        
        return result.Build();
    }
    
    private static string GetFilteredString(string str)
    {
        var utf8String = Utf8String.FromString(str);
        GetFilteredUtf8String(Marshal.ReadIntPtr((nint)Framework.Instance() + 0x2B48), utf8String);
        var result = utf8String->ExtractText();

        utf8String->Dtor(true);
        return result;
    }

    private class Config : ModuleConfiguration
    {
        public char Seperator = '.';
        public uint HighlightColor   = 17;
    }
}
