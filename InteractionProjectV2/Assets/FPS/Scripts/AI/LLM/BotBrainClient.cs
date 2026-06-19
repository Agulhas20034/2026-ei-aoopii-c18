using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;


public class BotBrainClient : MonoBehaviour
{
    [Header("Backend")]
    public string serverUrl = "http://localhost:8765/command";
    public float pollInterval = 0.25f;
    public float requestTimeout = 50f;

    [Header("Debug")]
    public bool debugLog = true;
    public bool verbose = false;       
    public bool showHud = true;
    public float logInterval = 1f;

    private readonly List<BotController> _bots = new List<BotController>();
    private float _nextLog;
    private bool _everConnected;
    private string _lastError = "";
    private int _lastCmdCount = -1;

    void Start()
    {
        _bots.AddRange(FindObjectsByType<BotController>(FindObjectsSortMode.None));
        _bots.Sort((a, b) => a.botId.CompareTo(b.botId));
        if (debugLog) Debug.Log($"[BrainClient] found {_bots.Count} bot(s). POSTing to {serverUrl}");
        StartCoroutine(PollLoop());
    }

    private IEnumerator PollLoop()
    {
        var wait = new WaitForSeconds(pollInterval);
        while (true)
        {
            yield return StartCoroutine(SendStateAndApply());
            yield return wait;
        }
    }

    private IEnumerator SendStateAndApply()
    {
        string json = BuildStateJson();
        if (verbose) Debug.Log($"[BrainClient] OUT: {json}");
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest req = new UnityWebRequest(serverUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = Mathf.CeilToInt(requestTimeout);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                _lastError = req.error;
                if (debugLog && Time.time >= _nextLog)
                {
                    _nextLog = Time.time + logInterval;
                    Debug.LogWarning($"[BrainClient] backend unreachable: {req.error} " +
                                     $"(is bot_server.py running on {serverUrl}?)");
                }
                yield break;
            }

            _lastError = "";
            if (!_everConnected) { _everConnected = true; if (debugLog) Debug.Log("[BrainClient] connected to backend OK"); }
            if (verbose) Debug.Log($"[BrainClient] IN: {req.downloadHandler.text}");
            ApplyResponse(req.downloadHandler.text);
        }
    }

    private string BuildStateJson()
    {
        var state = new GameStateJson { type = "game_state", bot_count = _bots.Count, bots = new List<BotStateJson>() };

        foreach (var bot in _bots)
        {
            var enemies = new List<EnemyJson>();
            foreach (var e in bot.Sensor.Current)
            {
                if (!e.IsValid) continue;
                enemies.Add(new EnemyJson
                {
                    id = e.id,
                    distance = (float)Math.Round(e.distance, 2),
                    angle = (float)Math.Round(e.angle, 1),
                    has_los = e.hasLOS,
                    position = Vec(e.Position)
                });
            }

            state.bots.Add(new BotStateJson
            {
                bot_id = bot.botId,
                position = Vec(bot.transform.position),
                rotation_y = bot.transform.eulerAngles.y,
                health = bot.HealthValue,
                is_alive = bot.IsAlive,
                nearby_enemies_wrapper = new EnemyListJson { items = enemies },
                nearby_allies = new List<int>()
            });
        }
        return JsonUtility.ToJson(state);
    }

    private void ApplyResponse(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        CommandResponseJson resp;
        try { resp = JsonUtility.FromJson<CommandResponseJson>(text); }
        catch (Exception ex) { Debug.LogWarning($"[BrainClient] bad response: {ex.Message}"); return; }

        if (resp?.commands_wrapper?.items == null) { _lastCmdCount = 0; return; }
        _lastCmdCount = resp.commands_wrapper.items.Count;

        foreach (var cmd in resp.commands_wrapper.items)
        {
            BotController bot = _bots.Find(b => b.botId == cmd.bot_id);
            if (bot != null) bot.ApplyCommand(cmd.action, cmd.target_id, cmd.fire, cmd.sprint);
        }
    }

    void OnGUI()
    {
        if (!showHud) return;

        GUI.color = string.IsNullOrEmpty(_lastError) ? Color.green : Color.red;
        GUI.Label(new Rect(10, 10, 600, 20),
            string.IsNullOrEmpty(_lastError)
                ? $"Backend: OK   commands last cycle: {_lastCmdCount}"
                : $"Backend ERROR: {_lastError}");

        GUI.color = Color.white;
        float y = 34;
        foreach (var bot in _bots)
        {
            int seen = bot.Sensor != null ? bot.Sensor.Current.Count : 0;
            GUI.color = bot.FiringNow ? Color.yellow : Color.white;
            GUI.Label(new Rect(10, y, 700, 20),
                $"Bot {bot.botId} | hp {bot.HealthValue:F0} | seen {seen} | tgt {bot.CurrentTargetId} | {bot.StatusLine}");
            y += 20;
        }
    }

    private static Vec3Json Vec(Vector3 v) =>
        new Vec3Json { x = (float)Math.Round(v.x, 2), y = (float)Math.Round(v.y, 2), z = (float)Math.Round(v.z, 2) };

    [Serializable] public class Vec3Json { public float x, y, z; }
    [Serializable] public class EnemyJson { public int id; public float distance; public float angle; public bool has_los; public Vec3Json position; }
    [Serializable] public class EnemyListJson { public List<EnemyJson> items; }
    [Serializable] public class BotStateJson { public int bot_id; public Vec3Json position; public float rotation_y; public float health; public bool is_alive; public EnemyListJson nearby_enemies_wrapper; public List<int> nearby_allies; }
    [Serializable] public class GameStateJson { public string type; public int bot_count; public List<BotStateJson> bots; }
    [Serializable] public class CommandJson { public int bot_id; public string action; public int target_id = -1; public bool fire; public bool sprint; public string action_label; }
    [Serializable] public class CommandListJson { public List<CommandJson> items; }
    [Serializable] public class CommandResponseJson { public CommandListJson commands_wrapper; }
}
