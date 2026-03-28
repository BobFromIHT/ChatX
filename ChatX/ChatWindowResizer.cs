using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ChatX
{
    internal static class ChatWindowResizer
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

        internal static void Register(ChatBehaviourAssets assets)
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

        internal static void ApplyAll()
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

        internal static void Reset()
        {
            _targets.Clear();
        }

        private static TargetGroup CreateTargetGroup(ChatBehaviourAssets assets)
        {
            var chatScrollRect = assets._chatText ? assets._chatText.GetComponentInParent<ScrollRect>() : null;
            var logicScrollRect = assets._gameLogicText ? assets._gameLogicText.GetComponentInParent<ScrollRect>() : null;
            var chatContainer = ChatX.FindDescendantComponentByName<RectTransform>(assets.transform, "_chatbox_textField");
            var chatGroupRect = GetRect(assets._chatboxTextGroup);
            var chatBackdrop = chatGroupRect != null && chatGroupRect.name == "_chatbox_backdrop"
                ? chatGroupRect
                : (chatContainer != null ? ChatX.FindDescendantComponentByName<RectTransform>(chatContainer, "_chatbox_backdrop") : ChatX.FindDescendantComponentByName<RectTransform>(assets.transform, "_chatbox_backdrop"));
            if (chatBackdrop != null && chatGroupRect != null && ReferenceEquals(chatBackdrop, chatGroupRect))
                chatBackdrop = null;

            var chatMask = chatContainer != null ? ChatX.FindDescendantComponentByName<RectTransform>(chatContainer, "_chatbox_mask") : ChatX.FindDescendantComponentByName<RectTransform>(assets.transform, "_chatbox_mask");
            var channelDockSearchRoot = chatBackdrop ?? chatGroupRect ?? chatContainer ?? assets.transform;
            var channelDock = ChatX.FindDescendantComponentByName<RectTransform>(channelDockSearchRoot, "_dolly_chatChannelDock");
            var inputRect = ChatInputResolver.TryGet(assets)?.RectTransform;
            var chatTextRect = assets._chatText ? assets._chatText.GetComponent<RectTransform>() : null;

            ChatX.RegisterScrollbarTargets(assets, chatScrollRect);

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

        private static void CaptureSnapshots(TargetGroup group, bool force = false)
        {
            RefreshSnapshot(group.ChatContainer, force);
            RefreshSnapshot(group.ChatBackdrop, force);
            RefreshSnapshot(group.ChatMask, force);
            RefreshSnapshot(group.ChatGroup, force);
            RefreshSnapshot(group.ChatScroll, force);
            RefreshSnapshot(group.ChatViewport, force);
            RefreshSnapshot(group.ChatScrollbar, force);
            RefreshSnapshot(group.ChatChannelDock, force);
            RefreshSnapshot(group.ChatInput, force);
            RefreshSnapshot(group.ChatText, force);
            RefreshSnapshot(group.LogicGroup, force);
            RefreshSnapshot(group.LogicScroll, force);
            RefreshSnapshot(group.LogicViewport, force);
            RefreshSnapshot(group.LogicScrollbar, force);
        }

        private static void Apply(TargetGroup group)
        {
            bool tall = ChatX.chatTallWindow?.Value ?? false;
            float multiplier = tall ? TallMultiplier : 1f;
            float chatDelta = GetDelta(group.ChatViewport, multiplier);
            float logicDelta = GetDelta(group.LogicViewport, multiplier);
            float inputOffset = tall ? ChatInputYOffset : 0f;
            float textOffset = tall ? ChatTextYOffset : 0f;

            ApplySnapshot(group.ChatContainer, chatDelta);
            ApplySnapshot(group.ChatBackdrop, chatDelta);
            ApplySnapshot(group.ChatMask, chatDelta);
            ApplySnapshot(group.ChatGroup, chatDelta);
            ApplySnapshot(group.ChatScroll, chatDelta);
            ApplySnapshot(group.ChatViewport, chatDelta);
            ApplySnapshot(group.ChatScrollbar, chatDelta);
            ApplySnapshot(group.ChatChannelDock, chatDelta);

            ApplySnapshot(group.LogicGroup, logicDelta);
            ApplySnapshot(group.LogicScroll, logicDelta);
            ApplySnapshot(group.LogicViewport, logicDelta);
            ApplySnapshot(group.LogicScrollbar, logicDelta);

            if (tall)
                ApplyTallOffsets(group);

            ApplySnapshot(group.ChatInput, 0f);
            ApplyInputOffset(group.ChatInput, inputOffset);

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
            AdjustRect(group.ChatBackdrop, TallBackdropShiftDown, TallBackdropExtraHeight);
            AdjustRect(group.ChatMask, TallMaskShiftDown, TallMaskExtraHeight);
            AdjustRect(group.ChatScroll, TallScrollShiftDown, TallScrollExtraHeight);
            AdjustRect(group.ChatViewport, TallScrollShiftDown, TallViewportExtraHeight);
            AdjustRect(group.ChatScrollbar, TallScrollbarShiftDown, TallScrollbarExtraHeight);
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

        private static void Cleanup()
        {
            _targets.RemoveAll(t => !t.Asset.TryGetTarget(out var assets) || assets == null);
        }
    }
}
