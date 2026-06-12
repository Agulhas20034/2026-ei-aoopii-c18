using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.FPS.AI;
using Unity.FPS.Game;

namespace Unity.FPS.AI
{
 
    public class BotManager : MonoBehaviour
    {
        [Header("Bot Setup")]
        [Tooltip("Prefab used for each bot. Must have BotController, NavMeshAgent, Health, Actor components.")]
        public GameObject BotPrefab;

        [Tooltip("Parent transform to keep the hierarchy tidy")]
        public Transform BotParent;

        [Tooltip("Number of bots to manage (should match Python server BOT_COUNT)")]
        public int BotCount = 4;

        [Header("Spawn Fallback")]
        [Tooltip("Default spawn points if server doesn't provide positions")]
        public Transform[] DefaultSpawnPoints;

        public static BotManager Instance { get; private set; }

        private BotController[] _bots;
        public BotController[] Bots => _bots;

        private readonly Dictionary<int, BotController> _botById = new();
        private bool _botsSpawned = false;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _bots = new BotController[BotCount];
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void RegisterBot(BotController bot)
        {
            if (bot.BotId < BotCount)
            {
                _bots[bot.BotId] = bot;
                _botById[bot.BotId] = bot;
            }
        }

        public void UnregisterBot(BotController bot)
        {
            if (bot.BotId < BotCount && _bots[bot.BotId] == bot)
            {
                _bots[bot.BotId] = null;
                _botById.Remove(bot.BotId);
            }
        }


       
        public void SpawnBots(SpawnBotsPacket packet)
        {
            if (_botsSpawned)
            {
                Debug.LogWarning("[BotManager] Bots already spawned, ignoring duplicate spawn packet.");
                return;
            }

            if (BotPrefab == null)
            {
                Debug.LogError("[BotManager] BotPrefab is not assigned!");
                return;
            }

            _botsSpawned = true;
            var parent = BotParent ? BotParent : transform;

            for (int i = 0; i < packet.bots.Length && i < BotCount; i++)
            {
                var entry = packet.bots[i];
                Vector3 spawnPos = new Vector3(entry.position.x, entry.position.y, entry.position.z);

                if (spawnPos == Vector3.zero && DefaultSpawnPoints != null && i < DefaultSpawnPoints.Length)
                    spawnPos = DefaultSpawnPoints[i].position;

                GameObject go = Instantiate(BotPrefab, spawnPos, Quaternion.identity, parent);
                go.name = $"Bot_{entry.bot_id}";

                var controller = go.GetComponent<BotController>();
                if (controller == null)
                {
                    Debug.LogError($"[BotManager] BotPrefab missing BotController component!");
                    continue;
                }

                var actor = go.GetComponent<Actor>();
                if (actor != null)
                {
                    actor.Affiliation = 1; 
                    Debug.Log($"[BotManager] Bot {entry.bot_id} Affiliation set to {actor.Affiliation}");
                }
                else
                {
                    Debug.LogWarning($"[BotManager] Bot {entry.bot_id} missing Actor component!");
                }

                controller.BotId = entry.bot_id;

                Debug.Log($"[BotManager] Spawned Bot {entry.bot_id} at {spawnPos}");
            }
        }


        public void ApplyCommands(BotCommandData[] commands)
        {
            if (commands == null) return;

            foreach (var cmd in commands)
            {
                if (_botById.TryGetValue(cmd.bot_id, out var bot) && bot != null)
                {
                    bot.ReceiveCommand(cmd);
                }
                else
                {
                    Debug.LogWarning($"[BotManager] Received command for unknown bot_id={cmd.bot_id}");
                }
            }
        }

        [ContextMenu("Spawn Bots Locally (Debug)")]
        public void SpawnBotsLocalDebug()
        {
            var packet = new SpawnBotsPacket
            {
                bots = new SpawnBotEntry[BotCount]
            };

            for (int i = 0; i < BotCount; i++)
            {
                Vector3 pos = DefaultSpawnPoints != null && i < DefaultSpawnPoints.Length
                    ? DefaultSpawnPoints[i].position
                    : new Vector3(i * 3f, 0f, 0f);

                packet.bots[i] = new SpawnBotEntry
                {
                    bot_id = i,
                    position = new Vec3 { x = pos.x, y = pos.y, z = pos.z }
                };
            }

            SpawnBots(packet);
        }
    }
}
