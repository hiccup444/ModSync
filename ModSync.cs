using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using System.Collections;
using Steamworks;
using Dissonance;
using Dissonance.Networking;

namespace MageArena_StealthSpells
{
    [BepInPlugin("com.magearena.modsync", "ModSync", "1.0.4")]
    [BepInProcess("MageArena.exe")]
    public class ModSync : BaseUnityPlugin
    {
        // ModSync variable for this mod itself
        public static string modsync = "all";
        private static ManualLogSource ModLogger;
        private Harmony harmony;
        private static ModSync instance;

        private bool isHost = false;
        private bool isClient = false;
        private bool modSyncCompleted = false;
        private List<ModInfo> localModList = new List<ModInfo>();
        private List<ModInfo> hostModList = new List<ModInfo>();
        private bool waitingForHostResponse = false;
        private float modSyncTimeout = 10f; // 10 seconds timeout
        private float modSyncStartTime = 0f;
        private Coroutine modSyncTimeoutCoroutine = null; // Store reference to timeout coroutine
        private bool modSyncRetrySent = false; // Track if we've sent a retry message
        private bool lobbyDetectionInitialized = false;
        private bool lobbyLockEnabled = false; // F9 toggle for lobby lock
        private float lastF9Press = 0f; // Prevent rapid toggling
        private const float F9_COOLDOWN = 0.5f; // Half second cooldown
        private List<string> connectedPlayers = new List<string>(); // Track connected players
        private bool playerJoinDetectionInitialized = false;
        private Dictionary<string, List<ModInfo>> receivedModLists = new Dictionary<string, List<ModInfo>>(); // Track received mod lists by player
        private HashSet<string> processedPlayers = new HashSet<string>(); // Track players who have been processed to prevent duplicate messages
        private bool gameStarted = false; // Track if the game has started
        private bool inModSyncTimeoutWhenGameStarted = false; // Track if we were in mod sync timeout when game started
        private DissonanceComms comms; // Chat system
        private bool chatSystemInitialized = false;
        private Dictionary<string, float> playerResponseTimeouts = new Dictionary<string, float>(); // Track player response timeouts
        private const float CHAT_RESPONSE_TIMEOUT = 8f; // 8 seconds timeout for chat responses
        private bool debugSyncMessages = false; // Config option to show ModSync messages
        private float lastRoleCheck = 0f; // Track last role check time
        private bool wasInLobby = false; // Track lobby state for logging

        // ModSync variable types
        public enum ModSyncType
        {
            Client,
            Host,
            All
        }

        // Structure to hold mod information
        public class ModInfo
        {
            public string ModName { get; set; }
            public string ModGuid { get; set; }
            public ModSyncType SyncType { get; set; }
            public bool HasModSyncVariable { get; set; }
        }

        private void Awake()
        {
            ModLogger = BepInEx.Logging.Logger.CreateLogSource("ModSync");
            harmony = new Harmony("com.magearena.modsync");
            instance = this;
            
            // Load config
            LoadConfig();
            
            ModLogger.LogInfo("ModSync plugin loaded!");
            
            // Create UI system
            CreateModSyncUI();
            
            // Start coroutine to wait for network manager and then start mod sync
            StartCoroutine(InitializeModSync());
            
            // Initialize lobby detection
            InitializeLobbyDetection();
            
            // Initialize player join detection
            InitializePlayerJoinDetection();
            
            // Initialize chat system
            InitializeChatSystem();
        }
        
        private void LoadConfig()
        {
            try
            {
                // Create config entry for debug sync messages
                debugSyncMessages = Config.Bind("Debug", "Show Sync Messages", false, 
                    "When enabled, ModSync chat messages will be visible in the chat (for debugging)").Value;
                
                ModLogger.LogInfo($"Debug sync messages: {debugSyncMessages}");
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error loading config: {ex.Message}");
                debugSyncMessages = false; // Default to false on error
            }
        }

        private IEnumerator InitializeModSync()
        {
            ModLogger.LogInfo("Waiting for chat system to initialize...");
            
            // Add safety delay to let FishNet initialize first
            yield return new WaitForSeconds(2f);
            
            // Wait for chat system to be available
            while (comms == null || comms.Text == null)
            {
                comms = DissonanceComms.GetSingleton();
                if (comms == null || comms.Text == null)
                {
                    yield return new WaitForSeconds(0.5f);
                }
            }
            
            ModLogger.LogInfo("Chat system found, starting mod sync process...");
            
            // Wait a bit more for chat to fully initialize
            yield return new WaitForSeconds(1f);
            
            // Start the mod sync process
            StartModSync();
        }
        
