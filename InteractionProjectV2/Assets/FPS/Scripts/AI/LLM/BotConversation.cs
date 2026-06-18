using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Unity.FPS.AI;                       
using FpsHealth = Unity.FPS.Game.Health;


public class BotConversation : MonoBehaviour
{
    [Header("Backend")]
    public string chatUrl = "http://localhost:8765/chat";

    [Header("Regroup")]
    [Tooltip("Where the bots gather. If empty, uses the average of the bots' positions at scene start (their spawn).")]
    public Transform regroupPoint;
    public float arriveTimeout = 12f;

    [Header("Conversation")]
    public int turns = 8;
    public float lineDuration = 3.5f;
    public string topic = "the fight you just won";

    [Header("Display")]
    public int bubbleFontSize = 24;
    public int logFontSize = 20;
    public float bubbleWidth = 460f;

    [Header("Debug")]
    public bool debugLog = true;

    private readonly List<BotController> _bots = new List<BotController>();
    private int _peakEnemies;
    private bool _started;
    private Vector3 _spawnCentroid;

    private readonly List<string> _transcriptDisplay = new List<string>();
    private BotController _currentSpeaker;
    private string _currentName = "";
    private string _currentLine = "";

    void Start()
    {
        _bots.AddRange(FindObjectsByType<BotController>(FindObjectsSortMode.None));
        _bots.Sort((a, b) => a.botId.CompareTo(b.botId));

        Vector3 sum = Vector3.zero;
        foreach (var b in _bots) sum += b.transform.position;
        _spawnCentroid = _bots.Count > 0 ? sum / _bots.Count : transform.position;

        StartCoroutine(WatchForFirstDeath());
    }

    private IEnumerator WatchForFirstDeath()
    {
        var wait = new WaitForSeconds(0.5f);
        while (!_started)
        {
            int alive = CountAliveEnemies();
            if (alive > _peakEnemies) _peakEnemies = alive;       
            if (_peakEnemies > 0 && alive < _peakEnemies)        
            {
                _started = true;
                StartCoroutine(RegroupAndTalk());
                yield break;
            }
            yield return wait;
        }
    }

    private int CountAliveEnemies()
    {
        int n = 0;
        foreach (var ec in FindObjectsByType<EnemyController>(FindObjectsSortMode.None))
        {
            if (ec.GetComponent<BotController>() != null) continue;   
            var h = ec.GetComponent<FpsHealth>();
            if (h != null && h.CurrentHealth > 0f) n++;
        }
        return n;
    }

    private IEnumerator RegroupAndTalk()
    {
        Vector3 point = regroupPoint != null ? regroupPoint.position : _spawnCentroid;
        var alive = LivingBots();
        if (alive.Count == 0) yield break;

        foreach (var b in alive) b.BeginRegroup(point);
        if (debugLog) Debug.Log("[Conversation] first enemy down -> regrouping at spawn");

        float t = 0f;
        while (t < arriveTimeout && LivingBots().Exists(b => !b.AtRegroup)) { t += Time.deltaTime; yield return null; }

        var transcript = new List<Line>();
        int fails = 0;
        try
        {
            for (int i = 0; i < turns; i++)
            {
                var live = LivingBots();
                if (live.Count == 0) break;                                 

                var speaker = live[i % live.Count];
                Vector3 center = GroupCenter(live);
                foreach (var b in live) b.FacePoint(b == speaker ? center : speaker.transform.position);
                _currentSpeaker = speaker;
                _currentName = DisplayName(speaker);
                _currentLine = "...";                                       

                string line = null;
                yield return StartCoroutine(RequestLine(speaker, transcript, s => line = s));

                if (string.IsNullOrEmpty(line))
                {
                    if (++fails >= 2) { if (debugLog) Debug.LogWarning("[Conversation] backend not responding -> ending early"); break; }
                    line = "...";
                }
                else fails = 0;

                if (speaker == null || !speaker.IsAlive) continue;           

                transcript.Add(new Line { bot_id = speaker.botId, text = line });
                _currentLine = line;
                _transcriptDisplay.Add($"{DisplayName(speaker)}: {line}");
                if (_transcriptDisplay.Count > 6) _transcriptDisplay.RemoveAt(0);
                if (debugLog) Debug.Log($"[Conversation] Bot {speaker.botId}: {line}");

                yield return new WaitForSeconds(lineDuration);
            }
        }
        finally
        {
            _currentSpeaker = null;
            _currentLine = "";
            foreach (var b in _bots) if (b != null) b.EndConversation();
            if (debugLog) Debug.Log("[Conversation] finished; bots resume");
        }
    }

