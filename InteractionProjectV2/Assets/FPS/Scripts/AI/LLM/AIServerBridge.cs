using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Unity.FPS.Game;

namespace Unity.FPS.AI
{
    public class AIServerBridge : MonoBehaviour
    {
        [Header("Server Connection")]
        [Tooltip("WebSocket address of the Python bot server")]
        public string ServerUrl = "ws://localhost:8765";

        [Tooltip("How often (seconds) to send a full game-state update")]
        public float StateUpdateInterval = 0.2f;

        [Tooltip("Radius to scan for nearby enemies/allies around each bot")]
        public float DetectionRadius = 300f;

        public static AIServerBridge Instance { get; private set; }

        private readonly Queue<BotCommandPacket> _commandQueue = new Queue<BotCommandPacket>();
        private readonly object _queueLock = new object();

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private bool _connected;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            _ = ConnectAsync();
        }

        void Update()
        {
            lock (_queueLock)
            {
                while (_commandQueue.Count > 0)
                {
                    var pkt = _commandQueue.Dequeue();

                    if (pkt.isSpawnPacket && pkt.spawnData != null)
                    {
                        BotManager.Instance?.SpawnBots(pkt.spawnData);
                    }
                    else if (pkt.commands != null)
                    {
                        BotManager.Instance?.ApplyCommands(pkt.commands);
                    }
                }
            }
        }

        void OnDestroy()
        {
            _cts?.Cancel();
            _ws?.Abort();
        }

        private async Task ConnectAsync()
        {
            _cts = new CancellationTokenSource();

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    Debug.Log($"Connecting to {ServerUrl}…");
                    await _ws.ConnectAsync(new Uri(ServerUrl), _cts.Token);
                    _connected = true;
                    Debug.Log("Connected to AI server.");

                    var receiveTask = ReceiveLoopAsync();
                    var sendTask    = SendLoopAsync();
                    await Task.WhenAny(receiveTask, sendTask);
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    Debug.LogWarning($"Connection error: {e.Message} — retrying in 3s");
                }

