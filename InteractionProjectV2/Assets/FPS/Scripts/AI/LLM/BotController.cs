using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.FPS.AI;                       
using FpsHealth = Unity.FPS.Game.Health;  


[RequireComponent(typeof(EnemyController))]
[RequireComponent(typeof(BotSensor))]
public class BotController : MonoBehaviour
{
    public enum BotAction { Idle, Patrol, Pursue, Engage, Retreat }

    [Header("Identity")]
    public int botId = 0;

    [Header("Movement")]
    public float walkSpeed = 4f;
    public float sprintSpeed = 7f;
    public float engageRange = 12f;     
    public float retreatDistance = 12f;
    public float wanderRadius = 15f;
    public Transform[] patrolPoints;

    [Header("Combat")]
    public float fireRange = 25f;
    [Tooltip("Seconds to keep chasing a target's last-seen spot after losing sight of it.")]
    public float searchMemory = 4f;

    [Header("Debug")]
    public bool debugLog = true;
    public float logInterval = 1f;

    public BotAction CurrentAction { get; private set; } = BotAction.Patrol;
    public bool IsAlive => _health != null && _health.CurrentHealth > 0f;
    public float HealthValue => _health != null ? _health.CurrentHealth : 0f;
    public BotSensor Sensor => _sensor;
    public int CurrentTargetId { get; private set; } = -1;
    public bool FiringNow { get; private set; }
    public string StatusLine { get; private set; } = "init";

    public bool ConversationHold { get; private set; }
    public float regroupArriveDist = 2.5f;
    private Vector3 _regroupPoint;
    private Vector3 _facePoint;
    private bool _hasFace;
    public bool AtRegroup => Vector3.Distance(transform.position, _regroupPoint) <= regroupArriveDist;
    public void BeginRegroup(Vector3 point) { ConversationHold = true; _regroupPoint = point; }
    public void EndConversation() { ConversationHold = false; _hasFace = false; }
    public void FacePoint(Vector3 p) { _facePoint = p; _hasFace = true; }

    private int _preferredTargetId = -1;
    private bool _backendRetreat;
    private bool _backendSprint;

    private EnemyController _enemy;
    private NavMeshAgent _agent;
    private FpsHealth _health;
    private BotSensor _sensor;
    private BotProfile _profile;
    private List<BotController> _allies;
    private float _retreatAtFraction = 0f;
    private int _patrolIndex;
    private Vector3 _wanderPoint;
    private bool _hasWander;
    private Vector3 _lastKnownPos;
    private float _lastSeenTime = -999f;
    private float _nextLog;

    void Awake()
    {
        _enemy = GetComponent<EnemyController>();
        _agent = GetComponent<NavMeshAgent>();
        _health = GetComponent<FpsHealth>();
        _sensor = GetComponent<BotSensor>();
        _profile = GetComponent<BotProfile>();
        if (_agent != null) _agent.updateRotation = false;
        ApplyProfile();
    }

    void Start()
    {
        _allies = new List<BotController>(FindObjectsByType<BotController>(FindObjectsSortMode.None));
    }

    private void ApplyProfile()
    {
        if (_profile == null) return;
        engageRange = Mathf.Lerp(16f, 5f, _profile.aggression);          
        _retreatAtFraction = Mathf.Lerp(0.05f, 0.6f, _profile.caution);  
    }

    private float HealthFraction => (_health != null && _health.MaxHealth > 0f)
        ? _health.CurrentHealth / _health.MaxHealth : 1f;

    public void ApplyCommand(string action, int targetId, bool fire, bool sprint)
    {
        _preferredTargetId = targetId;
        _backendRetreat = ParseAction(action) == BotAction.Retreat;
        _backendSprint = sprint;
        if (debugLog)
            Debug.Log($"[Bot {botId}] CMD action={action} target={targetId} fire={fire} sprint={sprint}", this);
    }

    void Update()
    {
        if (_agent == null || !_agent.isOnNavMesh) { StatusLine = "no NavMesh!"; return; }
        if (!IsAlive) { _agent.isStopped = true; StatusLine = "dead"; return; }
        if (ConversationHold) { HandleRegroup(); return; }

        EnemyContact target = ResolveTarget();
        CurrentTargetId = target != null ? target.id : -1;

        if (target != null)
        {
            _lastKnownPos = target.Position;
            _lastSeenTime = Time.time;
        }

        FiringNow = false;

        bool wantRetreat = _backendRetreat || HealthFraction < _retreatAtFraction;

        if (target != null && wantRetreat)
        {
            CurrentAction = BotAction.Retreat;
            Retreat(target);
            AimAt(target, allowFire: target.hasLOS && target.distance <= fireRange);
        }
        else if (target != null)
        {
            bool clearShot = target.hasLOS && target.distance <= fireRange;
            if (clearShot)
            {
                CurrentAction = BotAction.Engage;
                _agent.speed = walkSpeed;
                Drive(target.Position, engageRange);          
                AimAt(target, allowFire: true);               
            }
            else
            {
                CurrentAction = BotAction.Pursue;
                _agent.speed = sprintSpeed;                   
                Drive(target.Position, 1.5f);
                AimAt(target, allowFire: false);
                StatusLine = target.hasLOS ? $"pursue d={target.distance:F1} (out of range)"
                                           : $"pursue d={target.distance:F1} (no LOS)";
            }
        }
        else if (Time.time - _lastSeenTime < searchMemory)
        {
            CurrentAction = BotAction.Pursue;
            _agent.speed = sprintSpeed;
            Drive(_lastKnownPos, 1.5f);
            StatusLine = "searching last-seen spot";
        }
        else
        {
            CurrentAction = BotAction.Patrol;
            _agent.speed = walkSpeed;
            DoPatrol();
            StatusLine = "patrolling (no target)";
        }

        if (debugLog && Time.time >= _nextLog)
        {
            _nextLog = Time.time + logInterval;
            Debug.Log($"[Bot {botId}] {StatusLine} | {CurrentAction} | seen={_sensor.Current.Count}", this);
        }
    }

