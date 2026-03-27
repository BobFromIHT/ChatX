using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChatX
{
    internal sealed class ChatLinkClickHandler : MonoBehaviour,
        IPointerClickHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IInitializePotentialDragHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IScrollHandler
    {
        private ChatBehaviourAssets _assets;
        private ScrollRect _scrollRect;
        private TextMeshProUGUI _chatText;
        private bool _dragInProgress;

        internal void Bind(ChatBehaviourAssets assets, ScrollRect scrollRect, TextMeshProUGUI chatText)
        {
            _assets = assets;
            _scrollRect = scrollRect;
            _chatText = chatText;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null
                || eventData.button != PointerEventData.InputButton.Left
                || _dragInProgress
                || _chatText == null
                || ChatX.IsChatLinkPromptOpen)
                return;

            var eventCamera = eventData.pressEventCamera ?? eventData.enterEventCamera;
            int linkIndex = TMP_TextUtilities.FindIntersectingLink(_chatText, eventData.position, eventCamera);
            if (linkIndex < 0 || linkIndex >= _chatText.textInfo.linkCount)
                return;

            string url = _chatText.textInfo.linkInfo[linkIndex].GetLinkID();
            if (string.IsNullOrWhiteSpace(url))
                return;

            ChatX.ShowChatLinkPrompt(_assets, url);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _dragInProgress = false;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _dragInProgress = false;
        }

        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            ForwardToScrollRect(eventData, ExecuteEvents.initializePotentialDrag);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragInProgress = true;
            ForwardToScrollRect(eventData, ExecuteEvents.beginDragHandler);
        }

        public void OnDrag(PointerEventData eventData)
        {
            _dragInProgress = true;
            ForwardToScrollRect(eventData, ExecuteEvents.dragHandler);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            ForwardToScrollRect(eventData, ExecuteEvents.endDragHandler);
            _dragInProgress = false;
        }

        public void OnScroll(PointerEventData eventData)
        {
            ForwardToScrollRect(eventData, ExecuteEvents.scrollHandler);
        }

        private void ForwardToScrollRect<T>(BaseEventData eventData, ExecuteEvents.EventFunction<T> handler)
            where T : IEventSystemHandler
        {
            if (_scrollRect == null || eventData == null)
                return;

            ExecuteEvents.Execute(_scrollRect.gameObject, eventData, handler);
        }
    }
}
