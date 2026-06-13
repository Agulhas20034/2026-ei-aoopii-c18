# AI-Driven NPC Integration Guide

## Architecture Overview

```
Unity Game (NPCController)
        ↓ (sends bot state via HTTP POST)
Python Backend Flask Server (server.py)
        ↓ (queries Ollama LLM asynchronously)
Ollama + llama3.2:latest
        ↓ (returns decisions)
Python Backend
        ↓ (sends commands back via HTTP response)
Unity Game (executes movement/combat)
```

Communication is HTTP-based (not WebSocket) for simplicity and to avoid external dependencies.

## Setup Steps

### 1. Python Backend Setup

#### Prerequisites
- Python 3.9+
- Ollama running locally (port 11434)
- Required Python packages:

```bash
cd Backend/
pip install -r requirements.txt
```

Or manually install:
```bash
pip install httpx flask
```

#### Start the Python Backend
```bash
cd Backend/
python server.py
```

Expected output:
```
09:45:23 [INFO] Bot server starting on ws://0.0.0.0:8765
09:45:23 [INFO] Bot manager initialized with 4 bots
```

### 2. Unity Setup

#### Install Dependencies
1. Open Unity Package Manager (Window → TextMesh Pro → Import TMP Essential Resources)
2. Install WebSocketSharp via NuGet:
   - Download `WebSocketSharp.dll` from NuGet
   - Or use: `Install-Package WebSocketSharp -Version 1.0.3.11`
   - Place in `Assets/Plugins/`

3. Install Newtonsoft.Json:
   - Download from NuGet or Asset Store
   - Place in `Assets/Plugins/`

#### Add Scripts to Scene
1. Create an empty GameObject named "AICommandClient"
2. Add `AICommandClient.cs` component
3. Set the server URL to: `http://localhost:8765` (or your server IP)

For each NPC in your scene:
1. Add `NPCController.cs` component
2. Set the **Bot ID** (0, 1, 2, 3 for the 4 default bots)
3. Assign references:
   - Character Controller (for movement)
   - Health component
   - Head Transform (for looking direction)
   - Weapon Controller (for firing)

### 3. Configuration

#### Python Backend (server.py)
- `BOT_COUNT`: Number of NPCs (default: 4)
- `AI_DECISION_INTERVAL`: How often to query Ollama (default: 1.5s)
- `OLLAMA_MODEL`: Change to `"llama3.2:latest"` if using that model

#### Unity NPCController
- **Max Move Speed**: Units per second (default: 10)
- **Max Angular Speed**: Degrees per second (default: 180)
- **Detection Range**: How far to see enemies (default: 30)
- **Attack Range**: Fire range (default: 15)

### 4. Testing

#### Step 1: Start Ollama
```bash
ollama serve
ollama pull llama3.2:latest  # If not already pulled
```

#### Step 2: Start Python Backend
```bash
python Backend/server.py
```

#### Step 3: Run Unity Game
- Play the scene with NPCController components
- Watch the Console for connection logs
- You should see: `[AICommandClient] Connected to AI backend!`

#### Step 4: Verify AI Responses
Check the Python backend console for lines like:
```
[Bot 0] → pursue | move=(0.8,0.5) look_y=15.0° fire=False
[Bot 1] → aggressive_attack | move=(0.2,0.0) look_y=0.0° fire=True
```

## Troubleshooting

### Connection Issues
- **"Failed to connect"**: Ensure Python server is running and firewall allows port 8765
- **WebSocket error**: Check `serverUrl` matches your backend address

### AI Not Responding
- Check if Ollama is running: `ollama list`
- Check Python backend logs for Ollama request errors
- Ensure `llama3.2:latest` model is installed

### NPCs Not Moving
1. Verify `CharacterController` is assigned
2. Check that NPC GameObjects have colliders
3. Ensure Bot IDs match (0-3)

## Why HTTP Polling Instead of WebSocket?

- ✅ No external dependencies (uses Unity's built-in `UnityWebRequest`)
- ✅ Simpler to debug (standard HTTP requests)
- ✅ Works with standard networking firewalls
- ✅ Minimal latency for game AI updates
- ❌ Slightly more overhead than WebSocket (but negligible for this use case)

The polling interval is configurable: `commandUpdateInterval` in `AICommandClient.cs` (default: 0.25s = 4 updates per second)

## Optional: Bypass Python Backend

If you want to call Ollama directly from Unity (skip Python):

1. Create a C# Ollama client:
```csharp
using UnityEngine;
using UnityEngine.Networking;

public class OllamaClient : MonoBehaviour
{
    private const string OLLAMA_API = "http://localhost:11434/api/generate";

    public void QueryOllama(string prompt, System.Action<string> callback)
    {
        StartCoroutine(QueryCoroutine(prompt, callback));
    }

    private IEnumerator QueryCoroutine(string prompt, System.Action<string> callback)
    {
        var request = new UnityWebRequest(OLLAMA_API, "POST");
        var jsonData = JsonUtility.ToJson(new OllamaRequest 
        { 
            model = "llama3.2:latest",
            prompt = prompt,
            stream = false
        });
        
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            callback?.Invoke(request.downloadHandler.text);
        }
    }
}

[System.Serializable]
public class OllamaRequest
{
    public string model;
    public string prompt;
    public bool stream;
}
```

However, the Python backend approach is **recommended** because:
- ✅ Handles multiple NPCs efficiently
- ✅ Has fallback logic if Ollama is slow
- ✅ Easier to tweak system prompts
- ✅ Separates AI logic from game logic

## Performance Tuning

### Reduce Ollama Load
```python
# In server.py, increase AI_DECISION_INTERVAL
AI_DECISION_INTERVAL = 3.0  # Query every 3 seconds instead of 1.5
```

### Reduce Update Frequency in Unity
```csharp
// In AICommandClient.cs, increase polling interval
[SerializeField] private float commandUpdateInterval = 0.5f; // Update every 0.5 seconds instead of 0.25
```

## FAQ

**Q: Can I remove the Python backend?**  
A: Yes, but you'd need to implement Ollama communication and decision batching in C#. The Python approach is simpler and more maintainable.

**Q: What if Ollama is on a different machine?**  
A: Change `OLLAMA_URL` in `server.py` to your server's IP, and update `serverUrl` in `AICommandClient.cs`.

**Q: Can I use a different model?**  
A: Yes! Change `OLLAMA_MODEL = "..." ` in `server.py`. Just ensure it's installed with `ollama pull <model>`.

**Q: Why WebSocket instead of HTTP?**  
A: WebSocket keeps a persistent connection, reducing latency for frequent updates. Better for real-time game AI.

## Next Steps

1. ✅ Start Python backend
2. ✅ Add scripts to Unity
3. ✅ Configure NPC references
4. ✅ Play and watch NPCs respond to threats
5. ✅ Tune AI prompts and parameters

Good luck! 🎮
