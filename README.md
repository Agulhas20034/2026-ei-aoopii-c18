Project structure
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

