using System.Collections.Generic;
using UnityEngine;
using FpsHealth = Unity.FPS.Game.Health;  
using Unity.FPS.Game;

public class EnemyContact
{
    public FpsHealth health;
    public Transform tf;
    public Collider col;
    public int id;
    public float distance;
    public float angle;
    public bool hasLOS;

    public Vector3 Position => tf != null ? tf.position : Vector3.zero;
    public Vector3 AimPoint => col != null ? col.bounds.center : Position + Vector3.up * 0.8f;
    public bool IsValid => health != null && health.CurrentHealth > 0f && tf != null;
}


[RequireComponent(typeof(BotController))]
public class BotSensor : MonoBehaviour
{
    [Header("Detection")]
    public float detectionRadius = 60f;
    public string ignoreTag = "Player";
    public Transform eye;
    public float scanInterval = 0.2f;
    [Tooltip("How often to re-scan the scene for Health objects (cheap; troopers rarely spawn).")]
    public float healthRefreshInterval = 1f;

    [Header("Debug")]
    public bool debugLog = true;
    public bool debugGizmos = true;
    public float logInterval = 1f;

    public IReadOnlyList<EnemyContact> Current => _current;
    private readonly List<EnemyContact> _current = new List<EnemyContact>();
    private FpsHealth[] _allHealth = new FpsHealth[0];
    private float _nextScan, _nextLog, _nextHealthRefresh;
    private int _dbgTotal, _dbgIsBot, _dbgPlayer, _dbgDead, _dbgFar, _dbgNoLOS, _dbgKept;

    public Vector3 EyePosition => eye != null ? eye.position : transform.position + Vector3.up * 1.6f;

    void Update()
    {
        if (Time.time < _nextScan) return;
        _nextScan = Time.time + scanInterval;
        Scan();
    }

    private void Scan()
    {
        if (Time.time >= _nextHealthRefresh)
        {
            _nextHealthRefresh = Time.time + healthRefreshInterval;
            _allHealth = FindObjectsByType<FpsHealth>(FindObjectsSortMode.None);
        }

        _current.Clear();
        _dbgTotal = _allHealth.Length;
        _dbgIsBot = _dbgPlayer = _dbgDead = _dbgFar = _dbgNoLOS = _dbgKept = 0;

        for (int i = 0; i < _allHealth.Length; i++)
        {
            FpsHealth h = _allHealth[i];
            if (h == null) continue;
            if (h.CurrentHealth <= 0f) { _dbgDead++; continue; }
            if (h.transform == transform || h.GetComponentInParent<BotController>() != null) { _dbgIsBot++; continue; }
            var actor = h.GetComponentInParent<Actor>();
            if (actor != null && actor.Affiliation == 0) { _dbgPlayer++; continue; }
            if (!string.IsNullOrEmpty(ignoreTag) && h.CompareTag(ignoreTag)) { _dbgPlayer++; continue; }

            Vector3 to = h.transform.position - transform.position;
            float dist = to.magnitude;
            if (dist > detectionRadius) { _dbgFar++; continue; }

            Collider col = h.GetComponentInChildren<Collider>();
            bool los = HasLineOfSight(col, h);
            if (!los) _dbgNoLOS++;

            _current.Add(new EnemyContact
            {
                health = h, tf = h.transform, col = col, id = h.GetInstanceID(),
                distance = dist,
                angle = Vector3.SignedAngle(transform.forward, new Vector3(to.x, 0f, to.z), Vector3.up),
                hasLOS = los
            });
            _dbgKept++;
        }

        _current.Sort((a, b) => a.distance.CompareTo(b.distance));

        if (debugLog && Time.time >= _nextLog)
        {
            _nextLog = Time.time + logInterval;
            if (_dbgKept == 0)
                Debug.Log($"[Sensor {name}] 0 enemies. Health objs in scene={_dbgTotal} -> " +
                          $"self/allyBots={_dbgIsBot}, player={_dbgPlayer}, dead={_dbgDead}, beyond {detectionRadius}m={_dbgFar}.", this);
            else
            {
                var e = _current[0];
                Debug.Log($"[Sensor {name}] sees {_dbgKept}. Closest '{e.tf.name}' dist={e.distance:F1}m LOS={e.hasLOS} (noLOS={_dbgNoLOS})", this);
            }
        }
    }

    private bool HasLineOfSight(Collider targetCol, FpsHealth targetHealth)
    {
        Vector3 origin = EyePosition;
        Vector3 aim = targetCol != null ? targetCol.bounds.center : targetHealth.transform.position + Vector3.up * 0.8f;
        Vector3 dir = aim - origin;
        float d = dir.magnitude;
        if (d < 0.01f) return true;

        if (FirstHitIgnoringSelf(origin, dir / d, d + 0.5f, transform, out RaycastHit hit))
            return hit.collider.GetComponentInParent<FpsHealth>() == targetHealth;
        return true;
    }

    public static bool FirstHitIgnoringSelf(Vector3 origin, Vector3 dir, float maxDist, Transform self, out RaycastHit best)
    {
        RaycastHit[] hits = Physics.RaycastAll(origin, dir, maxDist, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            Transform t = hits[i].collider.transform;
            if (t == self || t.IsChildOf(self)) continue;
            best = hits[i];
            return true;
        }
        best = default;
        return false;
    }

    public EnemyContact GetById(int id)
    {
        for (int i = 0; i < _current.Count; i++)
            if (_current[i].id == id && _current[i].IsValid) return _current[i];
        return null;
    }

    public EnemyContact GetClosest()
    {
        for (int i = 0; i < _current.Count; i++)
            if (_current[i].IsValid) return _current[i];
        return null;
    }

    void OnDrawGizmos()
    {
        if (!debugGizmos) return;
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, detectionRadius);
        if (!Application.isPlaying) return;

        Vector3 origin = EyePosition;
        foreach (var c in _current)
        {
            if (!c.IsValid) continue;
            Gizmos.color = c.hasLOS ? Color.green : Color.red;
            Gizmos.DrawLine(origin, c.AimPoint);
            Gizmos.DrawWireSphere(c.AimPoint, 0.4f);
        }
    }
}
