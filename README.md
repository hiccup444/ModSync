# ModSync - Chat-Based Mod Synchronization

A plugin for Mage Arena that synchronizes mods between host and clients.

## Features

- **Automatic Detection**: Detects when players join and automatically checks their mod compatibility
- **Lobby Lock**: Host can enable lobby lock (F9) to kick players with mismatched mods (clients get 10 seconds to respond to mod requests)
- **Visual Feedback**: Shows success/error messages in the game UI

## How It Works

### For Hosts:
1. When a player joins, ModSync sends a mod request to the player.
2. If the player has ModSync, they respond with their mod list
3. Host compares mod lists automatically and either accepts or kicks the player.
- Mods with the "all" tag will be considered when matching mod lists. This means that client side mods should not affect the compatibility between host and player.

### For Clients:
1. When joining a lobby, clients automatically send their mod list to the host
2. Clients respond to mod requests from the host
3. If mods don't match, clients receive a mismatch message
- Most client side mods should be fine to run without getting a mismatch error.

## Installation

1. Install BepInEx for Mage Arena
2. Place `ModSync.dll` in the `BepInEx/plugins` folder

## Usage

### For Hosts:
- Press **F9** to toggle lobby lock on/off
- When lobby lock is enabled, players with mismatched mods will be automatically kicked
- Check the game logs for detailed ModSync information

### For Clients:
- No manual intervention required
- ModSync automatically handles synchronization when joining lobbies
- Check the game logs for detailed ModSync information

## Configuration

Mod developers can specify their mod's sync type by adding a static field or property named `modsync`:

```csharp
public static string modsync = "all"; // Requires matching on both host and client - This is what is considered when syncing lobbies
public static string modsync = "host"; // Only required on host - These do not count for syncing lobbies
public static string modsync = "client"; // Only required on client - These do not count for syncing lobbies
```

## Troubleshooting

### Common Issues:

1. **Timeout Errors**
   - Players without ModSync will timeout after 8-10 seconds.
   - This is expected behavior for players without the mod.

2. **Mod Mismatches**
   - Check that all required mods are installed. Mods with the "all" tag are considered for syncing.
   - Verify mod versions match between host and client
   - Turn off Lock Lobby if experiencing many mod mismatch issues.

### Debugging:

- Enable BepInEx logging to see detailed ModSync messages
- Check the game logs for `[ModSync]` entries

## Technical Details

### Dependencies:
- BepInEx

### Performance:
- Minimal overhead since it uses existing chat infrastructure
- Messages are only sent when needed (player joins)
- No continuous polling or background processes
