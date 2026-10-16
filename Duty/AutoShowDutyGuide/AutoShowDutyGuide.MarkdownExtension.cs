using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Dalamud.Game.Text;
using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using OmenTools.ImGuiOm.Markdown;
using OmenTools.ImGuiOm.Markdown.Layout;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using GameAction = Lumina.Excel.Sheets.Action;
using GameItem = Lumina.Excel.Sheets.Item;
using GameStatus = Lumina.Excel.Sheets.Status;

namespace DailyRoutines.ModulesPublic.Duty;

public partial class AutoShowDutyGuide
{
    public sealed partial class DutyGuideMarkdownExtension : InlineParser, IImGuiMarkdownExtension
    {
        private readonly Lazy<FrozenDictionary<string, GameAction>> actions = new
        (() =>
             LuminaGetter.Get<GameAction>()
                         .Where(x => x is { IsPvP: false, Icon: > 0, Name.IsEmpty: false })
                         .GroupBy(x => x.Name.ToString())
                         .ToFrozenDictionary(x => x.Key, x => x.MaxBy(row => row.ClassJobLevel > 0 || row.IsPlayerAction))
        );

        private string constructionText = string.Empty;

        public DutyGuideMarkdownExtension() => OpeningCharacters = ['<'];

        public void ConfigurePipeline
        (
            MarkdownPipelineBuilder builder
        )
        {
            builder.DisableHtml()
                   .UseYamlFrontMatter()
                   .UseGenericAttributes()
                   .UseCustomContainers()
                   .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough | EmphasisExtraOptions.Marked | EmphasisExtraOptions.Inserted);
            builder.InlineParsers.Insert(0, this);
        }

        public async Task LoadComponentsAsync
        (
            string markdown
        )
        {
            if (constructionText.Length > 0 || !markdown.Contains("<UnderConstruction", StringComparison.Ordinal))
                return;

            var source = await HTTPClientHelper.Instance().Get().GetStringAsync
                         (
                             "https://gh.atmoomen.top/raw.githubusercontent.com/thewakingsands/novice-network/refs/heads/master/src/components/content/UnderConstruction.astro"
                         );
            var document = new HtmlParser().ParseDocument(source);
            constructionText = document.QuerySelector(".ucontainer .text")?.TextContent ?? string.Empty;
        }

        public string Preprocess
        (
            string markdown
        )
        {
            using StringReader              reader     = new(markdown);
            List<(string Text, int Depth)>  lines      = [];
            Stack<(char Marker, int Depth)> containers = new();
            var                             maxDepth   = 0;
            var                             codeMarker = '\0';
            var                             codeLength = 0;

            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    var count = trimmed.TakeWhile(x => x == trimmed[0]).Count();

                    if (codeMarker == '\0')
                    {
                        codeMarker = trimmed[0];
                        codeLength = count;
                    }
                    else if (trimmed[0] == codeMarker && count >= codeLength && string.IsNullOrWhiteSpace(trimmed[count..]))
                        codeMarker = '\0';

                    lines.Add((line, 0));
                    continue;
                }

                if (codeMarker == '\0' &&
                    (line.StartsWith(";;;", StringComparison.Ordinal)                                                                              ||
                     (line.StartsWith(":::", StringComparison.Ordinal) && line[3..].TrimStart().StartsWith("collapse ", StringComparison.Ordinal)) ||
                     line == ":::"))
                {
                    var marker   = line[0];
                    var argument = line[3..].Trim();

                    switch (argument.Length)
                    {
                        case 0 when containers.TryPeek(out var previous) && previous.Marker == marker:
                            containers.Pop();
                            lines.Add((string.Empty, 0));
                            lines.Add((string.Empty, previous.Depth));
                            lines.Add((string.Empty, 0));
                            continue;
                        case > 0:
                            var depth = containers.Count + 1;
                            maxDepth = Math.Max(maxDepth, depth);
                            containers.Push((marker, depth));
                            lines.Add((string.Empty, 0));
                            lines.Add
                            (
                                (marker == ';' ?
                                     "guide " + argument :
                                     argument, depth)
                            );
                            lines.Add((string.Empty, 0));
                            continue;
                    }
                }

