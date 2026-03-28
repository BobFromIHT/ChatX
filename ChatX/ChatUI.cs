using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace ChatX
{
    public partial class ChatX
    {
        internal static readonly ConditionalWeakTable<ChatBehaviourAssets, ChatScrollbarState> _scrollbarStates = new(); // Keeps lightweight state for each live chat window.
        private static bool _dialogSuppressed;
        private static bool pendingRestore;
        private static bool _chatWasHidden;
        internal static readonly FieldInfo ChatInputBufferField = typeof(ChatBehaviour).GetField("_inputBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static bool _loggedMissingInputBufferField;

        internal sealed class ChatScrollbarState
        {
            public CanvasGroup Scrollbar;
            public GameObject ScrollbarObject;
            public Scrollbar ScrollbarComponent;
            public ScrollRect ScrollRect;
            public GameObject ChatContainer;
            public bool? CachedChatContainerActive;
            public bool? CachedChannelDockActive;
        }

        private static ChatScrollbarState GetScrollbarState(ChatBehaviourAssets assets)
        {
            if (!assets) return null;
            if (!_scrollbarStates.TryGetValue(assets, out var state))
            {
                state = new ChatScrollbarState();
                _scrollbarStates.Add(assets, state);
            }

            if (state.ScrollRect == null)
                state.ScrollRect = TryGetChatScrollRect(assets);

            if (state.Scrollbar == null || state.ScrollbarObject == null || state.ScrollbarComponent == null)
            {
                var canvasGroup = TryGetScrollbarGroup(assets, state);
                if (canvasGroup != null)
                    state.Scrollbar = canvasGroup;
            }

            if (state.ChatContainer == null)
                state.ChatContainer = ResolveChatContainer(assets);

            return state;
        }

        internal static void RegisterScrollbarTargets(ChatBehaviourAssets assets, ScrollRect chatScrollRect)
        {
            if (!assets) return;
            var state = _scrollbarStates.GetOrCreateValue(assets);
            if (chatScrollRect != null)
            {
                state.ScrollRect = chatScrollRect;
                var scrollbar = chatScrollRect.verticalScrollbar;
                if (scrollbar != null)
                {
                    state.ScrollbarComponent = scrollbar;
                    state.ScrollbarObject = scrollbar.gameObject;
                    if (state.Scrollbar == null)
                        state.Scrollbar = scrollbar.GetComponent<CanvasGroup>() ?? state.ScrollbarObject.AddComponent<CanvasGroup>();
                }
            }
        }

        private static ScrollRect TryGetChatScrollRect(ChatBehaviourAssets assets)
        {
            var chatText = assets?._chatText;
            if (!chatText) return null;
            try
            {
                return chatText.GetComponentInParent<ScrollRect>();
            }
            catch
            {
                return null;
            }
        }

        private static GameObject ResolveChatContainer(ChatBehaviourAssets assets)
        {
            if (!assets) return null;
            if (assets._chatboxTextGroup != null)
                return assets._chatboxTextGroup.gameObject;
            var fallback = FindDescendantTransformByName(assets.transform, "_chatbox_textField");
            if (fallback != null)
                return fallback.gameObject;
            var chatText = assets._chatText;
            return chatText != null ? chatText.gameObject : null;
        }

        private static bool ShouldHoldChatForExternalUI()
        {
            var dialog = DialogManager._current;
            bool dialogActive = dialog && dialog._isDialogEnabled;

            if (dialogActive)
            {
                _dialogSuppressed = true;
                return true;
            }

            if (_dialogSuppressed)
            {
                _dialogSuppressed = false;
                pendingRestore = true;
            }

            return false;
        }

        private static void ResetChatFocus()
        {
            var chat = ChatBehaviour._current;
            if (!chat)
                return;

            chat._focusedInChat = false;
            var input = ChatInputResolver.TryGet(chat._chatAssets);
            input?.Deactivate();

            if (ChatInputBufferField != null)
            {
                try
                {
                    ChatInputBufferField.SetValue(chat, 0f);
                }
                catch
                {
                }
            }
            else if (!_loggedMissingInputBufferField)
            {
                _loggedMissingInputBufferField = true;
                Log?.LogWarning("ChatX could not resolve ChatBehaviour._inputBuffer. Hidden-chat focus restoration may be limited.");
            }

            chat.Display_Chat(true);
            chat._textFadeTimer = Mathf.Max(chat._textFadeTimer, 1f);
            chat._logicFadeTimer = Mathf.Max(chat._logicFadeTimer, 1f);
            if (chat._chatAssets)
            {
                var general = chat._chatAssets._generalCanvasGroup;
                if (general)
                {
                    general.alpha = 1f;
                    general.blocksRaycasts = true;
                    general.interactable = true;
                }
            }
        }

        internal static void ResetUiRuntimeState()
        {
            _dialogSuppressed = false;
            pendingRestore = false;
            _chatWasHidden = false;
            _loggedMissingInputBufferField = false;
            ChatWindowResizer.Reset();
        }

        private static void SyncChatVisibility(ChatBehaviourAssets assets, ChatScrollbarState state, bool chatHidden, bool allFaded)
        {
            if (state == null) return;

            var chatContainer = state.ChatContainer;
            if (chatContainer == null)
                chatContainer = state.ChatContainer = ResolveChatContainer(assets);
            if (chatContainer != null)
            {
                if (chatHidden)
                {
                    if (!state.CachedChatContainerActive.HasValue)
                        state.CachedChatContainerActive = chatContainer.activeSelf;
                    if (chatContainer.activeSelf)
                        chatContainer.SetActive(false);
                }
                else if (state.CachedChatContainerActive.HasValue)
                {
                    bool desired = state.CachedChatContainerActive.Value;
                    state.CachedChatContainerActive = null;
                    if (chatContainer.activeSelf != desired)
                        chatContainer.SetActive(desired);
                }
            }

            var dock = assets?._chatChannelDockObject;
            if (dock != null)
            {
                if (chatHidden)
                {
                    if (!state.CachedChannelDockActive.HasValue)
                        state.CachedChannelDockActive = dock.activeSelf;
                    if (dock.activeSelf)
                        dock.SetActive(false);
                }
                else if (state.CachedChannelDockActive.HasValue)
                {
                    bool desired = state.CachedChannelDockActive.Value;
                    state.CachedChannelDockActive = null;
                    if (dock.activeSelf != desired)
                        dock.SetActive(desired);
                }
            }

            var generalGroup = assets?._generalCanvasGroup;
            if (generalGroup != null)
            {
                bool allowInteraction = !allFaded;
                generalGroup.blocksRaycasts = allowInteraction;
                generalGroup.interactable = allowInteraction;
            }

            if (state.ScrollRect != null)
            {
                bool allowScroll = !allFaded;
                state.ScrollRect.enabled = allowScroll;
            }
        }

        // Ensure the chat's vertical scrollbar has a CanvasGroup we can manipulate.
        private static CanvasGroup TryGetScrollbarGroup(ChatBehaviourAssets assets, ChatScrollbarState state)
        {
            if (state == null) return null;
            var scrollRect = state.ScrollRect ?? TryGetChatScrollRect(assets);
            state.ScrollRect = scrollRect;
            if (scrollRect == null) return null;

            var scrollbar = scrollRect.verticalScrollbar;
            if (scrollbar == null) return null;

            state.ScrollbarComponent = scrollbar;
            state.ScrollbarObject = scrollbar.gameObject;

            var group = scrollbar.GetComponent<CanvasGroup>();
            if (group == null)
                group = state.ScrollbarObject.AddComponent<CanvasGroup>();

            return group;
        }

        private static void UpdateScrollbarState(ChatBehaviourAssets assets, ChatScrollbarState state, bool chatHidden, float visualAlpha, bool transparentOverride, bool allFaded)
        {
            if (state == null) return;

            if (state.ScrollRect == null)
                state.ScrollRect = TryGetChatScrollRect(assets);

            if (state.ScrollRect == null)
                return;

            if (state.Scrollbar == null || state.ScrollbarObject == null || state.ScrollbarComponent == null)
            {
                state.Scrollbar = TryGetScrollbarGroup(assets, state);
                if (state.ScrollRect == null) return;
            }

            bool showScrollbar = !chatHidden && !allFaded && ShouldShowScrollbar(state);
            float targetAlpha = transparentOverride ? 0f : visualAlpha;

            if (state.Scrollbar != null)
            {
                state.Scrollbar.alpha = showScrollbar ? targetAlpha : 0f;
                state.Scrollbar.blocksRaycasts = showScrollbar;
                state.Scrollbar.interactable = showScrollbar;
            }

            if (state.ScrollbarComponent != null)
                state.ScrollbarComponent.interactable = showScrollbar;
        }

        private static void UpdateEffectiveAlpha(ref float alpha, ref bool sawNonZero, float candidate)
        {
            if (candidate > 0.001f)
            {
                sawNonZero = true;
                alpha = Mathf.Min(alpha, candidate);
            }
        }

        private static bool HasExternalHudOverlay(ChatBehaviourAssets assets)
        {
            var triggerText = assets?._triggerMessageText;
            return triggerText != null
                && triggerText.enabled
                && triggerText.gameObject.activeInHierarchy
                && !string.IsNullOrWhiteSpace(triggerText.text);
        }

        private static bool ShouldShowScrollbar(ChatScrollbarState state)
        {
            var scrollRect = state?.ScrollRect;
            if (scrollRect == null) return false;

            var content = scrollRect.content;
            var viewport = scrollRect.viewport != null ? scrollRect.viewport : scrollRect.GetComponent<RectTransform>();
            if (content == null || viewport == null) return false;

            var contentHeight = content.rect.height;
            var viewportHeight = viewport.rect.height;
            if (contentHeight <= 0f)
                contentHeight = content.sizeDelta.y;

            return contentHeight - viewportHeight > 1f;
        }

        internal static void ApplyOpacityCap(ChatBehaviourAssets assets, bool force = false)
        {
            if (!assets) return;

            bool suppressForUI = ShouldHoldChatForExternalUI();
            bool chatHidden = _chatHidden || suppressForUI;
            bool wasHidden = _chatWasHidden;
            _chatWasHidden = chatHidden;

            if (wasHidden && !chatHidden)
                pendingRestore = true;

            bool shouldRestore = pendingRestore && !chatHidden;
            if (shouldRestore)
                pendingRestore = false;

            float cap = chatHidden ? 0f : Mathf.Clamp01(chatOpacity?.Value ?? 1f);
            float effectiveAlpha = cap;
            bool sawNonZeroAlpha = false;

            var generalCanvas = assets._generalCanvasGroup;
            float baseGeneralAlpha = chatHidden ? 0f : 1f;

            var textGroups = assets._chatTextGroups;
            if (textGroups != null)
            {
                for (int i = 0; i < textGroups.Length; i++)
                {
                    var cg = textGroups[i];
                    if (!cg) continue;
                    float prior = cg.alpha;
                    float clamped = chatHidden ? 0f : shouldRestore ? cap : Mathf.Min(prior, cap);
                    if (force || shouldRestore || !Mathf.Approximately(prior, clamped))
                        cg.alpha = clamped;
                    UpdateEffectiveAlpha(ref effectiveAlpha, ref sawNonZeroAlpha, cg.alpha);
                }
            }

            if (assets._chatboxTextGroup)
            {
                float prior = assets._chatboxTextGroup.alpha;
                float clamped = chatHidden ? 0f : shouldRestore ? cap : Mathf.Min(prior, cap);
                if (force || shouldRestore || !Mathf.Approximately(prior, clamped))
                    assets._chatboxTextGroup.alpha = clamped;
                UpdateEffectiveAlpha(ref effectiveAlpha, ref sawNonZeroAlpha, assets._chatboxTextGroup.alpha);
            }

            if (assets._gameLogicGroup)
            {
                float prior = assets._gameLogicGroup.alpha;
                float clamped = chatHidden ? 0f : shouldRestore ? cap : Mathf.Min(prior, cap);
                if (force || shouldRestore || !Mathf.Approximately(prior, clamped))
                    assets._gameLogicGroup.alpha = clamped;
                UpdateEffectiveAlpha(ref effectiveAlpha, ref sawNonZeroAlpha, assets._gameLogicGroup.alpha);
            }

            bool allFaded = chatHidden || !sawNonZeroAlpha;
            bool externalOverlayVisible = HasExternalHudOverlay(assets);
            if (allFaded)
                effectiveAlpha = 0f;

            effectiveAlpha = Mathf.Clamp01(effectiveAlpha);
            bool transparent = transparentScrollbar?.Value == true;

            if (generalCanvas)
            {
                float target = externalOverlayVisible ? 1f : allFaded ? 0f : baseGeneralAlpha;
                if (force || !Mathf.Approximately(generalCanvas.alpha, target))
                    generalCanvas.alpha = target;
                bool allow = !allFaded;
                if (generalCanvas.blocksRaycasts != allow) generalCanvas.blocksRaycasts = allow;
                if (generalCanvas.interactable != allow) generalCanvas.interactable = allow;
            }

            var state = GetScrollbarState(assets);
            if (state != null)
            {
                UpdateScrollbarState(assets, state, chatHidden, effectiveAlpha, transparent, allFaded);
                SyncChatVisibility(assets, state, chatHidden, allFaded);
            }
        }
    }
}
