# AI Integration - Quick Start

## What's Fixed

✅ **Removed external dependencies:**
- No Newtonsoft.Json required
- No WebSocketSharp required  
- No external JSON libraries

✅ **Uses standard Unity APIs:**
- `UnityWebRequest` for HTTP communication
- `JsonUtility` for serialization
- No additional imports needed

## Installation

### 1. Install Python Dependencies
```bash
cd Backend
pip install -r requirements.txt
```

### 2. Ensure Ollama is Running
```bash
ollama serve
ollama pull llama3.2:latest
```

### 3. Start Python Backend
```bash
python server.py
```

Expected output:
```
21:45:30 [INFO] Starting AI Bot Server on http://0.0.0.0:8765
21:45:30 [INFO] Ollama model: llama3.2 @ http://localhost:11434/api/generate
21:45:30 [INFO] Managing 4 bots | Decision interval: 1.5s
21:45:30 [INFO] Starting Flask server on http://0.0.0.0:8765
 * Running on http://0.0.0.0:8765
```

### 4. In Unity

1. Create empty GameObject: **AICommandClient**
2. Add `AICommandClient.cs` component
3. Set Server URL: `http://localhost:8765`

For each NPC:
1. Add `NPCController.cs` component
2. Set Bot ID: 0, 1, 2, or 3
3. Assign required references:
   - Character Controller
   - Health component
   - Head Transform (for looking)
   - Actor (optional)

## Communication Flow

```
1. Unity sends bot state (position, health, enemies) via HTTP POST
2. Python backend receives state
3. Python queries Ollama with current state
4. Ollama LLM returns JSON decision
5. Python sends back bot commands via HTTP response
6. Unity applies movement/rotation/firing
```

## Important Notes

⚠️ **Fire/Jump Integration:**
The `ExecuteCommand()` method has fire and jump commented out because your game's specific implementation details are needed. Uncomment and wire up to your WeaponController and jump mechanics.

⚠️ **Enemy Detection:**
NPCController uses `Physics.OverlapSphere()` to find enemies with `EnemyController` component. Ensure your enemy NPCs have this component.

⚠️ **HTTP Polling:**
Unity polls the server every 0.25 seconds (default). Adjust `commandUpdateInterval` in `AICommandClient.cs` for different rates.

## Customization

### Change Decision Frequency
In `server.py`:
```python
AI_DECISION_INTERVAL = 1.5  # seconds between AI thoughts
```

### Change Poll Rate
In `NPCController.cs`:
```csharp
[SerializeField] private float commandUpdateInterval = 0.25f;  // seconds
```

### Change Model
In `server.py`:
```python
OLLAMA_MODEL = "llama3.2"  # Change to "phi:latest" or "llama3.2:latest" etc
```

### Adjust NPC Speed
In `NPCController.cs`:
```csharp
[SerializeField] private float maxMoveSpeed = 10f;
[SerializeField] private float maxAngularSpeed = 180f;  // degrees/second
```

### Detection Range
In `NPCController.cs`:
```csharp
[SerializeField] private float detectionRange = 30f;
[SerializeField] private float attackRange = 15f;
```

## Troubleshooting

### "Failed to connect"
- Ensure Python backend is running
- Check firewall allows port 8765
- Verify URL is `http://localhost:8765` (not `ws://`)

### "Ollama request failed"
- Ensure Ollama is running: `ollama serve`
- Check model exists: `ollama list`
- Verify llama3.2:latest is installed

### "No enemies detected"
- Ensure enemy NPCs have `EnemyController` component
- Verify enemy is within `detectionRange` (default: 30 units)
- Check enemy has `Health` component with CurrentHealth > 0

### Slow AI Responses
- Increase `AI_DECISION_INTERVAL` in server.py
- Increase `commandUpdateInterval` in Unity
- Use faster model: `ollama pull phi:latest` then set `OLLAMA_MODEL = "phi"`

## Files Modified/Created

**New Files:**
- `Assets/FPS/Scripts/AI/AICommandClient.cs` - Main communication client
- `Assets/FPS/Scripts/AI/NPCController.cs` - NPC behavior execution

**Modified Files:**
- `Backend/server.py` - Now uses Flask (HTTP) instead of WebSocket
- `Backend/requirements.txt` - Updated dependencies
- `INTEGRATION_GUIDE.md` - Full setup documentation

## Next: Wire Up Combat

Currently, the AI can command movement and rotation. To add firing:

```csharp
// In NPCController.ExecuteCommand()
if (command.fire && /* your weapon logic here */)
{
    // Fire weapon
}
```

Integrate with your existing weapon system to make NPCs actually shoot at enemies.

