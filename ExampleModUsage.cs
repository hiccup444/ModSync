using BepInEx;
using UnityEngine;

namespace ExampleMod
{
    [BepInPlugin("com.example.mymod", "My Example Mod", "1.0.0")]
    [BepInProcess("MageArena.exe")]
    [BepInDependency("com.magearena.modsync", BepInDependency.DependencyFlags.HardDependency)]
    public class ExampleMod : BaseUnityPlugin
    {
        // This mod requires both client and host to have it
        public static string modsync = "all";
        
        private void Awake()
        {
            // ModSync dependency is guaranteed by BepInEx hard dependency
            // If ModSync is not present, this mod won't even load
            Logger.LogInfo("ModSync found! Initializing mod...");
            // Your mod initialization code here
        }
    }
}

namespace AnotherExampleMod
{
    [BepInPlugin("com.example.clientonly", "Client Only Mod", "1.0.0")]
    [BepInProcess("MageArena.exe")]
    [BepInDependency("com.magearena.modsync", BepInDependency.DependencyFlags.HardDependency)]
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
    [BepInDependency("com.magearena.modsync", BepInDependency.DependencyFlags.HardDependency)]
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