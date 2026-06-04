using Dalamud.Game.Text;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private sealed class ChatPipeline
    (
        AutoReplyChatBot module
    )
    {
        public async Task ExecuteAsync
        (
            string            playerName,
            ushort            worldID,
            string            worldName,
            XivChatType       chatType,
            string            userText,
            CancellationToken ct
        )
        {
            var cfg    = module.config;
            var target = $"{playerName}@{worldName}";

            // ── Stage 1: Gate ──
            if (!cfg.ValidChatTypes.Contains(chatType)) return;
            if (playerName == LocalPlayerState.Name    && worldID == GameState.HomeWorld) return;
            if (chatType   == XivChatType.TellIncoming && cfg.OnlyReplyNonFriendTell && IsFriend(playerName, worldID)) return;
            if (!module.rateLimiter!.CanProceed(target, cfg.CooldownSeconds)) return;

            // Layer 1: Hard Guard (代码级，零 LLM 消耗)
            var hardBlock = HardGuardCheck(userText, cfg);

            if (hardBlock != null)
            {
                DLog.Debug($"Hard Guard blocked message from {target}: {hardBlock.Value.Reason}");
                return;
            }

            // Layer 2: Filter Model (Guard LLM)
            GuardResult? guardResult = null;

            if (cfg.EnableFilter && !string.IsNullOrWhiteSpace(cfg.FilterModel))
            {
                using var filterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                filterCts.CancelAfter(TimeSpan.FromSeconds(30));

                guardResult = await FilterMessageAsync(cfg, userText, filterCts.Token).ConfigureAwait(false);

                if (guardResult == null)
                {
                    DLog.Debug($"Guard model unavailable for {target}, skipping reply");
                    return;
                }
            }

            // Enrich
            module.AppendHistory(target, "user", userText);

            var conv = module.conversationStore!.GetOrLoad(target);
            var hist = conv.RecentTurns.ToList();

            if (hist.Count == 0) return;

            // Apply guard result to user message
            if (guardResult != null && guardResult.Value.Level != GuardLevel.Safe)
            {
                var gr = guardResult.Value;

                switch (gr.Level)
                {
                    case GuardLevel.Block:
                        DLog.Debug($"Guard blocked message from {target}: {gr.Reason}");
                        return;
                    case GuardLevel.Attack when cfg.AttackBehavior == AttackAction.Silent:
                        DLog.Debug($"Guard silenced attack from {target}: {gr.Intent}");
                        return;
                    case GuardLevel.Attack:
                        userText = $"[Guard: 用户试图进行 {gr.Intent ?? "未知"} 攻击 — {gr.Reason}] {userText}";
                        break;
                    case GuardLevel.Flag:
                        userText = $"[Guard: 消息被标记为可疑 — {gr.Reason}] {userText}";
                        break;
                }

                ReplaceLastUserMessage(conv, hist, userText);
            }

            // ── Stage 3-4: Build + LLM ──
            using var ticket = await module.rateLimiter.AcquireAsync(target, ct).ConfigureAwait(false);

            var replyContext = new ReplyContext
            {
                Target          = target,
                OriginalType    = chatType,
                DefaultChannel  = chatType == XivChatType.TellIncoming ? "tell" : "say",
                ChannelCommands = ChannelCommands
            };

            var toolContext = new ToolExecutionContext
            {
                ModuleConfig      = cfg,
                ReplyContext      = replyContext,
                ConversationStore = module.conversationStore!,
                CancellationToken = ct
            };

            string? reply;

            try
            {
                reply = await module.GenerateReplyAsync(cfg, target, toolContext, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                NotifyHelper.Instance().NotificationError(Lang.Get("AutoReplyChatBot-ErrorTitle"));
                DLog.Error($"{Lang.Get("AutoReplyChatBot-ErrorTitle")}:", ex);
                reply = string.Empty;
            }

            // ── Stage 5: PostProcess (Layer 3: Post Guard) ──
            if (!string.IsNullOrWhiteSpace(reply))
            {
                if (reply.StartsWith("[ATTACK", StringComparison.Ordinal))
                {
                    DLog.Debug($"Post Guard filtered attack-prefixed reply to {target}");
                    reply = string.Empty;
                }
                else if (cfg.HardGuardEnabled)
                {
                    var postBlock = HardGuardCheck(reply, cfg);

                    if (postBlock != null)
                    {
                        DLog.Debug($"Post Guard filtered reply to {target}: {postBlock.Value.Reason}");
                        reply = string.Empty;
                    }
                }
            }

            // ── Stage 6: Dispatch ──
            var sentViaTool = toolContext.SendMessageCalled;
            var toolMsg     = sentViaTool ? replyContext.SentMessage : null;

            if (sentViaTool && !string.IsNullOrWhiteSpace(toolMsg))
            {
                module.AppendHistory(target, "assistant", toolMsg);
                NotifyHelper.Instance().NotificationInfo(toolMsg, $"{Lang.Get("AutoReplyChatBot-AutoRepliedTo")}{target}");

                if (!string.IsNullOrWhiteSpace(reply))
                    module.AppendHistory(target, "assistant", reply);
            }
            else if (!string.IsNullOrWhiteSpace(reply))
            {
                SendReply(chatType, target, reply);
                NotifyHelper.Instance().NotificationInfo(reply, $"{Lang.Get("AutoReplyChatBot-AutoRepliedTo")}{target}");
                module.AppendHistory(target, "assistant", reply);
            }
        }
    }
}