                lines.Add((line, 0));
            }

            while (containers.TryPop(out var container))
            {
                lines.Add((string.Empty, 0));
                lines.Add((string.Empty, container.Depth));
                lines.Add((string.Empty, 0));
            }

            StringBuilder result = new(markdown.Length);

            foreach (var (text, depth) in lines)
            {
                if (depth > 0)
                {
                    result.Append(':', maxDepth - depth + 3);
                    if (text.Length > 0)
                        result.Append(' ');
                }

                result.Append(text).Append('\n');
            }

            return result.ToString();
        }

        public override bool Match
        (
            InlineProcessor processor,
            ref StringSlice slice
        )
        {
            var match = TagPattern().Match(slice.Text, slice.Start);
            if (!match.Success || match.Index + match.Length - 1 > slice.End)
                return false;

            var tag = match.Groups["name"].Value;
            if (tag is not ("Role" or "Status" or "Action" or "Item" or "Quest" or "UnderConstruction" or "DutyNav" or
                "img" or "a" or "i" or "ins" or "iframe" or "br" or "actione"))
                return false;

            var     element = new HtmlParser().ParseDocument(match.Value).Body.FirstElementChild;
            var     end     = match.Index + match.Length;
            string? body    = null;

            if (!match.Value.EndsWith("/>", StringComparison.Ordinal) && tag is not ("img" or "br"))
            {
                var closingTag = $"</{tag}>";
                var closing    = slice.Text.IndexOf(closingTag, end, slice.End - end + 1, StringComparison.Ordinal);

                if (closing >= 0)
                {
                    body = slice.Text[end..closing];
                    end  = closing + closingTag.Length;
                }
            }

            Inline inline;

            switch (tag)
            {
                case "br":
                    inline = new LineBreakInline { IsHard = true };
                    break;
                case "ins":
                    inline = new EmphasisInline
                    {
                        DelimiterChar  = '+',
                        DelimiterCount = 2,
                        IsClosed       = true
                    }.AppendChild(new LiteralInline(body ?? string.Empty));
                    break;
                case "a":
                    inline = new LinkInline(element.GetAttribute("href"), null)
                        { IsClosed = true }.AppendChild(new LiteralInline(body ?? string.Empty));
                    break;
                case "iframe":
                    var url = element.GetAttribute("src") ?? string.Empty;
                    inline = new LinkInline(url, null) { IsClosed = true }.AppendChild(new LiteralInline(url));
                    break;
                default:
                    inline = new GuideInline(() => BuildComponent(tag, element, body));
                    break;
            }

            inline.Span = new SourceSpan
            (
                processor.GetSourcePosition(slice.Start, out var line, out var column),
                processor.GetSourcePosition(end - 1)
            );
            inline.Line      = line;
            inline.Column    = column;
            processor.Inline = inline;
            slice.Start      = end;
            return true;
        }

        public bool TryBuildInline
        (
            Inline          inline,
            InlineRun       style,
            List<InlineRun> runs
        )
        {
            if (inline is not GuideInline guide)
                return false;

            foreach (var run in guide.Runs)
            {
                runs.Add
                (
                    style with
                    {
                        Text = run.Text,
                        Role = run.Role == MarkdownFontRole.Body ?
                                   style.Role :
                                   run.Role,
                        LinkURL = run.LinkURL ?? style.LinkURL,
                        IsImage = run.IsImage,
                        IconID = run.IconID,
                        ImageSize = run.ImageSize,
                        ImageInEms = run.ImageInEms,
                        Dispel = run.Dispel,
                        Tooltip = run.Tooltip
                    }
                );
            }

            return true;
        }

        public bool TryRenderBlock
        (
            Block          block,
            MarkdownConfig config
        )
        {
            if (block is not CustomContainer container)
                return false;

            if (container.Info == "collapse")
            {
                if (ImGui.CollapsingHeader(container.Arguments + "###collapse"))
                {
                    ImGui.Spacing();
                    ImGuiMarkdown.RenderBlocks(container, config);
                }

                return true;
            }

            if (container.Info != "guide")
                return false;

            var width = ImGui.GetContentRegionAvail().X;

            if (container.Arguments.Contains(".cols2", StringComparison.Ordinal) &&
                container.Count > 1                                              &&
                width           >= ImGui.GetFontSize() * 36.0F)
            {
                using var spacing = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0.0F));

                if (ImGui.BeginTable("##guide-columns", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("##image", ImGuiTableColumnFlags.WidthFixed, MathF.Min(350.0F * GlobalUIScale, width * 0.45F));
                    ImGui.TableSetupColumn("##text",  ImGuiTableColumnFlags.WidthStretch);

                    foreach (var child in container)
                    {
                        ImGui.TableNextColumn();
                        if (child is ContainerBlock contents)
                            ImGuiMarkdown.RenderBlocks(contents, config);
                    }

                    ImGui.EndTable();
                }
            }
            else
                ImGuiMarkdown.RenderBlocks(container, config);

            return true;
        }

        private List<InlineRun> BuildComponent
        (
            string   tag,
            IElement element,
            string?  body
        )
        {
            var             name  = element.GetAttribute("name") ?? string.Empty;
            var             label = body                         ?? name;
            var             id    = ReadID(element, "id");
            List<InlineRun> runs  = [];

            switch (tag)
            {
                case "Role":
                    var icon = name switch
                    {
                        "tank"   => 62581U,
                        "healer" => 62582U,
                        "dps"    => 62583U,
                        "melee"  => 62584U,
                        "ranged" => 62585U,
                        "magic"  => 62586U,
                        _        => 0U
                    };
                    runs.Add(CreateIcon(icon, name));
                    break;
                case "Status":
                    var status = LuminaGetter.GetRow<GameStatus>(id);

                    if (status is { } statusRow)
                    {
                        var tooltip = statusRow.Name.ToString() + "\n" + statusRow.Description.ToString();
                        runs.Add
                        (
                            CreateIcon(statusRow.Icon + ReadID(element, "stack"), tooltip) with
                            {
                                Dispel = element.HasAttribute("dispel"),
                                ImageSize = new Vector2(1.3F)
                            }
                        );
                    }

                    runs.Add(new InlineRun(label, MarkdownFontRole.Bold, null, false));
                    break;
                case "Action":
                case "actione":
                    GameAction? action = null;
                    if (id > 0)
                        action = LuminaGetter.GetRow<GameAction>(id);
                    else if (actions.Value.TryGetValue(name, out var found))
                        action = found;

                    if (action is { } actionRow)
                        runs.Add(CreateIcon(actionRow.Icon, actionRow.Name.ToString()));
                    if (!element.HasAttribute("noname"))
                        runs.Add(new InlineRun(label, MarkdownFontRole.Bold, null, false));
                    break;
                case "Item":
                    var item     = LuminaGetter.Get<GameItem>().FirstOrDefault(x => x.Name.ToString() == name);
                    var itemLink = "https://ff14.huijiwiki.com/wiki/%E7%89%A9%E5%93%81:" + Uri.EscapeDataString(name);
                    runs.Add(CreateIcon(item.Icon, name) with { LinkURL = itemLink });
                    if (!element.HasAttribute("noname"))
                        runs.Add(new InlineRun(label, MarkdownFontRole.Body, itemLink, false));
                    if (element.HasAttribute("hq"))
                        runs.Add(new InlineRun(SeIconChar.HighQuality.ToIconString(), MarkdownFontRole.Body, itemLink, false));
                    break;
                case "Quest":
                    var questIcon = element.GetAttribute("type") switch
                    {
                        "main" => 61412U,
                        "plus" => 61419U,
                        _      => 61411U
                    };
                    var questLink = "https://ff14.huijiwiki.com/wiki/%E4%BB%BB%E5%8A%A1:" + Uri.EscapeDataString(name);
                    runs.Add(CreateIcon(questIcon, name) with { LinkURL = questLink });
                    runs.Add(new InlineRun(label, MarkdownFontRole.Body, questLink, false));
                    break;
                case "img":
                    var source     = element.GetAttribute("src") ?? string.Empty;
                    var width      = ReadDimension(element.GetAttribute("width"));
                    var height     = ReadDimension(element.GetAttribute("height"));
                    var styleWidth = WidthPattern().Match(element.GetAttribute("style") ?? string.Empty);
                    if (styleWidth.Success)
                        width = ReadDimension(styleWidth.Groups[1].Value);
                    var small = element.ClassList.Contains("sm-icon");
                    runs.Add
                    (
                        new InlineRun(source, MarkdownFontRole.Body, null, true)
                        {
                            ImageSize = small ?
                                            new Vector2(1.3F) :
                                            new Vector2(width, height),
                            ImageInEms = small,
                            IconID = IconPathPattern().Match(source) is { Success: true } iconPath ?
                                         uint.Parse(iconPath.Groups[1].Value, CultureInfo.InvariantCulture) :
                                         null,
                            Tooltip = element.GetAttribute("title") ?? element.GetAttribute("alt")
                        }
                    );
                    break;
                case "i":
                    if (element.ClassList.Contains("mouse-right-button"))
                        runs.Add(new InlineRun(SeIconChar.MouseRightClick.ToIconString(), MarkdownFontRole.Body, null, false));
                    break;
                case "UnderConstruction":
                    runs.Add
                    (
                        new InlineRun("/images/bg/hint.png", MarkdownFontRole.Body, null, true)
                        {
                            ImageSize  = new Vector2(3.0F),
                            ImageInEms = true
                        }
                    );
                    runs.Add(new InlineRun(constructionText, MarkdownFontRole.Body, null, false));
                    break;
                case "DutyNav":
                    runs.Add(new InlineRun("https://ff14.org/duty/", MarkdownFontRole.Body, "https://ff14.org/duty/", false));
                    break;
            }

            return runs;
        }

        private static InlineRun CreateIcon
        (
            uint   id,
            string tooltip
        ) =>
            new(string.Empty, MarkdownFontRole.Body, null, true)
            {
                IconID     = id,
                ImageSize  = Vector2.One,
                ImageInEms = true,
                Tooltip    = tooltip
            };

        private static uint ReadID
        (
            IElement element,
            string   attribute
        ) =>
            uint.TryParse(element.GetAttribute(attribute)?.Trim('{', '}', ' '), CultureInfo.InvariantCulture, out var value) ?
                value :
                0;

        private static float ReadDimension
        (
            string? value
        ) =>
            float.TryParse(value?.Replace("px", string.Empty, StringComparison.Ordinal).Trim(), CultureInfo.InvariantCulture, out var result) ?
                result :
                0.0F;

        [GeneratedRegex("""\G<(?<name>[A-Za-z][\w-]*)\b(?:"[^"]*"|'[^']*'|[^'">])*>""")]
        private static partial Regex TagPattern();

        [GeneratedRegex(@"width\s*:\s*(\d+(?:\.\d+)?)px", RegexOptions.IgnoreCase)]
        private static partial Regex WidthPattern();

        [GeneratedRegex(@"^/images/icons/(\d+)\.png$")]
        private static partial Regex IconPathPattern();

        private sealed class GuideInline
        (
            Func<IReadOnlyList<InlineRun>> buildRuns
        ) : LeafInline
        {
            private readonly Lazy<IReadOnlyList<InlineRun>> runs = new(buildRuns);

            public IReadOnlyList<InlineRun> Runs => runs.Value;
        }
    }
}
