using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;
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
        private static readonly FieldInfo ChatInputBufferField = typeof(ChatBehaviour).GetField("_inputBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
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

        private static void RegisterScrollbarTargets(ChatBehaviourAssets assets, ScrollRect chatScrollRect)
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
            var fallback = FindChildObject(assets.transform, "_chatbox_textField");
            if (fallback != null)
                return fallback;
            var chatText = assets._chatText;
            return chatText != null ? chatText.gameObject : null;
        }

        private static GameObject FindChildObject(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == null) continue;
                if (current.name == name)
                    return current.gameObject;
                for (int i = 0; i < current.childCount; i++)
                    queue.Enqueue(current.GetChild(i));
            }
            return null;
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
            {
                contentHeight = content.sizeDelta.y;
            }

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
            {
                pendingRestore = true;
            }

            bool shouldRestore = pendingRestore && !chatHidden;
            if (shouldRestore)
                pendingRestore = false;

            // 0 when hidden, else user cap (0..1)
            float cap = chatHidden ? 0f : Mathf.Clamp01(chatOpacity?.Value ?? 1f);
            float effectiveAlpha = cap;
            bool sawNonZeroAlpha = false;

            // 1) Top-level only controls visibility; no clamping here.
            var generalCanvas = assets._generalCanvasGroup;

            float baseGeneralAlpha = chatHidden ? 0f : 1f;

            // 2) Clamp VANILLA-WRITTEN leaf groups to a ceiling (true 'max' clamp).

            //    Track the smallest value so the scrollbar matches the visible fade level.

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

            // 3) Clamp these container groups the same way (don't set them TO cap; Min them).
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
            {
                effectiveAlpha = 0f;
            }

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

            // 4) Scrollbar: let UpdateScrollbarState own its alpha; don't double-write here.
            var state = GetScrollbarState(assets);
            if (state != null)
            {
                UpdateScrollbarState(assets, state, chatHidden, effectiveAlpha, transparent, allFaded);
                SyncChatVisibility(assets, state, chatHidden, allFaded);
            }

        }

        // Handles the optional taller chat layout while preserving vanilla anchors.
        private static class ChatWindowResizer
        {
            private const float TallMultiplier = 1.69f;
            private const float ChannelDockShiftScale = 0.69f;
            private const float TallBackdropShiftDown = 78f;
            private const float TallBackdropExtraHeight = 55f;
            private const float TallMaskShiftDown = 78f;
            private const float TallMaskExtraHeight = 55f;
            private const float TallScrollShiftDown = 78f;
            private const float TallScrollExtraHeight = -40f;
            private const float TallViewportExtraHeight = -40f;
            private const float TallScrollbarShiftDown = 65f;
            private const float TallScrollbarExtraHeight = -30f;
            private const float ChatInputYOffset = -40f;
            private const float ChatTextYOffset = -15f;

            private enum VerticalAnchorMode
            {
                PreserveCenter,
                PreserveBottom,
                PreserveTop,
                ShiftByDelta
            }

            private sealed class RectSnapshot
            {
                public RectTransform Rect;
                public Vector2 OffsetMin;
                public Vector2 OffsetMax;
                public Vector2 AnchoredPosition;
                public bool StretchY;
                public float Height;
                public bool Captured;
                public float PivotY;
                public VerticalAnchorMode AnchorMode;
                public bool AllowResize;
                public float ShiftScale;
            }

            private sealed class TargetGroup
            {
                public WeakReference<ChatBehaviourAssets> Asset;
                public RectSnapshot ChatContainer;
                public RectSnapshot ChatBackdrop;
                public RectSnapshot ChatMask;
                public RectSnapshot ChatGroup;
                public RectSnapshot ChatScroll;
                public RectSnapshot ChatViewport;
                public RectSnapshot ChatScrollbar;
                public RectSnapshot ChatChannelDock;
                public RectSnapshot ChatInput;
                public RectSnapshot ChatText;
                public RectSnapshot LogicGroup;
                public RectSnapshot LogicScroll;
                public RectSnapshot LogicViewport;
                public RectSnapshot LogicScrollbar;
            }

            private static readonly List<TargetGroup> _targets = new();

            public static void Register(ChatBehaviourAssets assets)
            {
                if (assets == null) return;
                Cleanup();
                foreach (var group in _targets)
                {
                    if (group.Asset.TryGetTarget(out var existing) && ReferenceEquals(existing, assets))
                    {
                        CaptureSnapshots(group, force: true);
                        Apply(group);
                        return;
                    }
                }

                var newGroup = CreateTargetGroup(assets);
                _targets.Add(newGroup);
                CaptureSnapshots(newGroup, force: true);
                Apply(newGroup);
            }

            public static void ApplyAll()
            {
                Cleanup();
                foreach (var group in _targets)
                {
                    if (!group.Asset.TryGetTarget(out var assets) || assets == null)
                        continue;
                    CaptureSnapshots(group);
                    Apply(group);
                }
            }

            private static TargetGroup CreateTargetGroup(ChatBehaviourAssets assets)
            {
                var chatScrollRect = assets._chatText ? assets._chatText.GetComponentInParent<ScrollRect>() : null;
                var logicScrollRect = assets._gameLogicText ? assets._gameLogicText.GetComponentInParent<ScrollRect>() : null;
                var chatContainer = FindRectTransform(assets.transform, "_chatbox_textField");
                var chatGroupRect = GetRect(assets._chatboxTextGroup);
                var chatBackdrop = chatGroupRect != null && chatGroupRect.name == "_chatbox_backdrop"
                    ? chatGroupRect
                    : (chatContainer != null ? FindRectTransform(chatContainer, "_chatbox_backdrop") : FindRectTransform(assets.transform, "_chatbox_backdrop"));
                if (chatBackdrop != null && chatGroupRect != null && ReferenceEquals(chatBackdrop, chatGroupRect))
                    chatBackdrop = null;

                var chatMask = chatContainer != null ? FindRectTransform(chatContainer, "_chatbox_mask") : FindRectTransform(assets.transform, "_chatbox_mask");
                var channelDockSearchRoot = chatBackdrop ?? chatGroupRect ?? chatContainer ?? assets.transform;
                var channelDock = FindRectTransform(channelDockSearchRoot, "_dolly_chatChannelDock");
                var inputRect = ChatInputResolver.TryGet(assets)?.RectTransform;
                var chatTextRect = assets._chatText ? assets._chatText.GetComponent<RectTransform>() : null;

                RegisterScrollbarTargets(assets, chatScrollRect);

                return new TargetGroup
                {
                    Asset = new WeakReference<ChatBehaviourAssets>(assets),
                    ChatContainer = CaptureSnapshot(chatContainer, VerticalAnchorMode.PreserveBottom),
                    ChatBackdrop = CaptureSnapshot(chatBackdrop, VerticalAnchorMode.PreserveBottom),
                    ChatMask = CaptureSnapshot(chatMask, VerticalAnchorMode.PreserveBottom),
                    ChatGroup = CaptureSnapshot(chatGroupRect, VerticalAnchorMode.PreserveBottom),
                    ChatScroll = CaptureSnapshot(chatScrollRect ? chatScrollRect.GetComponent<RectTransform>() : null, VerticalAnchorMode.PreserveBottom),
                    ChatViewport = CaptureSnapshot(chatScrollRect ? chatScrollRect.viewport : null, VerticalAnchorMode.PreserveBottom),
                    ChatScrollbar = CaptureSnapshot(chatScrollRect && chatScrollRect.verticalScrollbar ? chatScrollRect.verticalScrollbar.GetComponent<RectTransform>() : null, VerticalAnchorMode.PreserveBottom),
                    ChatChannelDock = CaptureSnapshot(channelDock, VerticalAnchorMode.ShiftByDelta, allowResize: false, shiftScale: ChannelDockShiftScale),
                    ChatInput = CaptureSnapshot(inputRect, VerticalAnchorMode.PreserveBottom, allowResize: false),
                    ChatText = CaptureSnapshot(chatTextRect, VerticalAnchorMode.PreserveTop, allowResize: false),
                    LogicGroup = CaptureSnapshot(GetRect(assets._gameLogicGroup)),
                    LogicScroll = CaptureSnapshot(logicScrollRect ? logicScrollRect.GetComponent<RectTransform>() : null),
                    LogicViewport = CaptureSnapshot(logicScrollRect ? logicScrollRect.viewport : null),
                    LogicScrollbar = CaptureSnapshot(logicScrollRect && logicScrollRect.verticalScrollbar ? logicScrollRect.verticalScrollbar.GetComponent<RectTransform>() : null),
                };
            }

            private static RectSnapshot CaptureSnapshot(RectTransform rect, VerticalAnchorMode mode = VerticalAnchorMode.PreserveCenter, bool allowResize = true, float shiftScale = 1f)
            {
                if (rect == null) return null;
                return new RectSnapshot
                {
                    Rect = rect,
                    OffsetMin = rect.offsetMin,
                    OffsetMax = rect.offsetMax,
                    AnchoredPosition = rect.anchoredPosition,
                    StretchY = !Mathf.Approximately(rect.anchorMin.y, rect.anchorMax.y),
                    Height = rect.rect.height,
                    Captured = true,
                    PivotY = rect.pivot.y,
                    AnchorMode = mode,
                    AllowResize = allowResize,
                    ShiftScale = shiftScale
                };
            }

            private static IEnumerable<RectSnapshot> All(TargetGroup group)
            {
                yield return group.ChatContainer; yield return group.ChatBackdrop; yield return group.ChatMask;
                yield return group.ChatGroup; yield return group.ChatScroll; yield return group.ChatViewport;
                yield return group.ChatScrollbar; yield return group.ChatChannelDock; yield return group.ChatInput; yield return group.ChatText;
                yield return group.LogicGroup; yield return group.LogicScroll; yield return group.LogicViewport;
                yield return group.LogicScrollbar;
            }

            private static void CaptureSnapshots(TargetGroup group, bool force = false)
            {
                foreach (var snapshot in All(group))
                    RefreshSnapshot(snapshot, force);
            }

            private static void Apply(TargetGroup group)
            {
                bool tall = chatTallWindow?.Value ?? false;
                float multiplier = tall ? TallMultiplier : 1f;
                float chatDelta = GetDelta(group.ChatViewport, multiplier);
                float logicDelta = GetDelta(group.LogicViewport, multiplier);
                float inputOffset = tall ? ChatInputYOffset : 0f;
                float textOffset = tall ? ChatTextYOffset : 0f;

                foreach (var snapshot in new[] { group.ChatContainer, group.ChatBackdrop, group.ChatMask, group.ChatGroup, group.ChatScroll, group.ChatViewport, group.ChatScrollbar, group.ChatChannelDock })
                    ApplySnapshot(snapshot, chatDelta);

                foreach (var snapshot in new[] { group.LogicGroup, group.LogicScroll, group.LogicViewport, group.LogicScrollbar })
                    ApplySnapshot(snapshot, logicDelta);

                if (tall)
                    ApplyTallOffsets(group);

                // Keep input at its captured baseline, then apply only the explicit offset.
                ApplySnapshot(group.ChatInput, 0f);
                ApplyInputOffset(group.ChatInput, inputOffset);

                // Restore chat text position, then apply text-only offset.
                ApplySnapshot(group.ChatText, 0f);
                ApplyInputOffset(group.ChatText, textOffset);
            }

            private static void RefreshSnapshot(RectSnapshot snapshot, bool force)
            {
                if (snapshot == null || snapshot.Rect == null)
                    return;

                if (!snapshot.Captured || force)
                {
                    snapshot.OffsetMin = snapshot.Rect.offsetMin;
                    snapshot.OffsetMax = snapshot.Rect.offsetMax;
                    snapshot.AnchoredPosition = snapshot.Rect.anchoredPosition;
                    snapshot.StretchY = !Mathf.Approximately(snapshot.Rect.anchorMin.y, snapshot.Rect.anchorMax.y);
                    snapshot.Height = snapshot.Rect.rect.height;
                    snapshot.PivotY = snapshot.Rect.pivot.y;
                    snapshot.Captured = true;
                }
                else
                {
                    if (snapshot.Height <= 0f)
                        snapshot.Height = snapshot.Rect.rect.height;
                    if (snapshot.Rect != null)
                        snapshot.PivotY = snapshot.Rect.pivot.y;
                }
            }

            private static void ApplyTallOffsets(TargetGroup group)
            {
                (RectSnapshot snapshot, float shift, float extra)[] operations =
                {
                    (group.ChatBackdrop, TallBackdropShiftDown, TallBackdropExtraHeight),
                    (group.ChatMask, TallMaskShiftDown, TallMaskExtraHeight),
                    (group.ChatScroll, TallScrollShiftDown, TallScrollExtraHeight),
                    (group.ChatViewport, TallScrollShiftDown, TallViewportExtraHeight),
                    (group.ChatScrollbar, TallScrollbarShiftDown, TallScrollbarExtraHeight),
                };

                foreach (var (snapshot, shift, extra) in operations)
                    AdjustRect(snapshot, shift, extra);
            }

            private static void AdjustRect(RectSnapshot snapshot, float shiftDown, float extraHeight)
            {
                if (snapshot?.Rect == null)
                    return;

                if (Mathf.Abs(shiftDown) < 0.01f && Mathf.Abs(extraHeight) < 0.01f)
                    return;

                if (snapshot.StretchY)
                {
                    var offsetMin = snapshot.Rect.offsetMin;
                    var offsetMax = snapshot.Rect.offsetMax;
                    if (Mathf.Abs(shiftDown) >= 0.01f)
                    {
                        offsetMin.y -= shiftDown;
                        offsetMax.y -= shiftDown;
                    }
                    if (Mathf.Abs(extraHeight) >= 0.01f)
                        offsetMin.y -= extraHeight;
                    snapshot.Rect.offsetMin = offsetMin;
                    snapshot.Rect.offsetMax = offsetMax;
                }
                else
                {
                    if (snapshot.AllowResize && Mathf.Abs(extraHeight) >= 0.01f)
                        snapshot.Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, snapshot.Rect.rect.height + extraHeight);

                    if (Mathf.Abs(shiftDown) >= 0.01f)
                        snapshot.Rect.anchoredPosition = new Vector2(snapshot.Rect.anchoredPosition.x, snapshot.Rect.anchoredPosition.y - shiftDown);

                    if (!snapshot.AllowResize && Mathf.Abs(extraHeight) >= 0.01f)
                    {
                        var offsetMin = snapshot.Rect.offsetMin;
                        offsetMin.y -= extraHeight;
                        snapshot.Rect.offsetMin = offsetMin;
                    }
                }
            }

            private static float GetDelta(RectSnapshot snapshot, float multiplier)
            {
                if (snapshot == null || snapshot.Rect == null)
                    return 0f;

                float height = snapshot.Height > 0f ? snapshot.Height : snapshot.Rect.rect.height;
                if (height <= 0f)
                    return 0f;

                return height * multiplier - height;
            }

            private static void ApplyInputOffset(RectSnapshot snapshot, float offset)
            {
                if (snapshot?.Rect == null) return;
                if (Mathf.Abs(offset) < 0.01f) return;
                var pos = snapshot.Rect.anchoredPosition;
                snapshot.Rect.anchoredPosition = new Vector2(pos.x, pos.y + offset);
            }

            private static void ApplySnapshot(RectSnapshot snapshot, float delta)
            {
                if (snapshot == null || snapshot.Rect == null)
                    return;

                if (Mathf.Abs(delta) < 0.01f)
                {
                    if (snapshot.StretchY)
                    {
                        snapshot.Rect.offsetMin = snapshot.OffsetMin;
                        snapshot.Rect.offsetMax = snapshot.OffsetMax;
                    }
                    else
                    {
                        if (snapshot.AllowResize)
                            snapshot.Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, snapshot.Height);
                        snapshot.Rect.anchoredPosition = snapshot.AnchoredPosition;
                    }
                    return;
                }

                if (snapshot.StretchY)
                {
                    var offsetMin = snapshot.OffsetMin;
                    var offsetMax = snapshot.OffsetMax;
                    switch (snapshot.AnchorMode)
                    {
                        case VerticalAnchorMode.PreserveBottom:
                            offsetMax.y = snapshot.OffsetMax.y + delta;
                            break;
                        case VerticalAnchorMode.PreserveTop:
                            offsetMin.y = snapshot.OffsetMin.y - delta;
                            break;
                        case VerticalAnchorMode.ShiftByDelta:
                            offsetMin.y = snapshot.OffsetMin.y + delta * snapshot.ShiftScale;
                            offsetMax.y = snapshot.OffsetMax.y + delta * snapshot.ShiftScale;
                            break;
                        default:
                            offsetMin.y = snapshot.OffsetMin.y - delta * 0.5f;
                            offsetMax.y = snapshot.OffsetMax.y + delta * 0.5f;
                            break;
                    }
                    snapshot.Rect.offsetMin = offsetMin;
                    snapshot.Rect.offsetMax = offsetMax;
                    return;
                }

                if (snapshot.AllowResize)
                    snapshot.Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, snapshot.Height + delta);

                float newY = snapshot.AnchoredPosition.y;
                switch (snapshot.AnchorMode)
                {
                    case VerticalAnchorMode.PreserveBottom:
                        newY = snapshot.AnchoredPosition.y + delta * snapshot.PivotY;
                        break;
                    case VerticalAnchorMode.PreserveTop:
                        newY = snapshot.AnchoredPosition.y - delta * (1f - snapshot.PivotY);
                        break;
                    case VerticalAnchorMode.ShiftByDelta:
                        newY = snapshot.AnchoredPosition.y + delta * snapshot.ShiftScale;
                        break;
                    case VerticalAnchorMode.PreserveCenter:
                        break;
                    default:
                        newY = snapshot.AnchoredPosition.y;
                        break;
                }

                snapshot.Rect.anchoredPosition = new Vector2(snapshot.AnchoredPosition.x, newY);
            }

            private static RectTransform GetRect(CanvasGroup group) => group?.GetComponent<RectTransform>();

            private static RectTransform FindRectTransform(Transform root, string name)
            {
                if (root == null || string.IsNullOrEmpty(name)) return null;

                var queue = new Queue<Transform>();
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current == null)
                        continue;
                    if (current.name == name && current is RectTransform rect)
                        return rect;
                    for (int i = 0; i < current.childCount; i++)
                        queue.Enqueue(current.GetChild(i));
                }

                return null;
            }

            private static void Cleanup()
            {
                _targets.RemoveAll(t => !t.Asset.TryGetTarget(out var assets) || assets == null);
            }

            public static void Reset()
            {
                _targets.Clear();
            }
        }
    }
}