        private void StartModSync()
        {
            try
            {
                // Add safety check to prevent interference with FishNet
                if (Time.time < 3f) // Don't start mod sync too early
                {
                    ModLogger.LogInfo("Delaying mod sync to let FishNet initialize...");
                    StartCoroutine(DelayedStartModSync());
                    return;
                }
                
                ModLogger.LogInfo("Starting mod synchronization check...");
                
                // Get all loaded plugins
                localModList = GetLoadedPlugins();
                ModLogger.LogInfo($"Found {localModList.Count} loaded plugins");
                
                // Filter plugins that have modsync variable
                var modSyncPlugins = localModList.Where(p => p.HasModSyncVariable).ToList();
                ModLogger.LogInfo($"Found {modSyncPlugins.Count} plugins with modsync variable");
                
                // Only check "all" type plugins for matching
                var allTypePlugins = modSyncPlugins.Where(p => p.SyncType == ModSyncType.All).ToList();
                ModLogger.LogInfo($"Found {allTypePlugins.Count} plugins with 'all' sync type");
                
                // Log all plugins for debugging
                foreach (var plugin in localModList)
                {
                    if (plugin.HasModSyncVariable)
                    {
                        ModLogger.LogInfo($"Plugin: {plugin.ModName} ({plugin.ModGuid}) - SyncType: {plugin.SyncType}");
                    }
                    else
                    {
                        ModLogger.LogInfo($"Plugin: {plugin.ModName} ({plugin.ModGuid}) - No modsync variable (excluded from matching)");
                    }
                }
                
                // Determine if we're host or client
                DetermineNetworkRole();
                
                // Start the appropriate sync process
                if (isHost)
                {
                    // Host will wait for client requests
                }
                else if (isClient)
                {
                    StartClientModSync();
                }
                else
                {
                    // Not connected to network - skipping mod sync
                }
                
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error during mod sync: {ex.Message}");
                Logger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }
        
        private IEnumerator DelayedStartModSync()
        {
            yield return new WaitForSeconds(3f);
            StartModSync();
        }

        private List<ModInfo> GetLoadedPlugins()
        {
            var plugins = new List<ModInfo>();
            
            try
            {
                // Get all loaded assemblies
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        // Look for BepInPlugin attributes
                        var pluginTypes = assembly.GetTypes()
                            .Where(t => t.GetCustomAttributes(typeof(BepInPlugin), false).Any())
                            .ToList();
                        
                        foreach (var pluginType in pluginTypes)
                        {
                            var pluginAttr = pluginType.GetCustomAttribute<BepInPlugin>();
                            if (pluginAttr != null)
                            {
                                var modInfo = new ModInfo
                                {
                                    ModName = pluginAttr.Name,
                                    ModGuid = pluginAttr.GUID,
                                    SyncType = ModSyncType.Client, // Default
                                    HasModSyncVariable = false
                                };
                                
                                // Check for modsync variable
                                CheckForModSyncVariable(pluginType, modInfo);
                                
                                plugins.Add(modInfo);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"Error processing assembly {assembly.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting loaded plugins: {ex.Message}");
            }
            
            return plugins;
        }

        private void CheckForModSyncVariable(Type pluginType, ModInfo modInfo)
        {
            try
            {
                // Look for a static field or property named "modsync"
                var modSyncField = pluginType.GetField("modsync", 
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                
                var modSyncProperty = pluginType.GetProperty("modsync", 
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                
                if (modSyncField != null)
                {
                    var value = modSyncField.GetValue(null);
                    if (value != null)
                    {
                        modInfo.HasModSyncVariable = true;
                        modInfo.SyncType = ModSync.ParseModSyncType(value.ToString());
                        Logger.LogInfo($"Found modsync field in {modInfo.ModName}: {value}");
                    }
                }
                else if (modSyncProperty != null)
                {
                    var value = modSyncProperty.GetValue(null);
                    if (value != null)
                    {
                        modInfo.HasModSyncVariable = true;
                        modInfo.SyncType = ModSync.ParseModSyncType(value.ToString());
                        Logger.LogInfo($"Found modsync property in {modInfo.ModName}: {value}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Error checking for modsync variable in {modInfo.ModName}: {ex.Message}");
            }
        }



        private void DetermineNetworkRole()
        {
            // For chat-based system, we determine role based on lobby ownership
            try
            {
                if (BootstrapManager.CurrentLobbyID != 0)
                {
                    CSteamID lobbyOwner = SteamMatchmaking.GetLobbyOwner(new CSteamID(BootstrapManager.CurrentLobbyID));
                    CSteamID localSteamId = SteamUser.GetSteamID();
                    
                    if (lobbyOwner == localSteamId)
                    {
                        isHost = true;
                        isClient = false;
                        wasInLobby = true;
                    }
                    else
                    {
                        isHost = false;
                        isClient = true;
                        wasInLobby = true;
                    }
                }
                else
                {
                    isHost = false;
                    isClient = false;
                    if (wasInLobby)
                    {
                        wasInLobby = false;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogWarning($"Error determining network role: {ex.Message}");
                isHost = false;
                isClient = false;
            }
        }
        
        private void StartClientModSync()
        {
            ModLogger.LogInfo($"StartClientModSync called - isClient: {isClient}, isHost: {isHost}");
            
            if (!isClient)
            {
                ModLogger.LogWarning("Cannot start client mod sync - not a client");
                return;
            }
            
            ModLogger.LogInfo("Starting client mod sync process...");
            
            // Get only "all" type plugins for sync (excluding ModSync itself)
            var allTypePlugins = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All && p.ModGuid != "com.magearena.modsync").ToList();
            
            ModLogger.LogInfo($"Found {allTypePlugins.Count} plugins requiring host matching (excluding ModSync)");
            
            if (allTypePlugins.Count == 0)
            {
                ModLogger.LogInfo("No plugins require host matching - sync complete");
                modSyncCompleted = true;
                return;
            }
            
            // Check if we have any "All" mods (excluding ModSync) that require the host to have ModSync
                            if (allTypePlugins.Count > 0)
                {
                    // Send mod list to host and start timeout
                    ModLogger.LogInfo($"Starting mod sync for {allTypePlugins.Count} mods");
                }
            
            // Start timeout timer
            modSyncStartTime = Time.time;
            waitingForHostResponse = true;
            modSyncRetrySent = false; // Reset retry flag
            
            ModLogger.LogInfo($"Starting mod sync timeout timer at {modSyncStartTime} (timeout: {modSyncTimeout}s)");
            
            // Send mod list to host via chat
            SendModListToHostViaChat(allTypePlugins);
            
            // Start timeout coroutine and store reference
            modSyncTimeoutCoroutine = StartCoroutine(ModSyncTimeout());
        }
        
        private void SendModListToHostViaChat(List<ModInfo> modList)
        {
            ModLogger.LogInfo($"SendModListToHostViaChat called with {modList.Count} mods");
            
            // Don't send ModSync messages during gameplay
            if (gameStarted)
            {
                ModLogger.LogInfo("Skipping mod list send - game has started");
                return;
            }
            
            try
            {
                // Create a serializable mod list
                var modListData = new List<string>();
                foreach (var mod in modList)
                {
                    modListData.Add($"{mod.ModGuid}:{mod.ModName}:{mod.SyncType}");
                }
                
                ModLogger.LogInfo($"Sending {modListData.Count} mods to host via chat");
                
                // Get player name
                string playerName = GetPlayerName();
                ModLogger.LogInfo($"Client player name: {playerName}");
                
                // Send mod list via chat system
                if (comms != null && comms.Text != null)
                {
                    string modListString = string.Join(";", modListData);
                    string chatMessage = $"[MODSYNC]CLIENT_MODS:{playerName}:{modListString}";
                    
                    ModLogger.LogInfo($"About to send chat message: {chatMessage}");
                    
                    // Send to global channel
                    comms.Text.Send("Global", chatMessage);
                    ModLogger.LogInfo("Mod list sent to host via chat");
                    
                    // If debug is enabled, also send a visible message
                    if (debugSyncMessages)
                    {
                        string debugMessage = $"DEBUG: Sent mod list to host ({modListData.Count} mods)";
                        comms.Text.Send("Global", debugMessage);
                        ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                    }
                }
                else
                {
                    ModLogger.LogWarning($"Chat system not ready - comms: {comms != null}, Text: {comms?.Text != null}");
                    StartCoroutine(RetryChatSystem(modListData, playerName));
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error sending mod list to host via chat: {ex.Message}");
                ModLogger.LogError($"Stack trace: {ex.StackTrace}");
            }
        }
        
        private string GetPlayerName()
        {
            try
            {
                // Use Steam API to get player name
                string steamName = SteamFriends.GetPersonaName();
                if (!string.IsNullOrEmpty(steamName) && steamName != "Unknown")
                {
                    return steamName;
                }
                
                // Fallback
                return "Player";
            }
            catch (Exception ex)
            {
                ModLogger.LogWarning($"Error getting player name: {ex.Message}");
                return "Player";
            }
        }
        
        private string GetHostName()
        {
            try
            {
                // Get host name from Steam lobby data
                if (BootstrapManager.CurrentLobbyID != 0)
                {
                    string hostName = SteamMatchmaking.GetLobbyData(new CSteamID(BootstrapManager.CurrentLobbyID), "name");
                    if (!string.IsNullOrEmpty(hostName))
                    {
                        // Remove "'s Lobby" suffix if present
                        if (hostName.EndsWith("'s Lobby"))
                        {
                            hostName = hostName.Substring(0, hostName.Length - 9);
                        }
                        return hostName;
                    }
                    
                    // Try to get host's Steam name
                    CSteamID lobbyOwner = SteamMatchmaking.GetLobbyOwner(new CSteamID(BootstrapManager.CurrentLobbyID));
                    if (lobbyOwner != CSteamID.Nil)
                    {
                        return SteamFriends.GetFriendPersonaName(lobbyOwner);
                    }
                }
                
                // Fallback to Steam username
                return SteamFriends.GetPersonaName();
            }
            catch (Exception ex)
            {
                ModLogger.LogWarning($"Error getting host name: {ex.Message}");
                return "Host";
            }
        }
        
        private IEnumerator RetryChatSystem(List<string> modListData, string playerName)
        {
            // Wait for chat system to initialize
            yield return new WaitForSeconds(2f);
            
            int retryCount = 0;
            const int maxRetries = 5;
            
            while (retryCount < maxRetries)
            {
                // Don't send ModSync messages during gameplay
                if (gameStarted)
                {
                    yield break;
                }
                
                // Try to find chat system again
                if (comms != null && comms.Text != null)
                {
                    ModLogger.LogInfo($"Found ready chat system on retry {retryCount + 1}, sending mod list");
                    string modListString = string.Join(";", modListData);
                    string chatMessage = $"[MODSYNC]CLIENT_MODS:{playerName}:{modListString}";
                    comms.Text.Send("Global", chatMessage);
                    
                    // If debug is enabled, also send a visible message
                    if (debugSyncMessages)
                    {
                        string debugMessage = $"DEBUG: Sent mod list to host on retry ({modListData.Count} mods)";
                        comms.Text.Send("Global", debugMessage);
                        ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                    }
                    
                    yield break; // Success, exit the retry loop
                }
                
                retryCount++;
                ModLogger.LogWarning($"Chat system not ready on retry {retryCount}/{maxRetries}");
                
                if (retryCount < maxRetries)
                {
                    yield return new WaitForSeconds(2f); // Wait before next retry
                }
            }
            
            // All retries failed
            ModLogger.LogError("Chat system still not ready after all retries - mod sync may fail");
            ModSyncUI.ShowMessage("Chat error: Unable to send mod list to host", ModSyncUI.MessageType.Error);
        }
        
        private IEnumerator ModSyncTimeout()
        {
            ModLogger.LogInfo($"ModSyncTimeout coroutine started - waiting for response (timeout: {modSyncTimeout}s)");
            
            float retryTime = modSyncTimeout / 2f; // Retry halfway through timeout
            bool retryTriggered = false;
            
            while (waitingForHostResponse && Time.time - modSyncStartTime < modSyncTimeout)
            {
                // Check if we should send a retry message halfway through the timeout
                if (!retryTriggered && !modSyncRetrySent && Time.time - modSyncStartTime >= retryTime)
                {
                    retryTriggered = true;
                    
                    // Get the mod list that was originally sent
                    var allTypePlugins = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All && p.ModGuid != "com.magearena.modsync").ToList();
                    
                    if (allTypePlugins.Count > 0)
                    {
                        ModLogger.LogInfo($"Sending retry mod list to host (halfway through timeout)");
                        SendModListToHostViaChat(allTypePlugins);
                        modSyncRetrySent = true;
                    }
                }
                
                yield return null;
            }
            
            if (waitingForHostResponse)
            {
                ModLogger.LogError("Mod sync timeout - no response from host");
                waitingForHostResponse = false;
                modSyncCompleted = true;
                
                // Check if we have "All" mods (excluding ModSync) that require host sync
                var allTypePlugins = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All && p.ModGuid != "com.magearena.modsync").ToList();
                
                if (allTypePlugins.Count > 0)
                {
                    // Check if the game started while we were in timeout
                            if (inModSyncTimeoutWhenGameStarted)
        {
            // Game started during timeout - leave the game instead of lobby
            ModLogger.LogWarning($"Game started during mod sync timeout with {allTypePlugins.Count} mods requiring sync. Host doesn't have ModSync. Leaving game.");
            
            // Show notification to the client
            string missingModsList = string.Join(", ", allTypePlugins.Select(m => m.ModName));
            ModSyncUI.ShowMessage($"Game started during mod sync timeout. Host doesn't have ModSync but you have: {missingModsList}. Leaving game.", ModSyncUI.MessageType.Error);
            
            // Leave the game after a short delay to show the message
            StartCoroutine(LeaveGameAfterDelay(3f, $"Game started during mod sync timeout. Host doesn't have ModSync but you have: {missingModsList}"));
        }
        else
        {
            // Normal timeout in lobby - leave the lobby
            ModLogger.LogWarning($"Host doesn't have ModSync but we have {allTypePlugins.Count} mods requiring sync. Leaving lobby.");
            
            // Show notification to the client
            string missingModsList = string.Join(", ", allTypePlugins.Select(m => m.ModName));
            ModSyncUI.ShowMessage($"Host doesn't have ModSync but you have mods requiring sync: {missingModsList}. Leaving lobby.", ModSyncUI.MessageType.Error);
            
            // Leave the lobby after a short delay to show the message
            StartCoroutine(LeaveLobbyAfterDelay(3f, $"Host doesn't have ModSync but you have: {missingModsList}"));
        }
                }
                else
                {
                    // No mods requiring sync, just a normal timeout
                    HandleModSyncFailure("Timeout waiting for host response");
                }
            }
            else
            {
                ModLogger.LogInfo("Mod sync timeout coroutine ended - response received successfully");
            }
        }
        
        private void CompareModLists()
        {
            ModLogger.LogInfo("Comparing mod lists with host...");
            
            var localAllMods = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
            var hostAllMods = hostModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
            
            // Find missing mods on host
            var missingOnHost = localAllMods.Where(local => 
                !hostAllMods.Any(host => host.ModGuid == local.ModGuid)).ToList();
            
            // Find missing mods on client
            var missingOnClient = hostAllMods.Where(host => 
                !localAllMods.Any(local => local.ModGuid == host.ModGuid)).ToList();
            
            if (missingOnHost.Count == 0 && missingOnClient.Count == 0)
            {
                ModLogger.LogInfo("✅ Mod sync successful - all required mods match!");
                modSyncCompleted = true;
                waitingForHostResponse = false;
                
                // Show success message for client
                if (isClient)
                {
                    ModSyncUI.ShowMessage("ModSync done! You have the correct mods.", ModSyncUI.MessageType.Success);
                }
            }
            else
            {
                ModLogger.LogWarning("Mod sync failed - mod mismatch detected");
                
                if (missingOnHost.Count > 0)
                {
                    ModLogger.LogWarning($"Missing on host ({missingOnHost.Count}):");
                    foreach (var mod in missingOnHost)
                    {
                        ModLogger.LogWarning($"  - {mod.ModName} ({mod.ModGuid})");
                    }
                }
                
                if (missingOnClient.Count > 0)
                {
                    ModLogger.LogWarning($"Missing on client ({missingOnClient.Count}):");
                    foreach (var mod in missingOnClient)
                    {
                        ModLogger.LogWarning($"  - {mod.ModName} ({mod.ModGuid})");
                    }
                    
                    // Show missing mods message for client
                    if (isClient)
                    {
                        string missingMods = string.Join(", ", missingOnClient.Select(m => m.ModName));
                        ModSyncUI.ShowMessage($"You are missing mods: {missingMods}", ModSyncUI.MessageType.Error);
                    }
                }
                
                HandleModSyncFailure("Mod mismatch detected");
            }
        }
        
        private void HandleModSyncFailure(string reason)
        {
            ModLogger.LogError($"Mod sync failed: {reason}");
            
            // Show UI warning to the user
            ModSyncUI.ShowMessage($"Mod sync failed: {reason}", ModSyncUI.MessageType.Error);
            
            // For clients, show missing mods and suggest disconnection
            if (isClient)
            {
                var missingMods = hostModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All)
                    .Where(host => !localModList.Any(local => local.ModGuid == host.ModGuid))
                    .Select(m => m.ModName);
                
                if (missingMods.Any())
                {
                    string missingModsList = string.Join(", ", missingMods);
                    ModSyncUI.ShowMessage($"Missing required mods: {missingModsList}", ModSyncUI.MessageType.Error);
                    ModSyncUI.ShowMessage("Consider installing missing mods or disconnecting", ModSyncUI.MessageType.Warning);
                }
            }
            
            // For hosts, log the failure for debugging
            if (isHost)
            {
                ModLogger.LogError("Host mod sync failure - check client mod lists");
            }
        }
        
        private void Update()
        {
            // Add safety check to prevent interference with FishNet during critical operations
            if (Time.time < 5f) // Don't run mod sync logic for first 5 seconds to let FishNet initialize
            {
                return;
            }
            
            // Check for F9 hotkey
            CheckF9Hotkey();
            
            // Check if game has started
            CheckForGameStart();
            
            // Check if UI needs to be recreated
            CheckUIHealth();
            
            // Check for chat system timeouts
            CheckChatTimeouts();
            
            // Monitor lobby state changes
            if (Time.time - lastRoleCheck > 2f) // Check every 2 seconds instead of every frame
            {
                bool wasHost = isHost;
                bool wasClient = isClient;
                
                DetermineNetworkRole();
                
                // If network role changed, restart mod sync
                if ((wasHost != isHost) || (wasClient != isClient))
                {
                    StartModSync();
                }
                
                lastRoleCheck = Time.time;
            }
        }
        
        private void CheckUIHealth()
        {
            // Check if UI is working properly
            if (ModSyncUI.Instance == null)
            {
                ModLogger.LogWarning("ModSyncUI instance is null - recreating");
                CreateModSyncUI();
            }
        }
        
        private void CheckChatTimeouts()
        {
            // Don't check timeouts during gameplay
            if (gameStarted)
            {
                return;
            }
            
            // Add additional safety check to prevent interference with FishNet
            if (!isHost || !lobbyLockEnabled)
            {
                return;
            }
            
            // Check for expired timeouts
            var expiredPlayers = new List<string>();
            
            foreach (var kvp in playerResponseTimeouts)
            {
                if (Time.time > kvp.Value)
                {
                    expiredPlayers.Add(kvp.Key);
                }
            }
            
            // Handle expired timeouts
            foreach (var playerName in expiredPlayers)
            {
                ModLogger.LogWarning($"Chat timeout expired for {playerName}");
                playerResponseTimeouts.Remove(playerName);
                
                // If lobby lock is enabled, kick the player
                if (isHost && lobbyLockEnabled)
                {
                    string missingMods = string.Join(", ", localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).Select(m => m.ModName));
                    ModSyncUI.ShowMessage($"{playerName} timed out - no ModSync response", ModSyncUI.MessageType.Error);
                    ModSyncUI.ShowMessage($"Kicking {playerName} for missing ModSync", ModSyncUI.MessageType.Error);
                    
                    // If debug is enabled, send a visible message
                    if (debugSyncMessages && comms != null && comms.Text != null)
                    {
                        string debugMessage = $"DEBUG: Kicking {playerName} for timeout - no ModSync response";
                        comms.Text.Send("Global", debugMessage);
                        ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                    }
                    
                    KickPlayer(playerName, missingMods);
                }
            }
        }
        
        private void CheckForGameStart()
        {
            try
            {
                // Find the MainMenuManager to check GameHasStarted
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                
                if (mainMenuManager != null)
                {
                    // Check if the game has started using the game's own detection
                    if (mainMenuManager.GameHasStarted)
                    {
                        if (!gameStarted)
                        {
                            gameStarted = true;
                            ModLogger.LogInfo("Game started - stopping monitoring");
                            
                            // Check if we're currently in a mod sync timeout as a client
                            if (isClient && waitingForHostResponse && !modSyncCompleted)
                            {
                                inModSyncTimeoutWhenGameStarted = true;
                                ModLogger.LogWarning("Game started while in mod sync timeout - will leave game if no response received");
                                
                                // Check if we have "All" mods (excluding ModSync) that require host sync
                                var allTypePlugins = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All && p.ModGuid != "com.magearena.modsync").ToList();
                                
                                if (allTypePlugins.Count > 0)
                                {
                                    ModLogger.LogWarning($"Game started during mod sync timeout with {allTypePlugins.Count} mods requiring sync. Will leave game if no host response.");
                                    
                                    // Show notification to the client
                                    string missingModsList = string.Join(", ", allTypePlugins.Select(m => m.ModName));
                                    ModSyncUI.ShowMessage($"Game started during mod sync. Will leave if host doesn't respond: {missingModsList}", ModSyncUI.MessageType.Warning);
                                }
                            }
                            
                            // Cancel all mod sync timers when game starts
                            CancelAllModSyncTimers();
                        }
                    }
                    else
                    {
                        // Game has not started or has ended - check if we need to reinitialize
                        if (gameStarted)
                        {
                            gameStarted = false;
                            inModSyncTimeoutWhenGameStarted = false; // Reset the flag when returning to lobby
                            ModLogger.LogInfo("Returned to lobby/menu - reinitializing monitoring");
                            ReinitializeAfterGame();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogWarning($"Error checking for game start: {ex.Message}");
            }
        }
        
        private void ReinitializeAfterGame()
        {
            try
            {
                ModLogger.LogInfo("Reinitializing components after game...");
                
                // Reset flags first
                lobbyDetectionInitialized = false;
                playerJoinDetectionInitialized = false;
                chatSystemInitialized = false;
                
                // Reinitialize UI
                CreateModSyncUI();
                
                // Reinitialize lobby detection
                InitializeLobbyDetection();
                
                // Reinitialize player join detection
                InitializePlayerJoinDetection();
                
                // Reinitialize chat system
                InitializeChatSystem();
                
                // Clear any old data
                connectedPlayers.Clear();
                receivedModLists.Clear();
                playerResponseTimeouts.Clear();
                lobbyLockEnabled = false;
                
                // Reset mod sync state
                modSyncCompleted = false;
                waitingForHostResponse = false;
                
                ModLogger.LogInfo("Reinitialization complete");
                
                // Start a coroutine to ensure UI is ready
                StartCoroutine(EnsureUIReady());
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error during reinitialization: {ex.Message}");
            }
        }
        
        private IEnumerator EnsureUIReady()
        {
            // Wait a few frames for UI to initialize
            yield return new WaitForSeconds(0.1f);
            
            // Check if UI is working
            if (ModSyncUI.Instance == null)
            {
                ModLogger.LogWarning("UI still not ready after reinitialization, recreating...");
                CreateModSyncUI();
            }
            else
            {
                ModLogger.LogInfo("UI reinitialization confirmed successful");
            }
        }
        

        
        private void CheckF9Hotkey()
        {
            // Only allow F9 for hosts in lobby (not during gameplay)
            if (!isHost || gameStarted) return;
            
            // Check if F9 was pressed with cooldown
            if (Input.GetKeyDown(KeyCode.F9) && Time.time - lastF9Press > F9_COOLDOWN)
            {
                lastF9Press = Time.time;
                ToggleLobbyLock();
            }
        }
        
        private void ToggleLobbyLock()
        {
            bool wasEnabled = lobbyLockEnabled;
            lobbyLockEnabled = !lobbyLockEnabled;
            
            string status = lobbyLockEnabled ? "ENABLED" : "DISABLED";
            ModLogger.LogInfo($"Lobby Lock {status}");
            
            // Show visual notification based on new state
            if (lobbyLockEnabled)
            {
                ModSyncUI.ShowMessage("Lobby Lock Enabled! Press F9 to toggle.", ModSyncUI.MessageType.Success);
                
                // If enabling lobby lock, check all existing players
                if (isHost)
                {
                    CheckAllExistingPlayers();
                }
            }
            else
            {
                ModSyncUI.ShowMessage("Lobby Lock Disabled! Press F9 to toggle.", ModSyncUI.MessageType.Error);
                
                // Stop any running mod sync timers when lobby lock is disabled
                if (wasEnabled)
                {
                    StopModSyncTimers();
                }
            }
        }
        
        private void CheckAllExistingPlayers()
        {
            try
            {
                ModLogger.LogInfo("Lobby lock enabled - checking all existing players");
                
                // Clear processed players list so we can re-evaluate everyone with lobby lock enabled
                var previouslyProcessed = new HashSet<string>(processedPlayers);
                processedPlayers.Clear();
                
                if (previouslyProcessed.Count > 0)
                {
                    ModLogger.LogInfo($"Clearing {previouslyProcessed.Count} previously processed players for re-evaluation");
                }
                
                var currentPlayers = GetConnectedPlayers();
                foreach (var player in currentPlayers)
                {
                    // Don't check the host's own mods
                    if (isHost && player == GetPlayerName())
                    {
                        ModLogger.LogInfo($"Skipping mod check for host (self): {player}");
                        continue;
                    }
                    
                    ModLogger.LogInfo($"Checking existing player: {player}");
                    
                    // If we already have their mod list from before, re-evaluate it
                    if (receivedModLists.ContainsKey(player))
                    {
                        ModLogger.LogInfo($"Re-evaluating stored mod list for {player} with lobby lock enabled");
                        
                        // Use a separate method for re-evaluation to avoid signature issues
                        ReEvaluatePlayerMods(player, receivedModLists[player]);
                    }
                    else
                    {
                        // No stored mod list, request fresh mods
                        StartCoroutine(CheckPlayerModsWithTimeout(player));
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error checking existing players: {ex.Message}");
            }
        }

        private void ReEvaluatePlayerMods(string playerName, List<ModInfo> clientMods)
        {
            try
            {
                ModLogger.LogInfo($"Re-evaluating mods for {playerName}");
                
                // Compare with host's mod list (excluding ModSync from both sides)
                var hostAllMods = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All && p.ModGuid != "com.magearena.modsync").ToList();
                var clientAllMods = clientMods.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
                
                // Find mismatches
                var missingOnHost = clientAllMods.Where(client => 
                    !hostAllMods.Any(host => host.ModGuid == client.ModGuid)).ToList();
                
                var missingOnClient = hostAllMods.Where(host => 
                    !clientAllMods.Any(client => client.ModGuid == host.ModGuid)).ToList();
                
                if (missingOnHost.Count == 0 && missingOnClient.Count == 0)
                {
                    // Mods match - send success response
                    string responseMessage = $"[MODSYNC]MODS_MATCH:{playerName}:SUCCESS";
                    comms.Text.Send("Global", responseMessage);
                    
                    // If debug is enabled, also send a visible message
                    if (debugSyncMessages)
                    {
                        string debugMessage = $"DEBUG: Mods match with {playerName} (re-evaluation)";
                        comms.Text.Send("Global", debugMessage);
                        ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                    }
                    
                    ModLogger.LogInfo($"Re-evaluation: Mod sync successful with {playerName} - all required mods match!");
                    ModSyncUI.ShowMessage($"{playerName} has matching mods (re-checked).", ModSyncUI.MessageType.Success);
                    
                    // Mark player as processed
                    processedPlayers.Add(playerName);
                }
                else
                {
                    // Mods don't match - check if we should kick due to lobby lock
                    if (missingOnClient.Count > 0)
                    {
                        ModLogger.LogWarning($"Re-evaluation: Missing on client ({missingOnClient.Count}):");
                        foreach (var mod in missingOnClient)
                        {
                            ModLogger.LogWarning($"  - {mod.ModName} ({mod.ModGuid})");
                        }
                        
                        // Check if lobby lock is enabled and we should kick the player
                        if (isHost && lobbyLockEnabled && missingOnClient.Count > 0)
                        {
                            string missingModsList = string.Join(", ", missingOnClient.Select(m => m.ModName));
                            ModLogger.LogWarning($"LOBBY LOCK RE-EVALUATION: Kicking {playerName} for missing mods: {missingModsList}");
                            
                            // Send mods mismatch response
                            string missingMods = string.Join(",", missingOnClient.Select(m => m.ModName));
                            string responseMessage = $"[MODSYNC]MODS_MISMATCH:{playerName}:{missingMods}";
                            comms.Text.Send("Global", responseMessage);
                            
                            // If debug is enabled, send a visible message
                            if (debugSyncMessages)
                            {
                                string debugMessage = $"DEBUG: Re-evaluation kick - {playerName} missing: {missingModsList}";
                                comms.Text.Send("Global", debugMessage);
                                ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                            }
                            
                            // Show missing mods and kick notifications
                            ModSyncUI.ShowMessage($"Re-evaluation: {playerName} missing required mods: {missingModsList}", ModSyncUI.MessageType.Error);
                            ModSyncUI.ShowMessage($"Kicking {playerName} due to lobby lock", ModSyncUI.MessageType.Error);
                            
                            // Kick the player
                            KickPlayer(playerName, missingModsList);
                        }
                        else if (isHost)
                        {
                            // Show missing mods message for host (without kicking)
                            string missingModsList = string.Join(", ", missingOnClient.Select(m => m.ModName));
                            ModSyncUI.ShowMessage($"Re-evaluation: {playerName} missing mods: {missingModsList}", ModSyncUI.MessageType.Warning);
                        }
                    }
                    
                    // Mark player as processed (even for mismatches)
                    processedPlayers.Add(playerName);
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error re-evaluating player mods for {playerName}: {ex.Message}");
            }
        }
        
        private void StopModSyncTimers()
        {
            try
            {
                ModLogger.LogInfo("Stopping mod sync timers due to lobby lock being disabled");
                
                // Reset mod sync state
                modSyncCompleted = false;
                waitingForHostResponse = false;
                modSyncStartTime = 0f;
                
                // Stop the timeout coroutine if it's running
                if (modSyncTimeoutCoroutine != null)
                {
                    StopCoroutine(modSyncTimeoutCoroutine);
                    modSyncTimeoutCoroutine = null;
                    ModLogger.LogInfo("Stopped mod sync timeout coroutine");
                }
                
                // Clear any pending mod sync data
                receivedModLists.Clear();
                
                ModLogger.LogInfo("Mod sync timers stopped - no new kicks will be processed");
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error stopping mod sync timers: {ex.Message}");
            }
        }
        
        private void KickPlayer(string playerName, string missingMods)
        {
            try
            {
                if (!isHost)
                {
                    ModLogger.LogWarning("Cannot kick player - not host");
                    return;
                }
                
                ModLogger.LogInfo($"Attempting to kick {playerName} for missing mods: {missingMods}");
                
                // Add null checks to prevent conflicts with FishNet
                if (gameStarted)
                {
                    ModLogger.LogWarning($"Cannot kick {playerName} - game has started, using FishNet's built-in system");
                    return;
                }
                
                // Use the game's built-in kick system
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {
                    // Check if the player is in the game's tracking system
                    if (mainMenuManager.kickplayershold.nametosteamid.ContainsKey(playerName))
                    {
                        string steamId = mainMenuManager.kickplayershold.nametosteamid[playerName];
                        ModLogger.LogInfo($"Found Steam ID for {playerName}: {steamId}, kicking via game's kick system...");
                        
                        // Use the game's built-in kick method with Steam ID
                        mainMenuManager.KickPlayer(steamId);
                        
                        ModLogger.LogInfo($"Successfully kicked {playerName} using game's kick system");
                        
                        // Remove from tracking
                        receivedModLists.Remove(playerName);
                        connectedPlayers.Remove(playerName);
                        processedPlayers.Remove(playerName);
                        playerResponseTimeouts.Remove(playerName);
                    }
                    else
                    {
                        ModLogger.LogWarning($"Player {playerName} not found in game's tracking system - cannot kick");
                        ModLogger.LogInfo($"Attempting to retry kick for {playerName} after delay...");
                        
                        // Start a coroutine to retry the kick after a short delay
                        StartCoroutine(RetryKickPlayer(playerName, missingMods));
                    }
                }
                else
                {
                    ModLogger.LogWarning("MainMenuManager or kickplayershold not found - cannot kick player");
                }
                
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error kicking player {playerName}: {ex.Message}");
            }
        }
        
        private IEnumerator RetryKickPlayer(string playerName, string missingMods)
        {
            // Wait a short time for the game's tracking system to update
            yield return new WaitForSeconds(2f);
            
            try
            {
                ModLogger.LogInfo($"Retrying kick for {playerName}...");
                
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {
                    // Check if the player is now in the game's tracking system
                    if (mainMenuManager.kickplayershold.nametosteamid.ContainsKey(playerName))
                    {
                        string steamId = mainMenuManager.kickplayershold.nametosteamid[playerName];
                        ModLogger.LogInfo($"Found Steam ID for {playerName} on retry: {steamId}, kicking via game's kick system...");
                        
                        // Use the game's built-in kick method with Steam ID
                        mainMenuManager.KickPlayer(steamId);
                        
                        ModLogger.LogInfo($"Successfully kicked {playerName} on retry using game's kick system");
                        
                        // Remove from tracking
                        receivedModLists.Remove(playerName);
                        connectedPlayers.Remove(playerName);
                        processedPlayers.Remove(playerName);
                        playerResponseTimeouts.Remove(playerName);
                    }
                    else
                    {
                        ModLogger.LogWarning($"Player {playerName} still not found in game's tracking system after retry");
                    }
                }
                else
                {
                    ModLogger.LogWarning("MainMenuManager or kickplayershold not found on retry - cannot kick player");
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error retrying kick for player {playerName}: {ex.Message}");
            }
        }
        
        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
        }
        
        // Public method to check if mod sync is complete
        public static bool IsModSyncComplete()
        {
            return instance?.modSyncCompleted ?? false;
        }
        
        // Public method to get mod sync status
        public static string GetModSyncStatus()
        {
            if (instance == null) return "ModSync not initialized";
            if (instance.modSyncCompleted) return "Mod sync completed successfully";
            if (instance.waitingForHostResponse) return "Waiting for host response...";
            return "In Progress";
        }
        
        // Public method to check if lobby lock is enabled
        public static bool IsLobbyLockEnabled()
        {
            return instance?.lobbyLockEnabled ?? false;
        }
        
        // Public method to get lobby lock status
        public static string GetLobbyLockStatus()
        {
            if (instance == null) return "ModSync not initialized";
            if (!instance.isHost) return "Lobby Lock only available for hosts";
            return instance.lobbyLockEnabled ? "Lobby Lock ENABLED" : "Lobby Lock DISABLED";
        }
        
        // Public method to handle client kick (called when client is kicked)
        public static void OnClientKicked(string missingMods)
        {
            if (instance == null) return;
            
            ModLogger.LogWarning($"Client was kicked for missing mods: {missingMods}");
            ModSyncUI.ShowMessage($"You were kicked for missing mods: {missingMods}", ModSyncUI.MessageType.Error);
        }
        
        private Dictionary<string, string> GetConnectedPlayersWithSteamIds()
        {
            var players = new Dictionary<string, string>(); // Name -> SteamId
            try
            {
                // Check if we're in a lobby first
                if (BootstrapManager.CurrentLobbyID == 0)
                {
                    return players; // Return empty dictionary if not in lobby
                }
                
                // Use the game's built-in player tracking system
                var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                if (mainMenuManager != null && mainMenuManager.kickplayershold != null)
                {
                    foreach (var kvp in mainMenuManager.kickplayershold.nametosteamid)
                    {
                        players[kvp.Key] = kvp.Value;
                    }
                }
                
                // Only log if we're in a lobby and have players
                // Removed spam logging of connected players
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error getting connected players: {ex.Message}");
            }
            
            return players;
        }

        private List<string> GetConnectedPlayers()
        {
            return GetConnectedPlayersWithSteamIds().Keys.ToList();
        }
        
        private class PlayerInfo
        {
            public string Name { get; set; }
            public string SteamId { get; set; }
        }


        
        private void OnPlayerJoined(string playerName)
        {
            ModLogger.LogInfo($"Player joined: {playerName}");
            
            // Don't check the host's own mods
            if (isHost && playerName == GetPlayerName())
            {
                ModLogger.LogInfo($"Skipping mod check for host (self): {playerName}");
                return;
            }
            
            // If lobby lock is enabled, we need to check their mods
            if (lobbyLockEnabled)
            {
                ModLogger.LogInfo($"Lobby lock enabled - checking mods for {playerName}");
                
                // Wait for their mod list RPC with timeout
                StartCoroutine(CheckPlayerModsWithTimeout(playerName));
            }
            else
            {
                ModLogger.LogInfo($"Lobby lock disabled - allowing {playerName} to join");
            }
        }
        
        private void OnPlayerLeft(string playerName)
        {
            connectedPlayers.Remove(playerName);
            
            // Clear tracking for this player
            receivedModLists.Remove(playerName);
            processedPlayers.Remove(playerName);
            playerResponseTimeouts.Remove(playerName);
        }
        
        private void CancelAllModSyncTimers()
        {
            try
            {
                // Check if there are any active timers to cancel
                bool hasActiveTimers = modSyncTimeoutCoroutine != null || playerResponseTimeouts.Count > 0 || waitingForHostResponse;
                
                if (!hasActiveTimers)
                {
                    // No active timers to cancel
                    return;
                }
                
                ModLogger.LogInfo("Canceling all mod sync timers due to lobby departure");
                
                // Stop the main mod sync timeout coroutine if it's running
                if (modSyncTimeoutCoroutine != null)
                {
                    StopCoroutine(modSyncTimeoutCoroutine);
                    modSyncTimeoutCoroutine = null;
                    ModLogger.LogInfo("Stopped mod sync timeout coroutine");
                }
                
                // Clear all player response timeouts
                if (playerResponseTimeouts.Count > 0)
                {
                    ModLogger.LogInfo($"Clearing {playerResponseTimeouts.Count} player response timeouts");
                    playerResponseTimeouts.Clear();
                }
                
                // Reset mod sync state
                modSyncCompleted = false;
                waitingForHostResponse = false;
                modSyncStartTime = 0f;
                inModSyncTimeoutWhenGameStarted = false; // Reset the game start flag
                
                // Clear all tracking data
                receivedModLists.Clear();
                processedPlayers.Clear();
                
                ModLogger.LogInfo("All mod sync timers canceled and state reset");
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error canceling mod sync timers: {ex.Message}");
            }
        }
        
        private IEnumerator CheckPlayerModsWithTimeout(string playerName)
        {
            // Don't check the host's own mods
            if (isHost && playerName == GetPlayerName())
            {
                ModLogger.LogInfo($"Skipping mod check for host (self): {playerName}");
                yield break;
            }
            
            // Don't check mods during gameplay
            if (gameStarted)
            {
                ModLogger.LogInfo($"Skipping mod check for {playerName} - game has started");
                yield break;
            }
            
            ModLogger.LogInfo($"Starting mod check for {playerName} with {CHAT_RESPONSE_TIMEOUT} second timeout");
            
            // Request mods from the player via chat
            if (comms != null && comms.Text != null)
            {
                string requestMessage = $"[MODSYNC]REQUEST_MODS:{playerName}";
                comms.Text.Send("Global", requestMessage);
                ModLogger.LogInfo($"Sent mod request to {playerName} via chat");
                
                // If debug is enabled, also send a visible message
                if (debugSyncMessages)
                {
                    string debugMessage = $"DEBUG: Requesting mods from {playerName}";
                    comms.Text.Send("Global", debugMessage);
                    ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                }
                
                // Add to timeout tracking
                playerResponseTimeouts[playerName] = Time.time + CHAT_RESPONSE_TIMEOUT;
            }
            else
            {
                ModLogger.LogWarning("Chat system not available for mod request");
                yield break;
            }
            
            float timeout = CHAT_RESPONSE_TIMEOUT;
            float elapsed = 0f;
            
            while (elapsed < timeout)
            {
                // Check if lobby lock is still enabled - if not, stop checking
                if (!lobbyLockEnabled)
                {
                    ModLogger.LogInfo($"Lobby lock disabled while checking {playerName} - stopping mod check");
                    playerResponseTimeouts.Remove(playerName);
                    yield break;
                }
                
                // Check if we received a mod list for this player
                if (HasReceivedModListForPlayer(playerName))
                {
                    ModLogger.LogInfo($"Received mod list for {playerName} - processing");
                    playerResponseTimeouts.Remove(playerName);
                    yield break; // HandleClientModsMessage will handle the rest
                }
                
                elapsed += Time.deltaTime;
                yield return null;
            }
            
            // Timeout - but only kick if lobby lock is still enabled
            if (lobbyLockEnabled)
            {
                ModLogger.LogWarning($"Timeout waiting for mod list from {playerName} - assuming they don't have ModSync");
                
                string missingMods = string.Join(", ", localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).Select(m => m.ModName));
                ModSyncUI.ShowMessage($"{playerName} joined without ModSync or required mods: {missingMods}", ModSyncUI.MessageType.Error);
                ModSyncUI.ShowMessage($"Kicking {playerName} for missing ModSync/mods", ModSyncUI.MessageType.Error);
                
                // If debug is enabled, send a visible message
                if (debugSyncMessages)
                {
                    string debugMessage = $"DEBUG: Kicking {playerName} for timeout - no ModSync response";
                    comms.Text.Send("Global", debugMessage);
                    ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                }
                
                KickPlayer(playerName, missingMods);
            }
            else
            {
                ModLogger.LogInfo($"Lobby lock disabled during timeout for {playerName} - not kicking");
            }
            
            // Clean up timeout tracking
            playerResponseTimeouts.Remove(playerName);
        }
        
        private bool HasReceivedModListForPlayer(string playerName)
        {
            return receivedModLists.ContainsKey(playerName);
        }
        
        private void CreateModSyncUI()
        {
            // Check if UI already exists
            if (ModSyncUI.Instance != null)
            {
                ModLogger.LogInfo("ModSyncUI already exists");
                return;
            }
            
            // Create UI GameObject
            GameObject uiObj = new GameObject("ModSyncUI");
            uiObj.AddComponent<ModSyncUI>();
            DontDestroyOnLoad(uiObj);
            ModLogger.LogInfo("ModSyncUI created");
        }


        
        private void InitializeLobbyDetection()
        {
            if (lobbyDetectionInitialized) return;
            
            try
            {
                // Hook into lobby events by monitoring BootstrapManager
                StartCoroutine(MonitorLobbyEvents());
                lobbyDetectionInitialized = true;
                ModLogger.LogInfo("Lobby detection initialized");
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error initializing lobby detection: {ex.Message}");
            }
        }
        
        private void InitializePlayerJoinDetection()
        {
            if (playerJoinDetectionInitialized) return;
            
            try
            {
                // Start monitoring for player join events
                StartCoroutine(MonitorPlayerJoins());
                playerJoinDetectionInitialized = true;
                ModLogger.LogInfo("Player join detection initialized");
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error initializing player join detection: {ex.Message}");
            }
        }
        
        private void InitializeChatSystem()
        {
            if (chatSystemInitialized) return;
            
            try
            {
                // Find the DissonanceComms singleton
                comms = DissonanceComms.GetSingleton();
                
                if (comms != null && comms.Text != null)
                {
                    // Subscribe to chat messages
                    comms.Text.MessageReceived += OnChatMessageReceived;
                    chatSystemInitialized = true;
                    ModLogger.LogInfo("Chat system initialized successfully");
                }
                else
                {
                    ModLogger.LogWarning("Chat system not ready, will retry later");
                    StartCoroutine(RetryChatSystemInitialization());
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error initializing chat system: {ex.Message}");
            }
        }
        
        private IEnumerator RetryChatSystemInitialization()
        {
            int retryCount = 0;
            const int maxRetries = 10;
            
            while (retryCount < maxRetries && !chatSystemInitialized)
            {
                yield return new WaitForSeconds(1f);
                
                try
                {
                    comms = DissonanceComms.GetSingleton();
                    
                    if (comms != null && comms.Text != null)
                    {
                        // Subscribe to chat messages
                        comms.Text.MessageReceived += OnChatMessageReceived;
                        chatSystemInitialized = true;
                        ModLogger.LogInfo($"Chat system initialized successfully on retry {retryCount + 1}");
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.LogWarning($"Chat system initialization retry {retryCount + 1} failed: {ex.Message}");
                }
                
                retryCount++;
            }
            
            if (!chatSystemInitialized)
            {
                ModLogger.LogError("Failed to initialize chat system after all retries");
            }
        }
        
        private void OnChatMessageReceived(TextMessage message)
        {
            try
            {
                // Add null checks to prevent interference with FishNet
                if (message.Message == null || !message.Message.StartsWith("[MODSYNC]"))
                {
                    return;
                }
                
                // Don't process ModSync messages during gameplay to avoid conflicts
                if (gameStarted)
                {
                    ModLogger.LogInfo($"Ignoring ModSync message during gameplay: {message.Message}");
                    return;
                }
                
                ModLogger.LogInfo($"Received ModSync message: {message.Message}");
                
                // Parse the message properly handling usernames with colons
                var parsedMessage = ParseModSyncMessage(message.Message);
                if (parsedMessage == null)
                {
                    ModLogger.LogWarning($"Failed to parse ModSync message: {message.Message}");
                    return;
                }
                
                string command = parsedMessage.Command;
                string playerName = parsedMessage.PlayerName;
                string data = parsedMessage.Data;
                
                ModLogger.LogInfo($"Parsed command: {command}, player: {playerName}, data: {data}");
                
                // Create a parts array for compatibility with existing handlers
                string[] parts = new string[] { $"[MODSYNC]{command}", playerName, data };
                
                switch (command)
                {
                    case "CLIENT_MODS":
                        HandleClientModsMessage(message, parts);
                        break;
                    case "HOST_MODS":
                        HandleHostModsMessage(message, parts);
                        break;
                    case "MODS_MATCH":
                        HandleModsMatchMessage(message, parts);
                        break;
                    case "MODS_MISMATCH":
                        HandleModsMismatchMessage(message, parts);
                        break;
                    case "REQUEST_MODS":
                        HandleRequestModsMessage(message, parts);
                        break;
                    case "TEST":
                        HandleTestMessage(message, parts);
                        break;
                    default:
                        ModLogger.LogWarning($"Unknown ModSync command: {command} from message: {message.Message}");
                        break;
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error processing chat message: {ex.Message}");
            }
        }
        
        private class ParsedModSyncMessage
        {
            public string Command { get; set; }
            public string PlayerName { get; set; }
            public string Data { get; set; }
        }
        
        private ParsedModSyncMessage ParseModSyncMessage(string message)
        {
            try
            {
                // Remove [MODSYNC] prefix
                string content = message.Substring("[MODSYNC]".Length);
                
                // Find the first colon to get the command
                int firstColonIndex = content.IndexOf(':');
                if (firstColonIndex == -1)
                {
                    ModLogger.LogWarning($"No colon found in ModSync message: {message}");
                    return null;
                }
                
                string command = content.Substring(0, firstColonIndex);
                string remaining = content.Substring(firstColonIndex + 1);
                
                // Handle different message formats
                if (command == "REQUEST_MODS")
                {
                    // Format: REQUEST_MODS:playerName
                    return new ParsedModSyncMessage
                    {
                        Command = command,
                        PlayerName = remaining, // Everything after the first colon is the player name
                        Data = ""
                    };
                }
                else if (command == "CLIENT_MODS" || command == "HOST_MODS")
                {
                    // Format: COMMAND:playerName:modData
                    // Use the list of connected players to identify where the username ends
                    var connectedPlayerNames = GetConnectedPlayers();
                    
                    string playerName = null;
                    string data = null;
                    
                    // Try to match against known player names
                    foreach (var knownPlayer in connectedPlayerNames)
                    {
                        if (remaining.StartsWith(knownPlayer + ":"))
                        {
                            playerName = knownPlayer;
                            data = remaining.Substring(knownPlayer.Length + 1); // +1 for the colon
                            break;
                        }
                    }
                    
                    // If we couldn't match a known player, fall back to the host's name or our own name
                    if (playerName == null)
                    {
                        string hostName = GetHostName();
                        string ourName = GetPlayerName();
                        
                        if (remaining.StartsWith(hostName + ":"))
                        {
                            playerName = hostName;
                            data = remaining.Substring(hostName.Length + 1);
                        }
                        else if (remaining.StartsWith(ourName + ":"))
                        {
                            playerName = ourName;
                            data = remaining.Substring(ourName.Length + 1);
                        }
                    }
                    
                    // If we still couldn't identify the player, use fallback parsing
                    if (playerName == null)
                    {
                        ModLogger.LogInfo($"Player not found in connected list, using fallback parsing for {command} message");
                        var fallbackResult = ParseWithFallbackMethod(remaining, command);
                        if (fallbackResult != null)
                        {
                            playerName = fallbackResult.PlayerName;
                            data = fallbackResult.Data;
                        }
                    }
                    
                    if (playerName == null)
                    {
                        ModLogger.LogWarning($"Could not identify player name in {command} message: {message}");
                        return null;
                    }
                    
                    return new ParsedModSyncMessage
                    {
                        Command = command,
                        PlayerName = playerName,
                        Data = data
                    };
                }
                else if (command == "MODS_MATCH")
                {
                    // Format: MODS_MATCH:playerName:data
                    // Use known player names to identify where the username ends
                    var connectedPlayerNames = GetConnectedPlayers();
                    
                    string playerName = null;
                    string data = "";
                    
                    // Try to match against known player names
                    foreach (var knownPlayer in connectedPlayerNames)
                    {
                        if (remaining.StartsWith(knownPlayer + ":"))
                        {
                            playerName = knownPlayer;
                            data = remaining.Substring(knownPlayer.Length + 1);
                            break;
                        }
                        else if (remaining == knownPlayer)
                        {
                            // No data part, just the player name
                            playerName = knownPlayer;
                            break;
                        }
                    }
                    
                    // Fallback to host/our name
                    if (playerName == null)
                    {
                        string hostName = GetHostName();
                        string ourName = GetPlayerName();
                        
                        if (remaining.StartsWith(hostName + ":"))
                        {
                            playerName = hostName;
                            data = remaining.Substring(hostName.Length + 1);
                        }
                        else if (remaining == hostName)
                        {
                            playerName = hostName;
                        }
                        else if (remaining.StartsWith(ourName + ":"))
                        {
                            playerName = ourName;
                            data = remaining.Substring(ourName.Length + 1);
                        }
                        else if (remaining == ourName)
                        {
                            playerName = ourName;
                        }
                    }
                    
                    // If we still couldn't identify the player, use fallback parsing
                    if (playerName == null)
                    {
                        ModLogger.LogInfo($"Player not found in connected list, using fallback parsing for MODS_MATCH message");
                        var fallbackResult = ParseWithFallbackMethod(remaining, "MODS_MATCH");
                        if (fallbackResult != null)
                        {
                            playerName = fallbackResult.PlayerName;
                            data = fallbackResult.Data;
                        }
                    }
                    
                    if (playerName == null)
                    {
                        ModLogger.LogWarning($"Could not identify player name in MODS_MATCH message: {message}");
                        return null;
                    }
                    
                    return new ParsedModSyncMessage
                    {
                        Command = command,
                        PlayerName = playerName,
                        Data = data
                    };
                }
                else if (command == "MODS_MISMATCH")
                {
                    // Format: MODS_MISMATCH:playerName:missingMods
                    // Use known player names to identify where the username ends
                    var connectedPlayerNames = GetConnectedPlayers();
                    
                    string playerName = null;
                    string missingMods = "";
                    
                    // Try to match against known player names
                    foreach (var knownPlayer in connectedPlayerNames)
                    {
                        if (remaining.StartsWith(knownPlayer + ":"))
                        {
                            playerName = knownPlayer;
                            missingMods = remaining.Substring(knownPlayer.Length + 1);
                            break;
                        }
                    }
                    
                    // Fallback to host/our name
                    if (playerName == null)
                    {
                        string hostName = GetHostName();
                        string ourName = GetPlayerName();
                        
                        if (remaining.StartsWith(hostName + ":"))
                        {
                            playerName = hostName;
                            missingMods = remaining.Substring(hostName.Length + 1);
                        }
                        else if (remaining.StartsWith(ourName + ":"))
                        {
                            playerName = ourName;
                            missingMods = remaining.Substring(ourName.Length + 1);
                        }
                    }
                    
                    // If we still couldn't identify the player, use fallback parsing
                    if (playerName == null)
                    {
                        ModLogger.LogInfo($"Player not found in connected list, using fallback parsing for MODS_MISMATCH message");
                        var fallbackResult = ParseWithFallbackMethod(remaining, "MODS_MISMATCH");
                        if (fallbackResult != null)
                        {
                            playerName = fallbackResult.PlayerName;
                            missingMods = fallbackResult.Data;
                        }
                    }
                    
                    if (playerName == null)
                    {
                        ModLogger.LogWarning($"Could not identify player name in MODS_MISMATCH message: {message}");
                        return null;
                    }
                    
                    return new ParsedModSyncMessage
                    {
                        Command = command,
                        PlayerName = playerName,
                        Data = missingMods
                    };
                }
                else if (command == "TEST")
                {
                    // Format: TEST:playerName:testData
                    // Use known player names to identify where the username ends
                    var connectedPlayerNames = GetConnectedPlayers();
                    
                    string playerName = null;
                    string data = "";
                    
                    // Try to match against known player names
                    foreach (var knownPlayer in connectedPlayerNames)
                    {
                        if (remaining.StartsWith(knownPlayer + ":"))
                        {
                            playerName = knownPlayer;
                            data = remaining.Substring(knownPlayer.Length + 1);
                            break;
                        }
                    }
                    
                    // Fallback to host/our name
                    if (playerName == null)
                    {
                        string hostName = GetHostName();
                        string ourName = GetPlayerName();
                        
                        if (remaining.StartsWith(hostName + ":"))
                        {
                            playerName = hostName;
                            data = remaining.Substring(hostName.Length + 1);
                        }
                        else if (remaining.StartsWith(ourName + ":"))
                        {
                            playerName = ourName;
                            data = remaining.Substring(ourName.Length + 1);
                        }
                    }
                    
                    // If we still couldn't identify the player, use fallback parsing
                    if (playerName == null)
                    {
                        ModLogger.LogInfo($"Player not found in connected list, using fallback parsing for TEST message");
                        var fallbackResult = ParseWithFallbackMethod(remaining, "TEST");
                        if (fallbackResult != null)
                        {
                            playerName = fallbackResult.PlayerName;
                            data = fallbackResult.Data;
                        }
                    }
                    
                    if (playerName == null)
                    {
                        ModLogger.LogWarning($"Could not identify player name in TEST message: {message}");
                        return null;
                    }
                    
                    return new ParsedModSyncMessage
                    {
                        Command = command,
                        PlayerName = playerName,
                        Data = data
                    };
                }
                else
                {
                    ModLogger.LogWarning($"Unknown ModSync command: {command}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error parsing ModSync message: {ex.Message}");
                return null;
            }
        }

        private class FallbackParseResult
        {
            public string PlayerName { get; set; }
            public string Data { get; set; }
        }

        private FallbackParseResult ParseWithFallbackMethod(string remaining, string command)
        {
            try
            {
                ModLogger.LogInfo($"Using fallback parsing for: {remaining}");
                
                // For CLIENT_MODS and HOST_MODS, we need to find where mod data starts
                if (command == "CLIENT_MODS" || command == "HOST_MODS")
                {
                    // Look for GUID pattern, but be more careful about it
                    string[] segments = remaining.Split(':');
                    
                    for (int i = 0; i < segments.Length; i++)
                    {
                        string segment = segments[i];
                        
                        // Check if this looks like a GUID with improved heuristics
                        if (IsLikelyGuid(segment))
                        {
                            // Found likely GUID, everything before this (minus trailing colon) is the player name
                            int guidStartIndex = remaining.IndexOf(segment);
                            if (guidStartIndex > 1)
                            {
                                string playerName = remaining.Substring(0, guidStartIndex - 1);
                                string data = remaining.Substring(guidStartIndex);
                                
                                ModLogger.LogInfo($"Fallback parsing identified - Player: '{playerName}', Data: '{data}'");
                                
                                return new FallbackParseResult
                                {
                                    PlayerName = playerName,
                                    Data = data
                                };
                            }
                        }
                    }
                    
                    ModLogger.LogWarning($"Fallback parsing failed to find GUID pattern in: {remaining}");
                    return null;
                }
                else
                {
                    // For other commands, try to find the last colon
                    int lastColonIndex = remaining.LastIndexOf(':');
                    if (lastColonIndex > 0)
                    {
                        string playerName = remaining.Substring(0, lastColonIndex);
                        string data = remaining.Substring(lastColonIndex + 1);
                        
                        ModLogger.LogInfo($"Fallback parsing (last colon) identified - Player: '{playerName}', Data: '{data}'");
                        
                        return new FallbackParseResult
                        {
                            PlayerName = playerName,
                            Data = data
                        };
                    }
                    else
                    {
                        // No colon found, assume entire string is player name
                        ModLogger.LogInfo($"Fallback parsing (no colon) identified - Player: '{remaining}', Data: ''");
                        
                        return new FallbackParseResult
                        {
                            PlayerName = remaining,
                            Data = ""
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error in fallback parsing: {ex.Message}");
                return null;
            }
        }

        private bool IsLikelyGuid(string segment)
        {
            try
            {
                // Check if segment looks like a GUID
                if (string.IsNullOrEmpty(segment) || !segment.Contains("."))
                    return false;
                
                string[] parts = segment.Split('.');
                
                // Must have at least 3 parts (e.g., com.domain.mod)
                if (parts.Length < 3)
                    return false;
                
                // First part should look like a domain TLD or company identifier
                string firstPart = parts[0].ToLower();
                if (firstPart.Length < 2)
                    return false;
                
                // Check for common GUID prefixes
                if (firstPart == "com" || firstPart == "net" || firstPart == "org" || 
                    firstPart == "io" || firstPart == "dev" || firstPart == "app")
                    return true;
                
                // Allow other patterns that look like reverse domain notation
                // Second part should be reasonable length (company/author name)
                if (parts[1].Length >= 2 && parts[1].Length <= 20)
                {
                    // Third part should be reasonable length (mod name part)
                    if (parts[2].Length >= 2 && parts[2].Length <= 30)
                        return true;
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }
        
        private void HandleClientModsMessage(TextMessage message, string[] parts)
        {
            // Double-check we're not in gameplay
            if (gameStarted)
            {
                ModLogger.LogInfo($"Ignoring CLIENT_MODS message during gameplay from {(parts.Length > 2 ? parts[2] : "unknown")}");
                return;
            }
            
            if (parts.Length < 3)
            {
                ModLogger.LogWarning($"Invalid CLIENT_MODS message format: {message.Message}");
                return;
            }
            
            string playerName = parts[1];
            string modListString = parts[2];
            
            ModLogger.LogInfo($"Received client mods from {playerName}: {modListString}");
            
            // Parse mod list
            var clientMods = new List<ModInfo>();
            string[] modEntries = modListString.Split(';');
            
            foreach (string modEntry in modEntries)
            {
                if (string.IsNullOrEmpty(modEntry)) continue;
                
                string[] modParts = modEntry.Split(':');
                if (modParts.Length >= 3)
                {
                    var modInfo = new ModInfo
                    {
                        ModGuid = modParts[0],
                        ModName = modParts[1],
                        SyncType = ParseModSyncType(modParts[2]),
                        HasModSyncVariable = true
                    };
                    clientMods.Add(modInfo);
                }
                else
                {
                    ModLogger.LogWarning($"Invalid mod entry format: {modEntry}");
                }
            }
            
            // Store the received mod list
            receivedModLists[playerName] = clientMods;
            
            // Check if we've already processed this player AND lobby lock wasn't recently enabled
            // If lobby lock is enabled and we haven't processed this specific combination, we should re-evaluate
            bool shouldReprocess = false;
            if (processedPlayers.Contains(playerName))
            {
                // Check if lobby lock is now enabled and we should re-evaluate
                if (lobbyLockEnabled)
                {
                    ModLogger.LogInfo($"Lobby lock is enabled - re-evaluating previously processed player {playerName}");
                    shouldReprocess = true;
                    // Remove from processed players so we can re-evaluate with lobby lock context
                    processedPlayers.Remove(playerName);
                }
                else
                {
                    ModLogger.LogInfo($"Already processed {playerName} and lobby lock not enabled - skipping duplicate message");
                    return;
                }
            }
            
            // Compare with host's mod list (excluding ModSync from both sides)
            var hostAllMods = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All && p.ModGuid != "com.magearena.modsync").ToList();
            var clientAllMods = clientMods.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
            
            // Find mismatches
            var missingOnHost = clientAllMods.Where(client => 
                !hostAllMods.Any(host => host.ModGuid == client.ModGuid)).ToList();
            
            var missingOnClient = hostAllMods.Where(host => 
                !clientAllMods.Any(client => client.ModGuid == host.ModGuid)).ToList();
            
            if (missingOnHost.Count == 0 && missingOnClient.Count == 0)
            {
                // Mods match - send success response
                string responseMessage = $"[MODSYNC]MODS_MATCH:{playerName}:SUCCESS";
                comms.Text.Send("Global", responseMessage);
                
                // If debug is enabled, also send a visible message
                if (debugSyncMessages)
                {
                    string debugMessage = $"DEBUG: Mods match with {playerName}";
                    comms.Text.Send("Global", debugMessage);
                    ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                }
                
                ModLogger.LogInfo($"Mod sync successful with {playerName} - all required mods match!");
                ModSyncUI.ShowMessage($"{playerName} joined with required mods.", ModSyncUI.MessageType.Success);
                
                // Mark player as processed
                processedPlayers.Add(playerName);
            }
            else
            {
                // Mods don't match - send failure response
                string missingMods = string.Join(",", missingOnClient.Select(m => m.ModName));
                string responseMessage = $"[MODSYNC]MODS_MISMATCH:{playerName}:{missingMods}";
                comms.Text.Send("Global", responseMessage);
                
                // If debug is enabled, also send a visible message
                if (debugSyncMessages)
                {
                    string debugMessage = $"DEBUG: Mods mismatch with {playerName} (Missing: {missingMods})";
                    comms.Text.Send("Global", debugMessage);
                    ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                }
                
                ModLogger.LogWarning($"Mod sync failed with {playerName} - mod mismatch detected");
                
                if (missingOnClient.Count > 0)
                {
                    ModLogger.LogWarning($"Missing on client ({missingOnClient.Count}):");
                    foreach (var mod in missingOnClient)
                    {
                        ModLogger.LogWarning($"  - {mod.ModName} ({mod.ModGuid})");
                    }
                    
                    // Check if lobby lock is enabled and we should kick the player
                    if (isHost && lobbyLockEnabled && missingOnClient.Count > 0)
                    {
                        string missingModsList = string.Join(", ", missingOnClient.Select(m => m.ModName));
                        ModLogger.LogWarning($"LOBBY LOCK: Kicking {playerName} for missing mods: {missingModsList}");
                        
                        // If this is a re-evaluation due to lobby lock being enabled, mention that
                        if (shouldReprocess)
                        {
                            ModLogger.LogInfo($"Re-evaluation due to lobby lock: {playerName} has mod mismatches, kicking");
                        }
                        
                        // If debug is enabled, send a visible message
                        if (debugSyncMessages)
                        {
                            string debugMessage = $"DEBUG: Kicking {playerName} for missing mods: {missingModsList}";
                            comms.Text.Send("Global", debugMessage);
                            ModLogger.LogInfo($"Debug message sent: {debugMessage}");
                        }
                        
                        // Show missing mods and kick notifications
                        ModSyncUI.ShowMessage($"{playerName} joined without required mods: {missingModsList}", ModSyncUI.MessageType.Error);
                        ModSyncUI.ShowMessage($"Kicking {playerName} for mismatched mods", ModSyncUI.MessageType.Error);
                        
                        // Kick the player
                        KickPlayer(playerName, missingModsList);
                    }
                    else if (isHost)
                    {
                        // Show missing mods message for host (without kicking)
                        string missingModsList = string.Join(", ", missingOnClient.Select(m => m.ModName));
                        ModSyncUI.ShowMessage($"{playerName} joined without required mods: {missingModsList}", ModSyncUI.MessageType.Error);
                        
                        if (!lobbyLockEnabled)
                        {
                            ModSyncUI.ShowMessage("Lobby lock disabled - not kicking", ModSyncUI.MessageType.Warning);
                        }
                    }
                }
                
                // Mark player as processed (even for mismatches)
                processedPlayers.Add(playerName);
            }
        }
        
        private void HandleHostModsMessage(TextMessage message, string[] parts)
        {
            // Double-check we're not in gameplay
            if (gameStarted)
            {
                ModLogger.LogInfo("Ignoring HOST_MODS message during gameplay");
                return;
            }
            
            if (parts.Length < 3)
            {
                ModLogger.LogWarning($"Invalid HOST_MODS message format: {message.Message}");
                return;
            }
            
            string modListString = parts[2];
            
            ModLogger.LogInfo($"Received host mods: {modListString}");
            
            // Parse mod list
            var hostMods = new List<ModInfo>();
            string[] modEntries = modListString.Split(';');
            
            foreach (string modEntry in modEntries)
            {
                string[] modParts = modEntry.Split('|');
                if (modParts.Length >= 3)
                {
                    var modInfo = new ModInfo
                    {
                        ModGuid = modParts[0],
                        ModName = modParts[1],
                        SyncType = ParseModSyncType(modParts[2]),
                        HasModSyncVariable = true
                    };
                    hostMods.Add(modInfo);
                }
            }
            
            // Store host mod list
            hostModList = hostMods;
            waitingForHostResponse = false;
            
            // Compare with local mod list
            CompareModLists();
        }
        
        private void HandleModsMatchMessage(TextMessage message, string[] parts)
        {
            if (parts.Length < 3)
            {
                ModLogger.LogWarning($"Invalid MODS_MATCH message format: {message.Message}");
                return;
            }
            
            string playerName = parts[1];
            string result = parts.Length > 2 ? parts[2] : "SUCCESS";
            
            ModLogger.LogInfo($"Received mods match confirmation for {playerName}: {result}");
            
            // Remove from timeout tracking
            playerResponseTimeouts.Remove(playerName);
            
            if (result == "SUCCESS")
            {
                // If we're a client and this is a response from the host, mark mod sync as complete
                if (isClient && playerName == GetPlayerName())
                {
                    ModLogger.LogInfo("Received success response from host - mod sync complete!");
                    modSyncCompleted = true;
                    waitingForHostResponse = false;
                    modSyncRetrySent = false; // Reset retry flag
                    
                    // Stop the timeout coroutine if it's running
                    if (modSyncTimeoutCoroutine != null)
                    {
                        StopCoroutine(modSyncTimeoutCoroutine);
                        modSyncTimeoutCoroutine = null;
                        ModLogger.LogInfo("Stopped mod sync timeout coroutine");
                    }
                    
                    ModSyncUI.ShowMessage("ModSync done! You have the correct mods.", ModSyncUI.MessageType.Success);
                }
                else if (isHost)
                {
                    // Host received confirmation about a client
                    ModSyncUI.ShowMessage($"{playerName} has matching mods.", ModSyncUI.MessageType.Success);
                }
                else
                {
                    ModLogger.LogInfo($"Received MODS_MATCH for {playerName} but not applicable to current role (isClient: {isClient}, isHost: {isHost})");
                }
            }
            else
            {
                ModLogger.LogWarning($"Received MODS_MATCH with non-SUCCESS result: {result}");
            }
        }
        
        private void HandleModsMismatchMessage(TextMessage message, string[] parts)
        {
            if (parts.Length < 3)
            {
                ModLogger.LogWarning($"Invalid MODS_MISMATCH message format: {message.Message}");
                return;
            }
            
            string playerName = parts[1];
            string missingMods = parts[2];
            
            ModLogger.LogInfo($"Received mods mismatch for {playerName}: {missingMods}");
            
            // Remove from timeout tracking
            playerResponseTimeouts.Remove(playerName);
            
            // If we're a client and this is a response from the host, handle the mismatch
            if (isClient && playerName == GetPlayerName())
            {
                ModLogger.LogWarning("Received mismatch response from host - mod sync failed!");
                modSyncCompleted = true;
                waitingForHostResponse = false;
                modSyncRetrySent = false; // Reset retry flag
                
                // Stop the timeout coroutine if it's running
                if (modSyncTimeoutCoroutine != null)
                {
                    StopCoroutine(modSyncTimeoutCoroutine);
                    modSyncTimeoutCoroutine = null;
                    ModLogger.LogInfo("Stopped mod sync timeout coroutine due to mismatch");
                }
                
                // Parse the missing mods to understand what we're missing
                string[] missingModNames = missingMods.Split(',');
                
                // Check if any of our "All" tagged mods are in the missing list
                var ourAllMods = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All && p.ModGuid != "com.magearena.modsync").ToList();
                var ourAllModNames = ourAllMods.Select(m => m.ModName).ToList();
                
                // Check if any of our "All" mods are missing on the host
                var ourAllModsMissingOnHost = missingModNames.Where(missing => ourAllModNames.Contains(missing.Trim())).ToList();
                
                if (ourAllModsMissingOnHost.Count > 0)
                {
                    // The host is missing some of our "All" tagged mods - this is a problem
                    ModLogger.LogWarning($"Host is missing our 'All' tagged mods: {string.Join(", ", ourAllModsMissingOnHost)}. Leaving lobby.");
                    
                    // Show notification to the client
                    string missingAllMods = string.Join(", ", ourAllModsMissingOnHost);
                    ModSyncUI.ShowMessage($"Host is missing your required mods: {missingAllMods}. Leaving lobby.", ModSyncUI.MessageType.Error);
                    
                    // Leave the lobby after a short delay to show the message
                    StartCoroutine(LeaveLobbyAfterDelay(3f, $"Host missing required 'All' mods: {missingAllMods}"));
                }
                else
                {
                    // We're missing mods from the host, but our "All" mods are present on host
                    // This is a normal mismatch where we need to install missing mods
                    ModLogger.LogInfo($"We're missing mods from host: {missingMods}, but our 'All' mods are present on host");
                    
                    // Show the normal mismatch message - user can choose to stay or leave
                    ModSyncUI.ShowMessage($"You are missing mods: {missingMods}", ModSyncUI.MessageType.Error);
                    ModSyncUI.ShowMessage("Consider installing missing mods", ModSyncUI.MessageType.Warning);
                    
                    // Don't automatically leave - let the user decide
                    // The host may or may not kick us depending on their lobby lock setting
                }
            }
            else if (isHost)
            {
                // Host received mismatch about a client
                ModSyncUI.ShowMessage($"{playerName} has mismatched mods: {missingMods}", ModSyncUI.MessageType.Error);
            }
        }
        
        private void HandleRequestModsMessage(TextMessage message, string[] parts)
        {
            // Double-check we're not in gameplay
            if (gameStarted)
            {
                ModLogger.LogInfo($"Ignoring REQUEST_MODS message during gameplay from {(parts.Length > 1 ? parts[1] : "unknown")}");
                return;
            }
            
            if (parts.Length < 2)
            {
                ModLogger.LogWarning($"Invalid REQUEST_MODS message format: {message.Message}");
                return;
            }
            
            string requesterName = parts[1];
            
            ModLogger.LogInfo($"Received mod request from {requesterName}");
            
            // Host has ModSync (confirmed by REQUEST_MODS message), so send our mod list
            // Get all mods that require sync (excluding ModSync itself)
            var syncRequiredPlugins = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All && p.ModGuid != "com.magearena.modsync").ToList();
            
            // Prepare mod list data for response
            var modListData = new List<string>();
            foreach (var mod in syncRequiredPlugins)
            {
                modListData.Add($"{mod.ModGuid}:{mod.ModName}:{mod.SyncType}");
            }
            
            string modListString = string.Join(";", modListData);
            string responseMessage = $"[MODSYNC]CLIENT_MODS:{GetPlayerName()}:{modListString}";
            comms.Text.Send("Global", responseMessage);
            
            // If debug is enabled, also send a visible message
            if (debugSyncMessages)
            {
                string debugMessage = $"DEBUG: Sent mod list to {requesterName} ({syncRequiredPlugins.Count} mods)";
                comms.Text.Send("Global", debugMessage);
                ModLogger.LogInfo($"Debug message sent: {debugMessage}");
            }
            
            ModLogger.LogInfo($"Sent mod list to {requesterName} ({syncRequiredPlugins.Count} mods)");
        }
        
        private void HandleTestMessage(TextMessage message, string[] parts)
        {
            if (parts.Length < 3)
            {
                ModLogger.LogWarning($"Invalid TEST message format: {message.Message}");
                return;
            }
            
            string testContent = parts[2];
            ModLogger.LogInfo($"Received test message: {testContent}");
            ModSyncUI.ShowMessage($"Chat test received: {testContent}", ModSyncUI.MessageType.Info);
        }
        
        private IEnumerator LeaveLobbyAfterDelay(float delay, string reason)
        {
            ModLogger.LogInfo($"Will leave lobby in {delay} seconds. Reason: {reason}");
            
            yield return new WaitForSeconds(delay);
            
            try
            {
                if (BootstrapManager.CurrentLobbyID != 0)
                {
                    ModLogger.LogInfo($"Leaving lobby {BootstrapManager.CurrentLobbyID} due to: {reason}");
                    
                                            // Find MainMenuManager and use its LeaveLobby method
                        var mainMenuManager = FindAnyObjectByType<MainMenuManager>();
                    if (mainMenuManager != null)
                    {
                        mainMenuManager.LeaveLobby();
                        ModSyncUI.ShowMessage($"Left lobby: {reason}", ModSyncUI.MessageType.Warning);
                    }
                    else
                    {
                        ModLogger.LogWarning("MainMenuManager not found, cannot leave lobby");
                    }
                }
                else
                {
                    ModLogger.LogWarning("Not in a lobby, cannot leave");
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error leaving lobby: {ex.Message}");
            }
        }
        
        private IEnumerator LeaveGameAfterDelay(float delay, string reason)
        {
            ModLogger.LogInfo($"Will leave game in {delay} seconds. Reason: {reason}");
            
            yield return new WaitForSeconds(delay);
            
            try
            {
                ModLogger.LogInfo($"Leaving game due to: {reason}");
                
                                        // Find MainMenuManager and use its LeaveGame method
                        var mainMenuManager = FindAnyObjectByType<MainMenuManager>();
                if (mainMenuManager != null)
                {
                    // Ensure proper UI cleanup before leaving game
                    // This is especially important when leaving from InGameLobby state
                    try
                    {
                        // Use reflection to access private UI fields for InGameLobby and startstartGameButton
                        try
                        {
                            var inGameLobbyField = typeof(MainMenuManager).GetField("InGameLobby", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (inGameLobbyField != null)
                            {
                                var inGameLobby = inGameLobbyField.GetValue(mainMenuManager) as GameObject;
                                if (inGameLobby != null && inGameLobby.activeSelf)
                                {
                                    ModLogger.LogInfo("Cleaning up InGameLobby UI element before leaving game");
                                    inGameLobby.SetActive(false);
                                }
                            }
                            
                            var startstartGameButtonField = typeof(MainMenuManager).GetField("startstartGameButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (startstartGameButtonField != null)
                            {
                                var startstartGameButton = startstartGameButtonField.GetValue(mainMenuManager) as GameObject;
                                if (startstartGameButton != null && startstartGameButton.gameObject.activeSelf)
                                {
                                    ModLogger.LogInfo("Cleaning up start game button before leaving game");
                                    startstartGameButton.gameObject.SetActive(false);
                                }
                            }
                        }
                        catch (Exception reflectionEx)
                        {
                            ModLogger.LogWarning($"Error using reflection to clean up InGameLobby/startstartGameButton UI: {reflectionEx.Message}");
                        }
                        
                        // Ensure other UI elements are properly reset
                        if (mainMenuManager.TextChatHolder != null && mainMenuManager.TextChatHolder.activeSelf)
                        {
                            mainMenuManager.TextChatHolder.SetActive(false);
                        }
                        
                        // Clear any active UI elements that might cause issues
                        if (mainMenuManager.InGameMenuHolder != null && mainMenuManager.InGameMenuHolder.activeSelf)
                        {
                            mainMenuManager.InGameMenuHolder.SetActive(false);
                        }
                        
                        // Clean up any other UI elements that might be active
                        if (mainMenuManager.InGameMenu != null && mainMenuManager.InGameMenu.activeSelf)
                        {
                            mainMenuManager.InGameMenu.SetActive(false);
                        }
                        
                        // Use reflection to access private UI fields
                        try
                        {
                            var menuScreenField = typeof(MainMenuManager).GetField("menuScreen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (menuScreenField != null)
                            {
                                var menuScreen = menuScreenField.GetValue(mainMenuManager) as GameObject;
                                if (menuScreen != null && menuScreen.activeSelf)
                                {
                                    ModLogger.LogInfo("Cleaning up menuScreen UI element before leaving game");
                                    menuScreen.SetActive(false);
                                }
                            }
                            
                            var lobbyScreenField = typeof(MainMenuManager).GetField("lobbyScreen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (lobbyScreenField != null)
                            {
                                var lobbyScreen = lobbyScreenField.GetValue(mainMenuManager) as GameObject;
                                if (lobbyScreen != null && lobbyScreen.activeSelf)
                                {
                                    ModLogger.LogInfo("Cleaning up lobbyScreen UI element before leaving game");
                                    lobbyScreen.SetActive(false);
                                }
                            }
                        }
                        catch (Exception reflectionEx)
                        {
                            ModLogger.LogWarning($"Error using reflection to clean up UI: {reflectionEx.Message}");
                        }
                        
                        // Reset game state flags
                        mainMenuManager.GameHasStarted = false;
                    }
                    catch (Exception cleanupEx)
                    {
                        ModLogger.LogWarning($"Error during UI cleanup: {cleanupEx.Message}");
                    }
                    
                    // Now call the game's LeaveGame method
                    mainMenuManager.LeaveGame();
                    ModSyncUI.ShowMessage($"Left game: {reason}", ModSyncUI.MessageType.Error);
                }
                else
                {
                    ModLogger.LogWarning("MainMenuManager not found, cannot leave game");
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error leaving game: {ex.Message}");
            }
        }
        
        private IEnumerator MonitorLobbyEvents()
        {
            ulong previousLobbyID = 0;
            bool hasShownHostMessages = false;
            bool hasShownClientMessages = false;
            
            while (!gameStarted)
            {
                yield return new WaitForSeconds(0.5f);
                
                try
                {
                    // Check if we joined a new lobby
                    if (BootstrapManager.CurrentLobbyID != 0 && BootstrapManager.CurrentLobbyID != previousLobbyID)
                    {
                        previousLobbyID = BootstrapManager.CurrentLobbyID;
                        
                        // Show messages for hosts
                        if (isHost && !hasShownHostMessages)
                        {
                            hasShownHostMessages = true;
                            ShowHostLobbyMessages();
                        }
                        // Show messages for clients
                        else if (isClient && !hasShownClientMessages)
                        {
                            hasShownClientMessages = true;
                            ShowClientLobbyMessages();
                        }
                    }
                    // Check if we left a lobby
                    else if (BootstrapManager.CurrentLobbyID == 0 && previousLobbyID != 0)
                    {
                        previousLobbyID = 0;
                        hasShownHostMessages = false;
                        hasShownClientMessages = false;
                        
                        // Cancel all mod sync timers when leaving lobby
                        CancelAllModSyncTimers();
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.LogWarning($"Error monitoring lobby events: {ex.Message}");
                }
            }
            
            ModLogger.LogInfo("Game started - stopping lobby event monitoring");
        }
        
        private IEnumerator MonitorPlayerJoins()
        {
            ModLogger.LogInfo("Player join detection initialized");
            
            var previousPlayers = new HashSet<string>();
            var hostPlayerName = GetPlayerName();
            var isFirstRun = true;
            var lastLobbyId = 0UL;
            
            while (!gameStarted)
            {
                // Check if we're in a lobby
                var currentLobbyId = BootstrapManager.CurrentLobbyID;
                
                // If we're not in a lobby, clear tracking and wait
                if (currentLobbyId == 0)
                {
                    if (lastLobbyId != 0)
                    {
                        ModLogger.LogInfo("Lobby closed, clearing player tracking");
                        previousPlayers.Clear();
                        receivedModLists.Clear();
                        connectedPlayers.Clear();
                        processedPlayers.Clear();
                        playerResponseTimeouts.Clear();
                        
                        // Cancel all mod sync timers when leaving lobby
                        CancelAllModSyncTimers();
                        
                        lastLobbyId = 0;
                        isFirstRun = true;
                    }
                    yield return new WaitForSeconds(1f);
                    continue;
                }
                
                // If we just joined a new lobby, reset tracking
                if (currentLobbyId != lastLobbyId)
                {
                    ModLogger.LogInfo($"Joined new lobby: {currentLobbyId}");
                    previousPlayers.Clear();
                    receivedModLists.Clear();
                    connectedPlayers.Clear();
                    processedPlayers.Clear();
                    playerResponseTimeouts.Clear();
                    lastLobbyId = currentLobbyId;
                    isFirstRun = true;
                }
                
                // Get current connected players from game's tracking system
                var currentPlayers = GetConnectedPlayers();
                var currentPlayerSet = new HashSet<string>(currentPlayers);
                
                // Skip the first run to avoid detecting the host as a "new player"
                if (isFirstRun)
                {
                    previousPlayers = currentPlayerSet;
                    isFirstRun = false;
                    ModLogger.LogInfo($"Initializing player tracking. Host: {hostPlayerName}");
                    yield return new WaitForSeconds(1f);
                    continue;
                }
                
                try
                {
                    // Check for new players
                    foreach (var playerName in currentPlayers)
                    {
                        // Don't detect the host as a new player
                        if (!previousPlayers.Contains(playerName) && playerName != hostPlayerName)
                        {
                            OnPlayerJoined(playerName);
                        }
                    }
                    
                    // Check for players who left
                    foreach (var playerName in previousPlayers)
                    {
                        if (!currentPlayerSet.Contains(playerName) && playerName != hostPlayerName)
                        {
                            OnPlayerLeft(playerName);
                        }
                    }
                    
                    previousPlayers = currentPlayerSet;
                }
                catch (Exception ex)
                {
                    ModLogger.LogError($"Error in player join monitoring: {ex.Message}");
                }
                
                // Wait before next check
                yield return new WaitForSeconds(1f);
            }
            
            ModLogger.LogInfo("Game started - stopping player join monitoring");
        }
        

        
        private void ShowHostLobbyMessages()
        {
            try
            {
                // Set ModSync info in lobby data so clients can detect it
                SetHostModSyncInLobby();
                
                // Show "Created Lobby with mods" message
                var allTypeMods = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
                if (allTypeMods.Count > 0)
                {
                    string modList = string.Join(", ", allTypeMods.Select(m => m.ModName));
                    ModSyncUI.ShowMessage($"Created Lobby with mods: {modList}", ModSyncUI.MessageType.Info);
                }
                else
                {
                    ModSyncUI.ShowMessage("Created Lobby with mods: None", ModSyncUI.MessageType.Info);
                }
                
                // Show lobby lock status
                if (lobbyLockEnabled)
                {
                    ModSyncUI.ShowMessage("Lobby Lock Enabled! Press F9 to toggle.", ModSyncUI.MessageType.Success);
                }
                else
                {
                    ModSyncUI.ShowMessage("Lobby Lock Disabled! Press F9 to toggle.", ModSyncUI.MessageType.Error);
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error showing host lobby messages: {ex.Message}");
            }
        }
        
        private void SetHostModSyncInLobby()
        {
            try
            {
                if (BootstrapManager.CurrentLobbyID == 0)
                {
                    ModLogger.LogWarning("Not in a lobby, cannot set ModSync data");
                    return;
                }
                
                // Set ModSync info in lobby data
                SteamMatchmaking.SetLobbyData(new CSteamID(BootstrapManager.CurrentLobbyID), "modsync", "enabled");
                SteamMatchmaking.SetLobbyData(new CSteamID(BootstrapManager.CurrentLobbyID), "owner_modsync", "enabled");
                
                ModLogger.LogInfo("Set ModSync info in lobby data for client detection");
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error setting ModSync info in lobby: {ex.Message}");
            }
        }
        
        private void ShowClientLobbyMessages()
        {
            try
            {
                // Check if host has ModSync by looking for ModSync in the lobby data
                bool hostHasModSync = CheckIfHostHasModSync();
                
                if (hostHasModSync)
                {
                    ModSyncUI.ShowMessage("Host has ModSync, checking...", ModSyncUI.MessageType.Info);
                }
                else
                {
                    ModSyncUI.ShowMessage("Host doesn't have ModSync - skipping", ModSyncUI.MessageType.Error);
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error showing client lobby messages: {ex.Message}");
            }
        }
        
        private bool CheckIfHostHasModSync()
        {
            try
            {
                // Check if we're in a lobby
                if (BootstrapManager.CurrentLobbyID == 0)
                {
                    ModLogger.LogWarning("Not in a lobby, cannot check host ModSync");
                    return false;
                }
                
                // Try to get ModSync info from lobby data
                string lobbyModSync = SteamMatchmaking.GetLobbyData(new CSteamID(BootstrapManager.CurrentLobbyID), "modsync");
                
                if (!string.IsNullOrEmpty(lobbyModSync))
                {
                    ModLogger.LogInfo($"Host has ModSync: {lobbyModSync}");
                    return true;
                }
                
                CSteamID lobbyOwner = SteamMatchmaking.GetLobbyOwner(new CSteamID(BootstrapManager.CurrentLobbyID));
                if (lobbyOwner != CSteamID.Nil)
                {
                    // Check if the lobby owner has ModSync in their player data
                    string ownerModSync = SteamMatchmaking.GetLobbyData(new CSteamID(BootstrapManager.CurrentLobbyID), "owner_modsync");
                    if (!string.IsNullOrEmpty(ownerModSync))
                    {
                        ModLogger.LogInfo($"Host has ModSync (from owner data): {ownerModSync}");
                        return true;
                    }
                }
                
                // If we can't detect ModSync, assume they don't have it
                ModLogger.LogWarning("Could not detect if host has ModSync, assuming they don't");
                return false;
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error checking if host has ModSync: {ex.Message}");
                return false;
            }
        }
        
        // Public method to handle when host doesn't have ModSync
        public static void OnHostMissingModSync()
        {
            if (instance == null) return;
            
            ModLogger.LogWarning("Host doesn't have ModSync - skipping mod sync");
            ModSyncUI.ShowMessage("Host doesn't have ModSync - skipping", ModSyncUI.MessageType.Error);
        }
        
        // Public method to test chat system
        public static void TestChatSystem()
        {
            if (instance == null)
            {
                ModLogger.LogWarning("ModSync instance not available for testing");
                return;
            }
            
            if (instance.comms == null || instance.comms.Text == null)
            {
                ModLogger.LogWarning("Chat system not available for testing");
                return;
            }
            
            string testMessage = "[MODSYNC]TEST:Hello World";
            instance.comms.Text.Send("Global", testMessage);
            
            // If debug is enabled, also send a visible message
            if (instance.debugSyncMessages)
            {
                string debugMessage = "DEBUG: ModSync test message sent";
                instance.comms.Text.Send("Global", debugMessage);
                ModLogger.LogInfo($"Debug message sent: {debugMessage}");
            }
            
            ModLogger.LogInfo("Sent test chat message");
        }
        
        // Public method to force client mod sync (for testing)
        public static void ForceClientModSync()
        {
            if (instance == null)
            {
                ModLogger.LogWarning("ModSync instance not available");
                return;
            }
            
            ModLogger.LogInfo("ForceClientModSync called");
            instance.StartClientModSync();
        }
        
        // Public method to get current role info
        public static string GetRoleInfo()
        {
            if (instance == null)
                return "ModSync not initialized";
                
            return $"Host: {instance.isHost}, Client: {instance.isClient}, LobbyID: {BootstrapManager.CurrentLobbyID}";
        }
        
        // Public method to get current mod sync state
        public static string GetModSyncState()
        {
            if (instance == null)
                return "ModSync not initialized";
                
            return $"Completed: {instance.modSyncCompleted}, Waiting: {instance.waitingForHostResponse}, Timeout: {instance.modSyncTimeoutCoroutine != null}";
        }
        
        // Public method to manually send a test mod list (for testing)
        public static void SendTestModList()
        {
            if (instance == null)
            {
                ModLogger.LogWarning("ModSync instance not available");
                return;
            }
            
            if (instance.comms == null || instance.comms.Text == null)
            {
                ModLogger.LogWarning("Chat system not available");
                return;
            }
            
            // Create a test mod list
            var testMods = new List<ModInfo>
            {
                new ModInfo { ModGuid = "test.mod.1", ModName = "TestMod1", SyncType = ModSyncType.All, HasModSyncVariable = true },
                new ModInfo { ModGuid = "test.mod.2", ModName = "TestMod2", SyncType = ModSyncType.All, HasModSyncVariable = true }
            };
            
            ModLogger.LogInfo("Sending test mod list via chat");
            instance.SendModListToHostViaChat(testMods);
        }
        
        // Public method to test message parsing (for debugging)
        public static void TestMessageParsing(string testMessage)
        {
            if (instance == null)
            {
                ModLogger.LogWarning("ModSync instance not available");
                return;
            }
            
            ModLogger.LogInfo($"Testing message parsing: {testMessage}");
            
            // Test the parsing logic directly - using the same logic as OnChatMessageReceived
            string[] parts = testMessage.Split(':');
            ModLogger.LogInfo($"Split message into {parts.Length} parts: {string.Join("|", parts)}");
            
            if (parts.Length < 2)
            {
                ModLogger.LogWarning($"Invalid ModSync message format: {testMessage}");
                return;
            }
            
            // Handle commands that might contain colons - same logic as OnChatMessageReceived
            string command;
            if (parts.Length >= 2 && parts[0].Contains("REQUEST_MODS"))
            {
                command = "REQUEST_MODS";
            }
            else if (parts.Length >= 2 && parts[0].Contains("CLIENT_MODS"))
            {
                command = "CLIENT_MODS";
            }
            else
            {
                command = parts[1];
            }
            
            ModLogger.LogInfo($"Parsed command: {command} from message: {testMessage}");
            
            // Test the switch statement
            switch (command)
            {
                case "CLIENT_MODS":
                    ModLogger.LogInfo("Would handle CLIENT_MODS");
                    break;
                case "HOST_MODS":
                    ModLogger.LogInfo("Would handle HOST_MODS");
                    break;
                case "MODS_MATCH":
                    ModLogger.LogInfo("Would handle MODS_MATCH");
                    break;
                case "MODS_MISMATCH":
                    ModLogger.LogInfo("Would handle MODS_MISMATCH");
                    break;
                case "REQUEST_MODS":
                    ModLogger.LogInfo("Would handle REQUEST_MODS");
                    break;
                case "TEST":
                    ModLogger.LogInfo("Would handle TEST");
                    break;
                default:
                    ModLogger.LogWarning($"Unknown ModSync command: {command} from message: {testMessage}");
                    break;
            }
        }
        

        
        // Public method to get local mod list
        public static List<ModInfo> GetLocalModList()
        {
            return instance?.localModList ?? new List<ModInfo>();
        }
        
        // Public method to parse mod sync type
        public static ModSyncType ParseModSyncType(string value)
        {
            if (string.IsNullOrEmpty(value))
                return ModSyncType.Client;
                
            var lowerValue = value.ToLower().Trim();
            
            switch (lowerValue)
            {
                case "host":
                    return ModSyncType.Host;
                case "all":
                    return ModSyncType.All;
                case "client":
                default:
                    return ModSyncType.Client;
            }
        }
        
        // Public method to process host mod list (called from chat system)
        public static void ProcessHostModList(List<ModInfo> hostMods)
        {
            if (instance == null) return;
            
            instance.hostModList = hostMods;
            instance.waitingForHostResponse = false;
            
            ModLogger.LogInfo($"Received host mod list with {hostMods.Count} mods");
            instance.CompareModLists();
        }
        
        // Public method to process client mod list (called from chat system)
        public static void ProcessClientModList(List<ModInfo> clientMods, string playerName = "Player")
        {
            if (instance == null) return;
            
            ModLogger.LogInfo($"Received client mod list with {clientMods.Count} mods from {playerName}");
            
            // Store the received mod list for this player
            instance.receivedModLists[playerName] = new List<ModInfo>(clientMods);
            
            // Compare with host's mod list
            var hostAllMods = instance.localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
            var clientAllMods = clientMods.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
            
            // Find mismatches
            var missingOnHost = clientAllMods.Where(client => 
                !hostAllMods.Any(host => host.ModGuid == client.ModGuid)).ToList();
            
            var missingOnClient = hostAllMods.Where(host => 
                !clientAllMods.Any(client => client.ModGuid == host.ModGuid)).ToList();
            
            if (missingOnHost.Count == 0 && missingOnClient.Count == 0)
            {
                ModLogger.LogInfo("Host mod sync successful - all required mods match!");
                
                // Show success message for host
                if (instance.isHost)
                {
                    ModSyncUI.ShowMessage($"{playerName} joined with required mods.", ModSyncUI.MessageType.Success);
                }
            }
            else
            {
                ModLogger.LogWarning("Host mod sync failed - mod mismatch detected");
                
                if (missingOnHost.Count > 0)
                {
                    ModLogger.LogWarning($"Missing on host ({missingOnHost.Count}):");
                    foreach (var mod in missingOnHost)
                    {
                        ModLogger.LogWarning($"  - {mod.ModName} ({mod.ModGuid})");
                    }
                }
                
                if (missingOnClient.Count > 0)
                {
                    ModLogger.LogWarning($"Missing on client ({missingOnClient.Count}):");
                    foreach (var mod in missingOnClient)
                    {
                        ModLogger.LogWarning($"  - {mod.ModName} ({mod.ModGuid})");
                    }
                    
                    // Check if lobby lock is enabled and we should kick the player
                    if (instance.isHost && instance.lobbyLockEnabled && missingOnClient.Count > 0)
                    {
                        string missingMods = string.Join(", ", missingOnClient.Select(m => m.ModName));
                        ModLogger.LogWarning($"LOBBY LOCK: Kicking {playerName} for missing mods: {missingMods}");
                        
                        // Show missing mods and kick notifications
                        ModSyncUI.ShowMessage($"{playerName} joined without required mods: {missingMods}", ModSyncUI.MessageType.Error);
                        ModSyncUI.ShowMessage($"Kicking {playerName} for mismatched mods", ModSyncUI.MessageType.Error);
                        
                        // Kick the player
                        instance.KickPlayer(playerName, missingMods);
                    }
                    else if (instance.isHost)
                    {
                        // Show missing mods message for host (without kicking)
                        string missingMods = string.Join(", ", missingOnClient.Select(m => m.ModName));
                        ModSyncUI.ShowMessage($"{playerName} joined without required mods: {missingMods}", ModSyncUI.MessageType.Error);
                    }
                }
            }
        }

        // Public method to test message parsing with usernames containing colons
        public static void TestMessageParsingWithColons(string testMessage)
        {
            if (instance == null)
            {
                ModLogger.LogWarning("ModSync instance not available for testing");
                return;
            }
            
            ModLogger.LogInfo($"Testing message parsing: {testMessage}");
            
            var parsedMessage = instance.ParseModSyncMessage(testMessage);
            if (parsedMessage != null)
            {
                ModLogger.LogInfo($"Successfully parsed - Command: {parsedMessage.Command}, Player: {parsedMessage.PlayerName}, Data: {parsedMessage.Data}");
            }
            else
            {
                ModLogger.LogWarning("Failed to parse message");
            }
        }

        // Public method to test CLIENT_MODS message parsing specifically
        public static void TestClientModsParsing()
        {
            if (instance == null)
            {
                ModLogger.LogWarning("ModSync instance not available for testing");
                return;
            }
            
            // Test the exact message format that was causing issues
            string testMessage = "[MODSYNC]CLIENT_MODS:mommamia2:com.bisocm.unlimited_mages:Unlimited Mages:All";
            
            ModLogger.LogInfo($"Testing CLIENT_MODS parsing: {testMessage}");
            
            var parsedMessage = instance.ParseModSyncMessage(testMessage);
            if (parsedMessage != null)
            {
                ModLogger.LogInfo($"Successfully parsed CLIENT_MODS - Command: {parsedMessage.Command}, Player: {parsedMessage.PlayerName}, Data: {parsedMessage.Data}");
                
                // Verify the parsing is correct
                if (parsedMessage.Command == "CLIENT_MODS" && 
                    parsedMessage.PlayerName == "mommamia2" && 
                    parsedMessage.Data == "com.bisocm.unlimited_mages:Unlimited Mages:All")
                {
                    ModLogger.LogInfo("CLIENT_MODS parsing test PASSED!");
                }
                else
                {
                    ModLogger.LogWarning("CLIENT_MODS parsing test FAILED - incorrect parsing");
                }
            }
            else
            {
                ModLogger.LogWarning("Failed to parse CLIENT_MODS message");
            }
        }
    }
}
