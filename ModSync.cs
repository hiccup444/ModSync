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
    [BepInPlugin("com.magearena.modsync", "ModSync", "1.0.0")]
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
        private bool lobbyDetectionInitialized = false;
        private bool lobbyLockEnabled = false; // F9 toggle for lobby lock
        private float lastF9Press = 0f; // Prevent rapid toggling
        private const float F9_COOLDOWN = 0.5f; // Half second cooldown
        private List<string> connectedPlayers = new List<string>(); // Track connected players
        private bool playerJoinDetectionInitialized = false;
        private Dictionary<string, List<ModInfo>> receivedModLists = new Dictionary<string, List<ModInfo>>(); // Track received mod lists by player
        private HashSet<string> processedPlayers = new HashSet<string>(); // Track players who have been processed to prevent duplicate messages
        private bool gameStarted = false; // Track if the game has started
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
            
            // Get only "all" type plugins for sync
            var allTypePlugins = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
            
            ModLogger.LogInfo($"Found {allTypePlugins.Count} plugins requiring host matching");
            
            if (allTypePlugins.Count == 0)
            {
                ModLogger.LogInfo("No plugins require host matching - sync complete");
                modSyncCompleted = true;
                return;
            }
            
            // Start timeout timer
            modSyncStartTime = Time.time;
            waitingForHostResponse = true;
            
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
                    modListData.Add($"{mod.ModGuid}|{mod.ModName}|{mod.SyncType}");
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
            
            while (waitingForHostResponse && Time.time - modSyncStartTime < modSyncTimeout)
            {
                yield return null;
            }
            
            if (waitingForHostResponse)
            {
                ModLogger.LogError("Mod sync timeout - no response from host");
                waitingForHostResponse = false;
                modSyncCompleted = true;
                
                HandleModSyncFailure("Timeout waiting for host response");
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
                    if (debugSyncMessages)
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
                        }
                    }
                    else
                    {
                        // Game has not started or has ended - check if we need to reinitialize
                        if (gameStarted)
                        {
                            gameStarted = false;
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
                    StartCoroutine(CheckPlayerModsWithTimeout(player));
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogError($"Error checking existing players: {ex.Message}");
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
                        ModLogger.LogInfo($"Found player: {kvp.Key} (SteamID: {kvp.Value})");
                    }
                }
                
                // Only log if we're in a lobby and have players
                if (players.Count > 0)
                {
                    ModLogger.LogInfo($"Found {players.Count} connected players: {string.Join(", ", players.Keys)}");
                }
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
            ModLogger.LogInfo($"Player left: {playerName}");
            connectedPlayers.Remove(playerName);
            
            // Clear tracking for this player
            receivedModLists.Remove(playerName);
            processedPlayers.Remove(playerName);
            playerResponseTimeouts.Remove(playerName);
            
            ModLogger.LogInfo($"Cleared tracking for {playerName}");
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
                // Only process ModSync messages in lobby (not during gameplay)
                if (!message.Message.StartsWith("[MODSYNC]"))
                {
                    return;
                }
                
                // Don't process ModSync messages during gameplay
                if (gameStarted)
                {
                    ModLogger.LogInfo($"Ignoring ModSync message during gameplay: {message.Message}");
                    return;
                }
                
                ModLogger.LogInfo($"Received ModSync chat message: {message.Message}");
                
                // Parse the message - handle the case where command contains colons
                string[] parts = message.Message.Split(':');
                ModLogger.LogInfo($"Split message into {parts.Length} parts: {string.Join("|", parts)}");
                
                if (parts.Length < 2)
                {
                    ModLogger.LogWarning($"Invalid ModSync message format: {message.Message}");
                    return;
                }
                
                // Handle commands that might contain colons
                string command;
                if (parts.Length >= 2 && parts[0].Contains("REQUEST_MODS"))
                {
                    command = "REQUEST_MODS";
                }
                else if (parts.Length >= 2 && parts[0].Contains("CLIENT_MODS"))
                {
                    command = "CLIENT_MODS";
                }
                else if (parts.Length >= 2 && parts[0].Contains("MODS_MATCH"))
                {
                    command = "MODS_MATCH";
                }
                else if (parts.Length >= 2 && parts[0].Contains("MODS_MISMATCH"))
                {
                    command = "MODS_MISMATCH";
                }
                else
                {
                    command = parts[1];
                }
                
                ModLogger.LogInfo($"Parsed command: {command} from message: {message.Message}");
                
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
                    clientMods.Add(modInfo);
                }
            }
            
            // Store the received mod list
            receivedModLists[playerName] = clientMods;
            
            // Check if we've already processed this player to prevent duplicate messages
            if (processedPlayers.Contains(playerName))
            {
                ModLogger.LogInfo($"Already processed {playerName} - skipping duplicate message");
                return;
            }
            
            // Compare with host's mod list
            var hostAllMods = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
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
                    string debugMessage = $"DEBUG: Mods match with {playerName} ";
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
                
                // Stop the timeout coroutine if it's running
                if (modSyncTimeoutCoroutine != null)
                {
                    StopCoroutine(modSyncTimeoutCoroutine);
                    modSyncTimeoutCoroutine = null;
                    ModLogger.LogInfo("Stopped mod sync timeout coroutine due to mismatch");
                }
                
                ModSyncUI.ShowMessage($"Mod sync failed: You are missing mods: {missingMods}", ModSyncUI.MessageType.Error);
                ModSyncUI.ShowMessage("Consider installing missing mods or disconnecting", ModSyncUI.MessageType.Warning);
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
                ModLogger.LogInfo($"Ignoring REQUEST_MODS message during gameplay from {(parts.Length > 2 ? parts[2] : "unknown")}");
                return;
            }
            
            if (parts.Length < 2)
            {
                ModLogger.LogWarning($"Invalid REQUEST_MODS message format: {message.Message}");
                return;
            }
            
            string requesterName = parts[1];
            
            ModLogger.LogInfo($"Received mod request from {requesterName}");
            
            // Send our mod list to the requester
            var allTypePlugins = localModList.Where(p => p.HasModSyncVariable && p.SyncType == ModSyncType.All).ToList();
            var modListData = new List<string>();
            
            foreach (var mod in allTypePlugins)
            {
                modListData.Add($"{mod.ModGuid}|{mod.ModName}|{mod.SyncType}");
            }
            
            string modListString = string.Join(";", modListData);
            string responseMessage = $"[MODSYNC]CLIENT_MODS:{GetPlayerName()}:{modListString}";
            comms.Text.Send("Global", responseMessage);
            
            // If debug is enabled, also send a visible message
            if (debugSyncMessages)
            {
                string debugMessage = $"DEBUG: Sent host mod list to {requesterName} ({modListData.Count} mods)";
                comms.Text.Send("Global", debugMessage);
                ModLogger.LogInfo($"Debug message sent: {debugMessage}");
            }
            
            ModLogger.LogInfo($"Sent host mod list to {requesterName}");
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
                            ModLogger.LogInfo($"New player detected: {playerName}");
                            OnPlayerJoined(playerName);
                        }
                    }
                    
                    // Check for players who left
                    foreach (var playerName in previousPlayers)
                    {
                        if (!currentPlayerSet.Contains(playerName) && playerName != hostPlayerName)
                        {
                            ModLogger.LogInfo($"Player left: {playerName}");
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
    }
}