                _connected = false;
                await Task.Delay(3000, _cts.Token);
            }
        }

        private async Task SendLoopAsync()
        {
            while (_connected && !_cts.IsCancellationRequested)
            {
                try
                {
                    string json = BuildGameStateJson();
                    if (json != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await _ws.SendAsync(
                            new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text,
                            true, _cts.Token);
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    Debug.LogWarning($"Send error: {e.Message}");
                    break;
                }

                await Task.Delay((int)(StateUpdateInterval * 1000), _cts.Token);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[16384];
            var sb     = new StringBuilder();

            while (_connected && !_cts.IsCancellationRequested)
            {
                try
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(
                            new ArraySegment<byte>(buffer), _cts.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _connected = false;
                            return;
                        }

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    ParseMessage(sb.ToString());
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    Debug.LogWarning($"Receive error: {e.Message}");
                    break;
                }
            }
        }

        private void ParseMessage(string json)
        {
            try
            {
                var wrapper = JsonUtility.FromJson<ServerMessage>(json);

                if (wrapper.type == "spawn_bots")
                {
                    var spawn = JsonUtility.FromJson<SpawnBotsPacket>(json);
                    lock (_queueLock)
                        _commandQueue.Enqueue(new BotCommandPacket
                        {
                            isSpawnPacket = true,
                            spawnData     = spawn
                        });
                    return;
                }

                if (wrapper.type == "bot_commands")
                {
                    var pkt = JsonUtility.FromJson<BotCommandPacket>(json);
                    pkt.isSpawnPacket = false;
                    lock (_queueLock)
                        _commandQueue.Enqueue(pkt);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Parse error: {e.Message} | json={json.Substring(0, Math.Min(json.Length, 120))}");
            }
        }

        private string BuildGameStateJson()
        {
            if (BotManager.Instance == null) return null;

            var bots = BotManager.Instance.Bots;
            if (bots == null || bots.Length == 0) return null;

            var sb = new StringBuilder();
            sb.Append("{\"type\":\"game_state\",\"bots\":[");
            bool firstBot = true;

            for (int i = 0; i < bots.Length; i++)
            {
                BotController bot = bots[i];
                if (bot == null) continue;

                Vector3 pos = bot.transform.position;
                float   yaw = bot.transform.eulerAngles.y;
                float   hp  = bot.CurrentHealth;
                bool    alive = bot.IsAlive;

                var enemies  = bot.GetNearbyActors(DetectionRadius, foe: true);
                var allies   = bot.GetNearbyActors(DetectionRadius, foe: false);
                enemies.Sort((a, b) =>
                    Vector3.Distance(pos, a.transform.position)
                        .CompareTo(Vector3.Distance(pos, b.transform.position)));

                if (!firstBot)
                    sb.Append(",");
                firstBot = false;

                sb.Append("{");
                sb.Append($"\"bot_id\":{bot.BotId},");
                sb.Append($"\"health\":{hp:F1},");
                sb.Append($"\"is_alive\":{alive.ToString().ToLower()},");
                sb.Append($"\"position\":{{\"x\":{pos.x:F2},\"y\":{pos.y:F2},\"z\":{pos.z:F2}}},");
                sb.Append($"\"rotation\":{{\"y\":{yaw:F1}}},");
                sb.Append($"\"nearby_enemies_count\":{enemies.Count},");
                sb.Append("\"nearby_enemies\":[");
                for (int j = 0; j < enemies.Count; j++)
                {
                    var e = enemies[j];
                    Vector3 epos = e.transform.position;
                    float dist   = Vector3.Distance(pos, epos);
                    float angle  = Vector3.SignedAngle(
                        bot.transform.forward,
                        (epos - pos).normalized,
                        Vector3.up);
                    sb.Append(j > 0 ? "," : "");
                    sb.Append($"{{\"distance\":{dist:F1},\"angle\":{angle:F1},");
                    sb.Append($"\"position\":{{\"x\":{epos.x:F2},\"y\":{epos.y:F2},\"z\":{epos.z:F2}}}}}");
                }
                sb.Append("],");
                if (enemies.Count > 0)
                {
                    var firstEnemy = enemies[0].transform.position;
                    sb.Append($"\"closest_enemy_position\":{{\"x\":{firstEnemy.x:F2},\"y\":{firstEnemy.y:F2},\"z\":{firstEnemy.z:F2}}},");
                    sb.Append($"\"closest_enemy_distance\":{Vector3.Distance(pos, firstEnemy):F1},");
                }
                sb.Append($"\"nearby_allies_count\":{allies.Count}");
                sb.Append("}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        public void NotifyBotDied(int botId)
        {
            if (!_connected) return;
            string json = $"{{\"type\":\"bot_died\",\"bot_id\":{botId}}}";
            _ = SendRawAsync(json);
        }

        public void NotifyBotSpawned(int botId, Vector3 pos)
        {
            if (!_connected) return;
            string json = $"{{\"type\":\"bot_spawned\",\"bot_id\":{botId}," +
                          $"\"position\":{{\"x\":{pos.x:F2},\"y\":{pos.y:F2},\"z\":{pos.z:F2}}}}}";
            _ = SendRawAsync(json);
        }

        private async Task SendRawAsync(string json)
        {
            if (_ws?.State != WebSocketState.Open) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true, _cts.Token);
            }
            catch {  }
        }
    }

    [Serializable] public class ServerMessage    { public string type; }
    [Serializable] public class SpawnBotsPacket  { public string type; public SpawnBotEntry[] bots; }
    [Serializable] public class SpawnBotEntry    { public int bot_id; public Vec3 position; }
    [Serializable] public class Vec3             { public float x, y, z; }

    [Serializable]
    public class BotCommandPacket
    {
        public string           type;
        public BotCommandData[] commands;
        [NonSerialized] public bool          isSpawnPacket;
        [NonSerialized] public SpawnBotsPacket spawnData;
    }

    [Serializable]
    public class BotCommandData
    {
        public int    bot_id;
        public float  move_x;
        public float  move_z;
        public float  look_y;
        public float  look_x;
        public bool   fire;
        public bool   jump;
        public bool   sprint;
        public string action_label;
    }
}
