using BepInEx;
using UnityEngine;

namespace ExampleMod
{
    [BepInPlugin("com.example.mymod", "My Example Mod", "1.0.0")]
    [BepInProcess("MageArena.exe")]
    public class ExampleMod : BaseUnityPlugin
    {
        // This mod requires both client and host to have it
        public static string modsync = "all";
        
        private void Awake()
        {
            // Check if ModSync is present before initializing
            if (!CheckModSyncDependency())
            {
                Logger.LogError("ModSync is required but not found! Closing game...");
                ShowModSyncMissingMessage();
                StartCoroutine(CloseGameAfterDelay(6f));
                return;
            }
            
            Logger.LogInfo("ModSync found! Initializing mod...");
            // Your mod initialization code here
        }
        
        /// <summary>
        /// Simple function to check if ModSync is present - copy this to your mod
        /// </summary>
        /// <returns>True if ModSync is found, false otherwise</returns>
        private bool CheckModSyncDependency()
        {
            try
            {
                var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    var types = assembly.GetTypes();
                    foreach (var type in types)
                    {
                        var pluginAttr = type.GetCustomAttribute<BepInPlugin>();
                        if (pluginAttr != null && pluginAttr.GUID == "com.magearena.modsync")
                        {
                            Logger.LogInfo("ModSync plugin found!");
                            return true;
                        }
                    }
                }
                
                Logger.LogWarning("ModSync plugin not found!");
                return false;
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error checking for ModSync: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Shows error message to user - copy this to your mod
        /// </summary>
        private void ShowModSyncMissingMessage()
        {
            GameObject messageObj = new GameObject("ModSyncMissingMessage");
            var messageComponent = messageObj.AddComponent<ModSyncMissingMessage>();
            UnityEngine.Object.DontDestroyOnLoad(messageObj);
        }
        
        /// <summary>
        /// Closes the game after delay - copy this to your mod
        /// </summary>
        /// <param name="delaySeconds">Delay in seconds</param>
        private System.Collections.IEnumerator CloseGameAfterDelay(float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);
            
            Logger.LogInfo("Closing game due to missing ModSync dependency...");
            
            #if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
            #else
                Application.Quit();
            #endif
        }
    }
    
    /// <summary>
    /// UI component to show error message - copy this to your mod
    /// </summary>
    public class ModSyncMissingMessage : MonoBehaviour
    {
        private void OnGUI()
        {
            // Create a full-screen overlay
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");
            
            // Show error message
            GUI.color = Color.red;
            GUI.skin.label.fontSize = 24;
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            
            string message = "ERROR: ModSync is required but not found!\n\n" +
                           "This mod requires ModSync to be installed.\n" +
                           "Please install ModSync and restart the game.\n\n" +
                           "Game will close in a few seconds...";
            
            GUI.Label(new Rect(0, 0, Screen.width, Screen.height), message);
            
            // Auto-destroy after showing the message
            Destroy(gameObject, 5f);
        }
    }
}

namespace AnotherExampleMod
{
    [BepInPlugin("com.example.clientonly", "Client Only Mod", "1.0.0")]
    [BepInProcess("MageArena.exe")]
    public class ClientOnlyMod : BaseUnityPlugin
    {
        // This mod only needs to be on the client
        public static string modsync = "client";
        
        private void Awake()
        {
            // Your mod initialization code here
        }
    }
}

namespace HostOnlyExample
{
    [BepInPlugin("com.example.hostonly", "Host Only Mod", "1.0.0")]
    [BepInProcess("MageArena.exe")]
    public class HostOnlyMod : BaseUnityPlugin
    {
        // This mod only needs to be on the host
        public static string modsync = "host";
        
        private void Awake()
        {
            // Your mod initialization code here
        }
    }
}

namespace NoSyncExample
{
    [BepInPlugin("com.example.nosync", "No Sync Mod", "1.0.0")]
    [BepInProcess("MageArena.exe")]
    public class NoSyncMod : BaseUnityPlugin
    {
        // This mod has no modsync variable, so it's excluded from matching
        // It will work regardless of what other players have
        
        private void Awake()
        {
            // Your mod initialization code here
        }
    }
} 