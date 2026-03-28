using System;
using System.Text;
using UnityEngine;

namespace ChatX
{
    public partial class ChatX
    {
        private static bool _chatHidden; // Tracks whether the chat is hidden via the Home toggle.

        public static void ClearChatNow()
        {
            var cb = Player._mainPlayer?._chatBehaviour;
            var a = cb?._chatAssets;
            if (!cb || !a) return;
            cb._chatMessages?.Clear();
            if (a._chatText) a._chatText.text = string.Empty;
            ApplyOpacityCap(a);
        }

        public static void ClearGameFeedNow()
        {
            var cb = Player._mainPlayer?._chatBehaviour;
            var a = cb?._chatAssets;
            if (!cb || !a) return;
            cb._gameLogicMessages?.Clear();
            if (a._gameLogicText) a._gameLogicText.text = string.Empty;
            ApplyOpacityCap(a);
        }

        public static void ClearAllNow()
        {
            ClearChatNow();
            ClearGameFeedNow();
        }

        private static void TryHandleHotkeys()
        {
            // Listen for global clear/toggle shortcuts while keeping out of the user's way when typing.
            var player = Player._mainPlayer;
            if (!player) return;
            var input = ChatInputResolver.TryGetCurrent();
            bool inputFocused = input?.IsFocused == true;
            var clearKey = clearChatKey?.Value ?? KeyCode.None;
            if (clearKey != KeyCode.None && !inputFocused && Input.GetKeyDown(clearKey))
                ClearAllNow();
            var toggleKey = toggleChatVisibilityKey?.Value ?? KeyCode.None;
            if (toggleKey != KeyCode.None && !inputFocused && Input.GetKeyDown(toggleKey))
            {
                _chatHidden = !_chatHidden;
                var assets = player?._chatBehaviour?._chatAssets ?? ChatBehaviourAssets._current;
                ApplyOpacityCap(assets, force: true);
            }
        }

        private static bool ShouldPushGameFeed() => pushGameMessage?.Value == true;

        private static string StripRichTextTags(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            var sb = new StringBuilder(message.Length);
            bool inTag = false;
            foreach (var ch in message)
            {
                if (ch == '<')
                {
                    inTag = true;
                    continue;
                }
                if (ch == '>' && inTag)
                {
                    inTag = false;
                    continue;
                }
                if (!inTag) sb.Append(ch);
            }
            return sb.ToString();
        }

        internal static void RouteWaypointAttuneMessage(ChatBehaviour chat, string message)
        {
            if (!ShouldPushGameFeed())
            {
                chat?.New_ChatMessage(message);
                return;
            }

            if (!chat || string.IsNullOrEmpty(message)) return;

            PushGameFeedMessage(chat, StripRichTextTags(message));
        }

        internal static void PushGameFeedMessage(ChatBehaviour chat, string message)
        {
            if (!chat || string.IsNullOrEmpty(message)) return;

            if (ShouldPushGameFeed())
                chat.Init_GameLogicMessage(message);
            else
                chat.New_ChatMessage(message);
        }

        internal static void PushTargetedGameFeedMessage(ChatBehaviour chat, string message)
        {
            if (!chat || string.IsNullOrEmpty(message)) return;

            if (ShouldPushGameFeed())
                chat.Target_GameLogicMessage(message);
            else
                chat.Target_RecieveMessage(message);
        }
    }
}
