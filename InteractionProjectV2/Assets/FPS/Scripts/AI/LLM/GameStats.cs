using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Unity.FPS.AI;                      
using FpsHealth = Unity.FPS.Game.Health;


public class GameStats : MonoBehaviour
{
    public static GameStats Instance { get; private set; }

    [Header("Backend")]
    public string baseUrl = "http://localhost:8765";
    public float shotFlushInterval = 1f;
    public bool debugLog = true;

    public string SessionId { get; private set; } = "";

    private readonly HashSet<FpsHealth> _tracked = new HashSet<FpsHealth>();
    private readonly Dictionary<int, int> _lastKiller = new Dictionary<int, int>();
    private readonly Dictionary<int, int> _pendingShots = new Dictionary<int, int>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void Start()
    {
        StartCoroutine(StartSession());
        StartCoroutine(TrackLoop());
        StartCoroutine(FlushLoop());
    }

    private IEnumerator StartSession()
    {
        yield return PostJson("/session/start", "{}", resp =>
        {
            var r = JsonUtility.FromJson<SessionResp>(resp);
            SessionId = r != null ? r.session_id : "";
            if (debugLog) Debug.Log($"[GameStats] session = {SessionId}");
        });
    }

    private IEnumerator TrackLoop()
    {
        var wait = new WaitForSeconds(1f);
        while (true)
        {
            foreach (var ec in FindObjectsByType<EnemyController>(FindObjectsSortMode.None))
            {
                if (ec.GetComponent<BotController>() != null) continue;   
                var h = ec.GetComponent<FpsHealth>();
                if (h != null) Track(h);
            }
            yield return wait;
        }
    }

    private void Track(FpsHealth h)
    {
        if (!_tracked.Add(h)) return;
        int id = h.GetInstanceID();
        string victim = h.name;

        h.OnDamaged += (amount, source) =>
        {
            if (source == null) return;
            var bc = source.GetComponentInParent<BotController>();
            if (bc != null) _lastKiller[id] = bc.botId;   
        };

        h.OnDie += () =>
        {
            int killer = _lastKiller.TryGetValue(id, out var k) ? k : -1;
            string json = $"{{\"type\":\"kill\",\"session_id\":\"{SessionId}\",\"killer_bot_id\":{killer},\"victim\":\"{Sanitize(victim)}\"}}";
            StartCoroutine(PostJson("/event", json, null));
            if (debugLog) Debug.Log($"[GameStats] kill: Bot {killer} -> {victim}");
        };
    }

    public void ReportShot(int botId)
    {
        _pendingShots.TryGetValue(botId, out int c);
        _pendingShots[botId] = c + 1;
    }

    private IEnumerator FlushLoop()
    {
        var wait = new WaitForSeconds(shotFlushInterval);
        while (true)
        {
            yield return wait;
            if (_pendingShots.Count == 0) continue;

            var snapshot = new List<KeyValuePair<int, int>>(_pendingShots);
            _pendingShots.Clear();
            foreach (var kv in snapshot)
            {
                string json = $"{{\"type\":\"shots\",\"session_id\":\"{SessionId}\",\"bot_id\":{kv.Key},\"count\":{kv.Value}}}";
                yield return PostJson("/event", json, null);
            }
        }
    }

    private IEnumerator PostJson(string path, string json, System.Action<string> onOk)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        using (var uwr = new UnityWebRequest($"{baseUrl}{path}", "POST"))
        {
            uwr.uploadHandler = new UploadHandlerRaw(body);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json");
            uwr.timeout = 5;
            yield return uwr.SendWebRequest();
            if (uwr.result == UnityWebRequest.Result.Success) onOk?.Invoke(uwr.downloadHandler.text);
            else if (debugLog) Debug.LogWarning($"[GameStats] {path} failed: {uwr.error}");
        }
    }

    private static string Sanitize(string s) =>
        string.IsNullOrEmpty(s) ? "enemy" : s.Replace("\\", "").Replace("\"", "'");

    [System.Serializable] public class SessionResp { public string session_id; }
}
