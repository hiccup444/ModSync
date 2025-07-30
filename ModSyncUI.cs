using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace MageArena_StealthSpells
{
    public class ModSyncUI : MonoBehaviour
    {
        private static ModSyncUI instance;
        private Canvas canvas;
        private GameObject messageContainer;
        private List<GameObject> activeMessages = new List<GameObject>();
        private static ManualLogSource Logger;
        
        // Public property to access the instance
        public static ModSyncUI Instance => instance;
        
        // UI Settings
        private const float MESSAGE_DURATION = 5f;
        private const float MESSAGE_FADE_TIME = 0.5f;
        private const float MESSAGE_SPACING = 80f; // Increased spacing to prevent overlap
        private const float MESSAGE_WIDTH = 400f;
        private const float MESSAGE_HEIGHT = 60f;
        private const float MARGIN_X = 20f;
        private const float MARGIN_Y = 20f;
        private const int MAX_MESSAGES = 8; // Maximum number of messages to display
        
        private void Awake()
        {
            instance = this;
            Logger = BepInEx.Logging.Logger.CreateLogSource("ModSyncUI");
            CreateUI();
        }
        
        private void OnDestroy()
        {
            if (instance == this)
                instance = null;
        }
        
        public void CreateUI()
        {
            // Create Canvas
            GameObject canvasObj = new GameObject("ModSyncCanvas");
            canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000; // High priority to be on top
            
            // Add CanvasScaler for proper scaling
            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            
            // Add GraphicRaycaster for UI interactions
            canvasObj.AddComponent<GraphicRaycaster>();
            
            // Create message container
            messageContainer = new GameObject("MessageContainer");
            messageContainer.transform.SetParent(canvasObj.transform, false);
            
            // Position container in top left
            RectTransform containerRect = messageContainer.AddComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(0, 1);
            containerRect.anchorMax = new Vector2(0, 1);
            containerRect.pivot = new Vector2(0, 1);
            containerRect.anchoredPosition = new Vector2(MARGIN_X, -MARGIN_Y);
            
            Logger.LogInfo("ModSyncUI created successfully");
        }
        
        public static void ShowMessage(string message, MessageType type = MessageType.Info)
        {
            if (instance == null)
            {
                Logger.LogWarning("ModSyncUI instance not found, creating new one");
                CreateModSyncUI();
                // Wait a frame for the instance to be created
                return;
            }
            
            // Ensure UI is initialized
            if (instance.messageContainer == null)
            {
                Logger.LogWarning("ModSyncUI not initialized, recreating UI...");
                instance.CreateUI();
                
                // Check again after recreation
                if (instance.messageContainer == null)
                {
                    Logger.LogWarning("ModSyncUI still not initialized, skipping message");
                    return;
                }
            }
            
            instance.DisplayMessage(message, type);
        }
        
        private void DisplayMessage(string message, MessageType type)
        {
            // Check if we've reached the maximum message limit
            if (activeMessages.Count >= MAX_MESSAGES)
            {
                Logger.LogWarning($"Maximum message limit ({MAX_MESSAGES}) reached, removing oldest message");
                RemoveOldestMessage();
            }
            
            StartCoroutine(CreateMessageCoroutine(message, type));
        }
        
        private IEnumerator CreateMessageCoroutine(string message, MessageType type)
        {
            // Create message background
            GameObject messageObj = new GameObject($"ModSyncMessage_{activeMessages.Count}");
            messageObj.transform.SetParent(messageContainer.transform, false);
            
            // Add background image
            Image background = messageObj.AddComponent<Image>();
            background.color = GetMessageColor(type);
            
            // Set up RectTransform
            RectTransform rect = messageObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(MESSAGE_WIDTH, MESSAGE_HEIGHT);
            rect.anchoredPosition = new Vector2(0, -activeMessages.Count * MESSAGE_SPACING);
            
            // Create text
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(messageObj.transform, false);
            
            Text text = textObj.AddComponent<Text>();
            text.text = message;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 16;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            
            // Set up text RectTransform
            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 5);
            textRect.offsetMax = new Vector2(-10, -5);
            
            // Add to active messages
            activeMessages.Add(messageObj);
            
            // Fade in
            yield return StartCoroutine(FadeIn(messageObj));
            
            // Wait for duration
            yield return new WaitForSeconds(MESSAGE_DURATION);
            
            // Fade out
            yield return StartCoroutine(FadeOut(messageObj));
            
            // Remove from active messages and destroy
            activeMessages.Remove(messageObj);
            Destroy(messageObj);
            
            // Reposition remaining messages
            RepositionMessages();
        }
        
        private IEnumerator FadeIn(GameObject messageObj)
        {
            CanvasGroup canvasGroup = messageObj.GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = messageObj.AddComponent<CanvasGroup>();
            
            canvasGroup.alpha = 0f;
            
            float elapsed = 0f;
            while (elapsed < MESSAGE_FADE_TIME)
            {
                elapsed += Time.deltaTime;
                canvasGroup.alpha = Mathf.Lerp(0f, 1f, elapsed / MESSAGE_FADE_TIME);
                yield return null;
            }
            
            canvasGroup.alpha = 1f;
        }
        
        private IEnumerator FadeOut(GameObject messageObj)
        {
            CanvasGroup canvasGroup = messageObj.GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = messageObj.AddComponent<CanvasGroup>();
            
            float elapsed = 0f;
            while (elapsed < MESSAGE_FADE_TIME)
            {
                elapsed += Time.deltaTime;
                canvasGroup.alpha = Mathf.Lerp(1f, 0f, elapsed / MESSAGE_FADE_TIME);
                yield return null;
            }
            
            canvasGroup.alpha = 0f;
        }
        
        private void RepositionMessages()
        {
            for (int i = 0; i < activeMessages.Count; i++)
            {
                if (activeMessages[i] != null)
                {
                    RectTransform rect = activeMessages[i].GetComponent<RectTransform>();
                    rect.anchoredPosition = new Vector2(0, -i * MESSAGE_SPACING);
                }
            }
        }
        
        private void RemoveOldestMessage()
        {
            if (activeMessages.Count > 0)
            {
                GameObject oldestMessage = activeMessages[0];
                activeMessages.RemoveAt(0);
                
                if (oldestMessage != null)
                {
                    // Fade out and destroy the oldest message
                    StartCoroutine(FadeOutAndDestroy(oldestMessage));
                }
                
                // Reposition remaining messages
                RepositionMessages();
            }
        }
        
        private IEnumerator FadeOutAndDestroy(GameObject messageObj)
        {
            yield return StartCoroutine(FadeOut(messageObj));
            
            if (messageObj != null)
            {
                Destroy(messageObj);
            }
        }
        
        private Color GetMessageColor(MessageType type)
        {
            switch (type)
            {
                case MessageType.Success:
                    return new Color(0.2f, 0.8f, 0.2f, 0.9f);
                case MessageType.Warning:
                    return new Color(0.8f, 0.6f, 0.2f, 0.9f);
                case MessageType.Error:
                    return new Color(0.8f, 0.2f, 0.2f, 0.9f);
                case MessageType.Info:
                default:
                    return new Color(0.2f, 0.2f, 0.8f, 0.9f);
            }
        }
        
        private static void CreateModSyncUI()
        {
            GameObject uiObj = new GameObject("ModSyncUI");
            uiObj.AddComponent<ModSyncUI>();
            DontDestroyOnLoad(uiObj);
        }
        
        public enum MessageType
        {
            Info,
            Success,
            Warning,
            Error
        }
    }
} 