    private EnemyContact ResolveTarget()
    {
        if (_preferredTargetId != -1)
        {
            var t = _sensor.GetById(_preferredTargetId);
            if (t != null && t.IsValid) return t;
        }
        if (_profile != null && _profile.teamwork > 0.5f)
        {
            var focus = FocusFireTarget();
            if (focus != null) return focus;
        }
        return _sensor.GetClosest();
    }

    private EnemyContact FocusFireTarget()
    {
        if (_allies == null) return null;
        foreach (var ally in _allies)
        {
            if (ally == null || ally == this || !ally.IsAlive) continue;
            if (_profile.BondTo(ally.botId) <= 0f) continue;
            int tid = ally.CurrentTargetId;
            if (tid == -1) continue;
            var c = _sensor.GetById(tid);
            if (c != null && c.IsValid) return c;
        }
        return null;
    }

    private void Drive(Vector3 worldPos, float stopDist)
    {
        _agent.stoppingDistance = stopDist;
        _agent.isStopped = false;
        if (NavMesh.SamplePosition(worldPos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            _enemy.SetNavDestination(hit.position);       
        else
            _enemy.SetNavDestination(worldPos);
    }

    private void Retreat(EnemyContact target)
    {
        Vector3 away = (transform.position - target.Position).normalized;
        Vector3 desired = transform.position + away * retreatDistance;
        _agent.stoppingDistance = 0.5f;
        _agent.isStopped = false;
        if (NavMesh.SamplePosition(desired, out NavMeshHit hit, retreatDistance, NavMesh.AllAreas))
            _enemy.SetNavDestination(hit.position);
        StatusLine = $"retreating d={target.distance:F1}";
    }

    private void AimAt(EnemyContact target, bool allowFire)
    {
        Vector3 aim = target.AimPoint;
        _enemy.OrientTowards(target.Position);
        _enemy.OrientWeaponsTowards(aim);
        if (allowFire)
        {
            FiringNow = _enemy.TryAtack(aim);
            if (FiringNow && GameStats.Instance != null) GameStats.Instance.ReportShot(botId);
            StatusLine = FiringNow ? $"FIRING d={target.distance:F1}" : $"in range d={target.distance:F1} (weapon cooling)";
        }
    }

    private void DoPatrol()
    {
        _agent.stoppingDistance = 0.5f;
        _agent.isStopped = false;
        bool arrived = !_agent.pathPending && _agent.remainingDistance <= _agent.stoppingDistance + 0.1f;

        if (patrolPoints != null && patrolPoints.Length > 0)
        {
            if (arrived || !_agent.hasPath)
            {
                _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                _enemy.SetNavDestination(patrolPoints[_patrolIndex].position);
            }
        }
        else if (!_hasWander || arrived)
        {
            Vector3 random = transform.position + Random.insideUnitSphere * wanderRadius;
            if (NavMesh.SamplePosition(random, out NavMeshHit hit, wanderRadius, NavMesh.AllAreas))
            {
                _wanderPoint = hit.position;
                _hasWander = true;
                _enemy.SetNavDestination(_wanderPoint);
            }
        }
    }

    private void HandleRegroup()
    {
        _agent.speed = walkSpeed;
        if (!AtRegroup)
        {
            _agent.stoppingDistance = 0.5f;
            _agent.isStopped = false;
            if (NavMesh.SamplePosition(_regroupPoint, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                _enemy.SetNavDestination(hit.position);
            StatusLine = "regrouping";
        }
        else
        {
            _agent.isStopped = true;
            if (_hasFace) _enemy.OrientTowards(_facePoint);
            StatusLine = "regrouped (talking)";
        }
    }

    private BotAction ParseAction(string a)
    {
        switch ((a ?? "").ToLowerInvariant())
        {
            case "engage":
            case "attack":
            case "aggressive_attack": return BotAction.Engage;
            case "pursue":            return BotAction.Pursue;
            case "retreat":           return BotAction.Retreat;
            case "patrol":            return BotAction.Patrol;
            default:                  return BotAction.Idle;
        }
    }
}
