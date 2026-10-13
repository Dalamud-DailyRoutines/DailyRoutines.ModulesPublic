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
            if (!module.rateLimiter.CanProceed(target, cfg.CooldownSeconds)) return;

            module.AppendHistory(target, "user", userText);

            // ── Stage 3-4: Build + LLM ──
            using var ticket = await module.rateLimiter.AcquireAsync(target, ct).ConfigureAwait(false);

            var replyContext = new ReplyContext
            {
                Target       = target,
                OriginalType = chatType,
                DefaultChannel = chatType == XivChatType.TellIncoming ?
                                     "tell" :
                                     "say",
                ChannelCommands = ChannelCommands
            };

            var toolContext = new ToolExecutionContext
            {
                ModuleConfig      = cfg,
                ReplyContext      = replyContext,
                ConversationStore = module.conversationStore,
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
            var toolMsg = sentViaTool ?
                              replyContext.SentMessage :
                              null;

            if (sentViaTool && !string.IsNullOrWhiteSpace(toolMsg))
            {
                module.AppendHistory(target, "assistant", toolMsg);
                NotifyHelper.Instance().NotificationInfo(toolMsg, $"{Lang.Get("AutoReplyChatBot-AutoRepliedTo")}{target}");

                if (!string.IsNullOrWhiteSpace(reply))
                    module.AppendHistory(target, "assistant", reply);
            }
            else if (!string.IsNullOrWhiteSpace(reply))
            {
                await SendReply(chatType, target, reply).ConfigureAwait(false);
                NotifyHelper.Instance().NotificationInfo(reply, $"{Lang.Get("AutoReplyChatBot-AutoRepliedTo")}{target}");
                module.AppendHistory(target, "assistant", reply);
            }
        }
    }
}