    private List<BotController> LivingBots()
    {
        return _bots.FindAll(b => b != null && b.IsAlive);               
    }

    private Vector3 GroupCenter(List<BotController> bots)
    {
        if (bots.Count == 0) return _spawnCentroid;
        Vector3 s = Vector3.zero;
        foreach (var b in bots) s += b.transform.position;
        return s / bots.Count;
    }

    private string DisplayName(BotController b)
    {
        var p = b.GetComponent<BotProfile>();
        return (p != null && !string.IsNullOrEmpty(p.characterName)) ? p.characterName : $"Bot {b.botId}";
    }

    private IEnumerator RequestLine(BotController speaker, List<Line> transcript, System.Action<string> onResult)
    {
        var prof = speaker.GetComponent<BotProfile>();
        var persona = new PersonaJson { bot_id = speaker.botId, name = DisplayName(speaker) };
        if (prof != null)
        {
            persona.backstory = prof.backstory;
            persona.goals = prof.goals;
            persona.relationships = prof.RelationshipsText();
            persona.memory = prof.memory;
        }

        var payload = new ChatRequest
        {
            speaker_id = speaker.botId,
            bot_count = _bots.Count,
            topic = topic,
            session_id = GameStats.Instance != null ? GameStats.Instance.SessionId : "",
            persona = persona,
            transcript_wrapper = new LineList { items = transcript }
        };
        byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));

        using (var uwr = new UnityWebRequest(chatUrl, "POST"))
        {
            uwr.uploadHandler = new UploadHandlerRaw(body);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json");
            uwr.timeout = 10;
            yield return uwr.SendWebRequest();

            if (uwr.result == UnityWebRequest.Result.Success)
            {
                string txt = null;
                try
                {
                    var resp = JsonUtility.FromJson<ChatResponse>(uwr.downloadHandler.text);
                    txt = resp != null ? resp.text : null;
                }
                catch (System.Exception e)
                {
                    if (debugLog) Debug.LogWarning($"[Conversation] bad /chat response: {e.Message}");
                }
                onResult(txt);
            }
            else
            {
                if (debugLog) Debug.LogWarning($"[Conversation] /chat failed: {uwr.error}");
                onResult(null);
            }
        }
    }

    void OnGUI()
    {
        if (_currentSpeaker != null && !string.IsNullOrEmpty(_currentLine))
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 sp = cam.WorldToScreenPoint(_currentSpeaker.transform.position + Vector3.up * 2.5f);
                if (sp.z > 0f)
                {
                    var style = new GUIStyle(GUI.skin.box)
                    {
                        fontSize = bubbleFontSize,
                        fontStyle = FontStyle.Bold,
                        wordWrap = true,
                        alignment = TextAnchor.MiddleCenter
                    };
                    style.normal.textColor = Color.white;

                    var content = new GUIContent($"{_currentName}\n{_currentLine}");
                    float w = bubbleWidth;
                    float h = style.CalcHeight(content, w) + 16f;
                    GUI.color = Color.white;
                    GUI.Box(new Rect(sp.x - w / 2f, Screen.height - sp.y - h, w, h), content, style);
                }
            }
        }

        if (_transcriptDisplay.Count > 0)
        {
            var header = new GUIStyle(GUI.skin.label) { fontSize = logFontSize + 4, fontStyle = FontStyle.Bold };
            header.normal.textColor = Color.white;
            var label = new GUIStyle(GUI.skin.label) { fontSize = logFontSize, wordWrap = true };
            label.normal.textColor = Color.white;

            float areaW = 820f;
            float areaH = (logFontSize + 12f) * (_transcriptDisplay.Count + 2) + 24f;
            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(12, Screen.height - areaH - 12, areaW, areaH), GUI.skin.box);
            GUILayout.Label("SQUAD COMMS", header);
            foreach (var l in _transcriptDisplay) GUILayout.Label(l, label);
            GUILayout.EndArea();
        }
    }

    [System.Serializable] public class Line { public int bot_id; public string text; }
    [System.Serializable] public class LineList { public List<Line> items; }
    [System.Serializable] public class PersonaJson { public int bot_id; public string name; public string backstory; public string goals; public string relationships; public string memory; }
    [System.Serializable] public class ChatRequest { public int speaker_id; public int bot_count; public string topic; public string session_id; public PersonaJson persona; public LineList transcript_wrapper; }
    [System.Serializable] public class ChatResponse { public int bot_id; public string text; }
}
