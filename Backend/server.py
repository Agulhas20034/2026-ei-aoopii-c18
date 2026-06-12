import asyncio
import json
import logging
import random
import time
from dataclasses import dataclass, field, asdict
from typing import Optional
import httpx
import websockets
from websockets.server import WebSocketServerProtocol

HOST = "0.0.0.0"
PORT = 8765
OLLAMA_URL = "http://localhost:11434/api/generate"
OLLAMA_MODEL = "llama3.2"         
AI_DECISION_INTERVAL = 1.5       
BOT_COUNT = 4
LOG_LEVEL = logging.INFO

logging.basicConfig(
    level=LOG_LEVEL,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("BotServer")



@dataclass
class BotState:
    bot_id: int
    position: dict = field(default_factory=lambda: {"x": 0, "y": 0, "z": 0})
    rotation: dict = field(default_factory=lambda: {"y": 0})
    health: float = 100.0
    is_alive: bool = True
    nearby_enemies: list = field(default_factory=list)
    nearby_allies: list = field(default_factory=list)
    last_known_enemy_pos: Optional[dict] = None
    last_decision_time: float = 0.0
    current_action: str = "idle"


@dataclass
class BotCommand:
    bot_id: int
    move_x: float = 0.0       
    move_z: float = 0.0       
    look_y: float = 0.0      
    look_x: float = 0.0       
    fire: bool = False
    jump: bool = False
    sprint: bool = False
    action_label: str = "idle"



SYSTEM_PROMPT = """You are an AI controller for a first-person shooter bot.
You receive the bot's current game state and must decide what action to take.
Reply ONLY with a valid JSON object — no explanations, no markdown.

JSON format:
{
  "move_x": <float -1 to 1>,
  "move_z": <float -1 to 1>,
  "look_y": <float degrees -45 to 45>,
  "look_x": <float degrees -30 to 30>,
  "fire": <bool>,
  "jump": <bool>,
  "sprint": <bool>,
  "action_label": <short string describing the action>
}

Guidelines:
- You MUST engage ALL nearby enemies, not just the first one.
- Prioritize enemies within attack range (< 15 units) over distant ones.
- If multiple enemies exist, focus on the closest immediate threat, but remember the others.
- If enemies are visible, move toward them and fire when in range.
- If no enemies in range, move toward the closest one.
- If an enemy is a stationary turret or elevated, prioritize destroying it over mobile enemies.
- If health < 30%, retreat backward but keep facing enemies.
- Use sprint=true when advancing on distant enemies, but do not sprint under fire.
- Fire=true when enemies are within attack distance (roughly < 15 units).
- Keep look_y and look_x within reasonable ranges to face threats.
"""


async def ask_ollama(bot: BotState, http_client: httpx.AsyncClient) -> BotCommand:
    """Send bot state to Ollama and get a command back."""

    enemy_list_str = "none"
    if bot.nearby_enemies:
        enemy_lines = []
        for idx, e in enumerate(bot.nearby_enemies):
            dist = e.get('distance', 0)
            angle = e.get('angle', 0)
            epos = e.get('position', {})
            enemy_lines.append(
                f"  Enemy {idx}: dist={dist:.1f}m angle={angle:.0f}° "
                f"pos=({epos.get('x', 0):.1f},{epos.get('y', 0):.1f},{epos.get('z', 0):.1f})"
            )
        enemy_list_str = "\n".join(enemy_lines)

    user_prompt = f"""
Bot {bot.bot_id} state:
- Health: {bot.health:.0f}/100
- Alive: {bot.is_alive}
- Position: x={bot.position.get('x', 0):.1f} y={bot.position.get('y', 0):.1f} z={bot.position.get('z', 0):.1f}
- Facing: {bot.rotation.get('y', 0):.1f}°
- Nearby enemies ({len(bot.nearby_enemies)} total):
{enemy_list_str}
- Nearby allies: {len(bot.nearby_allies)}
- Last known enemy position: {bot.last_known_enemy_pos or 'unknown'}

Decide the bot's next action. Prioritize the most dangerous enemy: closest threats first, but also consider stationary/turret enemies.
"""

    payload = {
        "model": OLLAMA_MODEL,
        "prompt": user_prompt,
        "system": SYSTEM_PROMPT,
        "stream": False,
        "options": {"temperature": 0.4, "num_predict": 200},
    }

    try:
        resp = await http_client.post(OLLAMA_URL, json=payload, timeout=8.0)
        resp.raise_for_status()
        raw = resp.json().get("response", "").strip()

        if raw.startswith("```"):
            raw = raw.split("```")[1]
            if raw.startswith("json"):
                raw = raw[4:]

        data = json.loads(raw)
        return BotCommand(
            bot_id=bot.bot_id,
            move_x=float(data.get("move_x", 0)),
            move_z=float(data.get("move_z", 0)),
            look_y=float(data.get("look_y", 0)),
            look_x=float(data.get("look_x", 0)),
            fire=bool(data.get("fire", False)),
            jump=bool(data.get("jump", False)),
            sprint=bool(data.get("sprint", False)),
            action_label=str(data.get("action_label", "ai_move")),
        )

    except (httpx.RequestError, httpx.HTTPStatusError) as e:
        log.warning(f"[Bot {bot.bot_id}] Ollama request failed: {e} — using fallback")
    except (json.JSONDecodeError, KeyError, ValueError) as e:
        log.warning(f"[Bot {bot.bot_id}] Bad AI response: {e} — using fallback")

    return _fallback_command(bot)


def _fallback_command(bot: BotState) -> BotCommand:
    """Rule-based fallback when Ollama is unavailable."""
    if not bot.is_alive:
        return BotCommand(bot_id=bot.bot_id, action_label="dead")

    if not bot.nearby_enemies:
        return BotCommand(
            bot_id=bot.bot_id,
            move_z=1.0,
            look_y=random.uniform(-30, 30),
            sprint=False,
            action_label="patrol",
        )

    target_enemy = None
    
    threats_in_range = [e for e in bot.nearby_enemies if e.get("distance", 99) <= 15]
    if threats_in_range:
        target_enemy = min(threats_in_range, key=lambda e: e.get("distance", 99))
    else:
        target_enemy = bot.nearby_enemies[0]
    
    angle = target_enemy.get("angle", 0)
    dist = target_enemy.get("distance", 20)
    
    if dist < 8:
        return BotCommand(
            bot_id=bot.bot_id,
            move_z=0.2,  
            look_y=max(-45, min(45, angle)),
            fire=True,
            sprint=False,
            action_label="aggressive_attack",
        )
    elif dist < 15:
        return BotCommand(
            bot_id=bot.bot_id,
            move_z=0.6,
            look_y=max(-45, min(45, angle)),
            fire=True,
            sprint=False,
            action_label="attack",
        )
    else:
        return BotCommand(
            bot_id=bot.bot_id,
            move_z=1.0,
            look_y=max(-45, min(45, angle)),
            fire=False,
            sprint=True,
            action_label="pursue",
        )



class BotManager:
    def __init__(self):
        self.bots: dict[int, BotState] = {
            i: BotState(bot_id=i) for i in range(BOT_COUNT)
        }
        self.http_client = httpx.AsyncClient()
        self._pending_commands: list[BotCommand] = []

    def update_state(self, data: dict):
        """Apply a game-state update packet from Unity."""
        msg_type = data.get("type")

        if msg_type == "bot_state":
            bot_id = data.get("bot_id")
            if bot_id is not None and bot_id in self.bots:
                bot = self.bots[bot_id]
                bot.position = data.get("position", bot.position)
                bot.rotation = data.get("rotation", bot.rotation)
                bot.health = data.get("health", bot.health)
                bot.is_alive = data.get("is_alive", bot.is_alive)
                bot.nearby_enemies = data.get("nearby_enemies", [])
                bot.nearby_allies = data.get("nearby_allies", [])
                if bot.nearby_enemies:
                    bot.last_known_enemy_pos = bot.nearby_enemies[0].get("position")

        elif msg_type == "game_state":
            bots_data = data.get("bots", [])
            for bd in bots_data:
                bot_id = bd.get("bot_id")
                if bot_id is not None and bot_id in self.bots:
                    bot = self.bots[bot_id]
                    bot.position = bd.get("position", bot.position)
                    bot.rotation = bd.get("rotation", bot.rotation)
                    bot.health = bd.get("health", bot.health)
                    bot.is_alive = bd.get("is_alive", bot.is_alive)
                    bot.nearby_enemies = bd.get("nearby_enemies", [])
                    bot.nearby_allies = bd.get("nearby_allies", [])

        elif msg_type == "bot_died":
            bot_id = data.get("bot_id")
            if bot_id in self.bots:
                self.bots[bot_id].is_alive = False
                log.info(f"[Bot {bot_id}] Died")

        elif msg_type == "bot_spawned":
            bot_id = data.get("bot_id")
            if bot_id in self.bots:
                self.bots[bot_id].is_alive = True
                self.bots[bot_id].health = 100.0
                self.bots[bot_id].position = data.get("position", {"x": 0, "y": 0, "z": 0})
                log.info(f"[Bot {bot_id}] Spawned at {self.bots[bot_id].position}")

    async def think(self):
        """Run AI decisions for all bots that need updating."""
        now = time.time()
        tasks = []
        for bot in self.bots.values():
            if not bot.is_alive:
                continue
            if now - bot.last_decision_time >= AI_DECISION_INTERVAL:
                bot.last_decision_time = now
                tasks.append(self._decide(bot))

        if tasks:
            results = await asyncio.gather(*tasks, return_exceptions=True)
            for r in results:
                if isinstance(r, BotCommand):
                    self._pending_commands.append(r)
                elif isinstance(r, Exception):
                    log.error(f"Decision task error: {r}")

    async def _decide(self, bot: BotState) -> BotCommand:
        cmd = await ask_ollama(bot, self.http_client)
        log.debug(f"[Bot {bot.bot_id}] → {cmd.action_label} | "
                  f"move=({cmd.move_x:.1f},{cmd.move_z:.1f}) "
                  f"look_y={cmd.look_y:.1f}° fire={cmd.fire}")
        return cmd

    def flush_commands(self) -> list[dict]:
        """Return pending commands and clear the queue."""
        cmds = [asdict(c) for c in self._pending_commands]
        self._pending_commands.clear()
        return cmds

    def spawn_packet(self) -> dict:
        """Initial packet telling Unity to spawn all 4 bots."""
        spawn_positions = [
            {"x": 5,  "y": 0, "z": 5},
            {"x": -5, "y": 0, "z": 5},
            {"x": 5,  "y": 0, "z": -5},
            {"x": -5, "y": 0, "z": -5},
        ]
        return {
            "type": "spawn_bots",
            "bots": [
                {"bot_id": i, "position": spawn_positions[i]}
                for i in range(BOT_COUNT)
            ],
        }

    async def close(self):
        await self.http_client.aclose()



connected_clients: set[WebSocketServerProtocol] = set()
bot_manager = BotManager()


async def handle_client(ws: WebSocketServerProtocol):
    addr = ws.remote_address
    log.info(f"Unity connected from {addr}")
    connected_clients.add(ws)

    await ws.send(json.dumps(bot_manager.spawn_packet()))
    log.info("Sent spawn_bots packet to Unity")

    try:
        async for raw_msg in ws:
            try:
                data = json.loads(raw_msg)
                bot_manager.update_state(data)
            except json.JSONDecodeError:
                log.warning(f"Invalid JSON from Unity: {raw_msg[:80]}")
    except websockets.exceptions.ConnectionClosed as e:
        log.info(f"Unity disconnected: {e}")
    finally:
        connected_clients.discard(ws)
        log.info(f"Client {addr} removed")


async def broadcast_commands():
    """Periodically think and broadcast commands to all connected clients."""
    global connected_clients
    while True:
        await asyncio.sleep(0.1)  

        if not connected_clients:
            continue

        await bot_manager.think()
        cmds = bot_manager.flush_commands()
        if not cmds:
            continue

        packet = json.dumps({"type": "bot_commands", "commands": cmds})
        dead = set()
        for ws in connected_clients:
            try:
                await ws.send(packet)
            except websockets.exceptions.ConnectionClosed:
                dead.add(ws)
        connected_clients -= dead


async def main():
    log.info(f"Starting AI Bot Server on ws://{HOST}:{PORT}")
    log.info(f"Ollama model: {OLLAMA_MODEL} @ {OLLAMA_URL}")
    log.info(f"Managing {BOT_COUNT} bots | Decision interval: {AI_DECISION_INTERVAL}s")

    async with websockets.serve(handle_client, HOST, PORT):
        try:
            await broadcast_commands()
        except KeyboardInterrupt:
            pass
        finally:
            await bot_manager.close()
            log.info("Server shut down.")


if __name__ == "__main__":
    asyncio.run(main())
