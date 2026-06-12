using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.FPS.Game;

namespace Unity.FPS.AI
{
   
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(Health))]
    [RequireComponent(typeof(Actor))]
    public class BotController : MonoBehaviour
    {
        [Header("Bot Identity")]
        public int BotId = 0;

        [Header("Movement")]
        [Tooltip("Base movement speed")]
        public float MoveSpeed = 5f;
        [Tooltip("Sprint speed multiplier")]
        public float SprintMultiplier = 1.8f;
        [Tooltip("How fast the bot turns (deg/sec)")]
        public float TurnSpeed = 200f;

        [Header("Combat")]
        [Tooltip("Weapon held by this bot")]
        public WeaponController Weapon;
        [Tooltip("Transform used as the eye / muzzle reference")]
        public Transform EyeTransform;

        [Header("Detection")]
        [Tooltip("Layer mask for detecting walls when doing LOS checks")]
        public LayerMask ObstacleMask;

        public float CurrentHealth => _health ? _health.CurrentHealth : 0f;
        public bool  IsAlive       => _health ? _health.CurrentHealth > 0f : false;

        private NavMeshAgent   _agent;
        private Health         _health;
        private Actor          _actor;
        private BotCommandData _currentCmd;

        private float _targetYaw;
        private float _targetPitch;
        private bool  _isShooting;
        private float _shootTimer;

        void Awake()
        {
            _agent  = GetComponent<NavMeshAgent>();
            _health = GetComponent<Health>();
            _actor  = GetComponent<Actor>();
        }

        void Start()
        {
            _health.OnDie += OnDie;

            _agent.speed         = MoveSpeed;
            _agent.angularSpeed  = 0f;  
            _agent.updateRotation = false;

            _targetYaw = transform.eulerAngles.y;

            BotManager.Instance?.RegisterBot(this);
            AIServerBridge.Instance?.NotifyBotSpawned(BotId, transform.position);
        }

        void Update()
        {
            if (!IsAlive) return;

            ApplyMovement();
            ApplyRotation();
            ApplyShooting();
        }

        void OnDestroy()
        {
            if (_health) _health.OnDie -= OnDie;
            BotManager.Instance?.UnregisterBot(this);
        }


        public void ReceiveCommand(BotCommandData cmd)
        {
            _currentCmd = cmd;

            _targetYaw   = transform.eulerAngles.y + cmd.look_y;
            _targetPitch = Mathf.Clamp(
                (EyeTransform ? EyeTransform.localEulerAngles.x : 0f) + cmd.look_x,
                -40f, 40f);

            _isShooting = cmd.fire;
        }

        private void ApplyMovement()
        {
            if (_currentCmd == null) return;

            float speed = MoveSpeed * (_currentCmd.sprint ? SprintMultiplier : 1f);
            _agent.speed = speed;

            Vector3 localDir = new Vector3(_currentCmd.move_x, 0f, _currentCmd.move_z);
            if (localDir.sqrMagnitude > 0.01f)
            {
                localDir = Vector3.ClampMagnitude(localDir, 1f);
                Vector3 worldDir = transform.TransformDirection(localDir);
                Vector3 dest = transform.position + worldDir * speed * Time.deltaTime * 10f;

                if (NavMesh.SamplePosition(dest, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                    _agent.SetDestination(hit.position);
            }
            else
            {
                _agent.ResetPath();
            }
        }

        private void ApplyRotation()
        {
            float currentYaw = transform.eulerAngles.y;
            float newYaw = Mathf.MoveTowardsAngle(currentYaw, _targetYaw, TurnSpeed * Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, newYaw, 0f);

            if (EyeTransform != null)
            {
                float currentPitch = EyeTransform.localEulerAngles.x;
                float newPitch = Mathf.MoveTowardsAngle(currentPitch, _targetPitch, TurnSpeed * Time.deltaTime);
                EyeTransform.localRotation = Quaternion.Euler(newPitch, 0f, 0f);
            }
        }

        private void ApplyShooting()
        {
            if (Weapon == null) return;

            if (_isShooting)
            {
                Weapon.HandleShootInputs(false, true, false); 
            }
            else
            {
                Weapon.HandleShootInputs(false, false, false);
            }
        }


        public List<Actor> GetNearbyActors(float radius, bool foe)
        {
            var result = new List<Actor>();
            var allActors = FindObjectsByType<Actor>(FindObjectsSortMode.None);

            if (Time.frameCount % 300 == 0 && BotId == 0)
            {
                Debug.Log($"=== [Bot {BotId}] ALL ACTORS IN SCENE (at frame {Time.frameCount}) ===");
                foreach (var actor in allActors)
                {
                    float d = Vector3.Distance(transform.position, actor.transform.position);
                    bool isFoe = actor.Affiliation != _actor.Affiliation;
                    Debug.Log($"  {actor.name}: aff={actor.Affiliation} isFoe={isFoe} dist={d:F1}m radius={radius}m");
                }
                Debug.Log($"=== END ACTOR LIST ===");
            }

            foreach (var actor in allActors)
            {
                if (actor == _actor) continue;
                bool isFoe = actor.Affiliation != _actor.Affiliation;
                if (isFoe != foe) continue;
                
                float distance = Vector3.Distance(transform.position, actor.transform.position);
                if (distance > radius)
                    continue;
                
                result.Add(actor);
            }

            if (foe && result.Count > 0)
            {
                foreach (var enemy in result)
                {
                    Debug.Log($"[Bot {BotId}] DETECTED ENEMY: {enemy.name} (aff={enemy.Affiliation})");
                }
            }

            return result;
        }

        private void OnDie()
        {
            Debug.Log($"[Bot {BotId}] Died");
            AIServerBridge.Instance?.NotifyBotDied(BotId);
            _agent.isStopped = true;
            _isShooting = false;
        }
    }
}
