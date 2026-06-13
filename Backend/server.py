import asyncio
import json
import logging
import time
import threading
import queue
from dataclasses import dataclass, asdict
from typing import Optional

import httpx
from flask import Flask, request, jsonify


HOST = "0.0.0.0"
PORT = 8765
OLLAMA_URL = "http://localhost:11434/api/generate"
OLLAMA_MODEL = "llama3.2"
AI_DECISION_INTERVAL = 0.8   
BOT_COUNT = 4
FIRE_RANGE = 25.0           
LOG_LEVEL = logging.INFO

logging.basicConfig(level=LOG_LEVEL, format="%(asctime)s [%(levelname)s] %(message)s", datefmt="%H:%M:%S")
log = logging.getLogger("BotServer")

app = Flask(__name__)
app.config["JSON_SORT_KEYS"] = False


@dataclass
class BotState:
    bot_id: int
    position: dict
    rotation_y: float = 0.0
    health: float = 100.0
    is_alive: bool = True
    nearby_enemies: list = None      
    nearby_allies: list = None
    last_decision_time: float = 0.0

    def __post_init__(self):
        if self.position is None:
            self.position = {"x": 0, "y": 0, "z": 0}
        if self.nearby_enemies is None:
            self.nearby_enemies = []
        if self.nearby_allies is None:
            self.nearby_allies = []


@dataclass
class BotCommand:
    bot_id: int
    action: str = "patrol"        
    target_id: int = -1           
    fire: bool = False
    sprint: bool = False
    action_label: str = "idle"


SYSTEM_PROMPT = """You are the tactical brain for one first-person-shooter bot.
You receive the bot's state and a numbered list of enemies it can see.
You DO NOT steer movement directly — the game engine handles pathfinding.
You only choose a high-level action, which enemy to focus, and whether to fire.

Reply with ONLY a valid JSON object, no prose, no markdown:
{
  "action": "patrol" | "pursue" | "engage" | "retreat" | "idle",
  "target_index": <int, the Enemy N to focus, or -1 if none>,
  "fire": <bool>,
  "sprint": <bool>,
  "action_label": "<short description>"
}

Rules:
- No enemies visible -> action "patrol", target_index -1, fire false.
- Enemy visible but far / no clear shot -> "pursue" the closest one, sprint true, fire false.
- Enemy within fire range AND has line of sight -> "engage", fire true.
- Pick target_index = the closest immediate threat. Prefer enemies with line of sight.
- If health < 30 -> "retreat" but keep target_index set so the bot keeps facing it; fire only if it still has a clear shot.
- Never fire at an enemy without line of sight.
"""


def _build_user_prompt(bot: BotState) -> str:
    if bot.nearby_enemies:
        lines = []
        for idx, e in enumerate(bot.nearby_enemies):
            lines.append(
                f"  Enemy {idx}: dist={e.get('distance', 0):.1f}m "
                f"angle={e.get('angle', 0):.0f} deg los={e.get('has_los', False)}"
            )
        enemy_str = "\n".join(lines)
    else:
        enemy_str = "  none"

    return f"""Bot {bot.bot_id} state:
- Health: {bot.health:.0f}/100
- Fire range: {FIRE_RANGE:.0f}m
- Enemies visible ({len(bot.nearby_enemies)}):
{enemy_str}

Choose the action JSON now."""


async def ask_ollama(bot: BotState, http: httpx.AsyncClient) -> BotCommand:
    payload = {
        "model": OLLAMA_MODEL,
        "prompt": _build_user_prompt(bot),
        "system": SYSTEM_PROMPT,
        "stream": False,
        "format": "json",  
        "options": {"temperature": 0.3, "num_predict": 120},
    }

    try:
        resp = await http.post(OLLAMA_URL, json=payload, timeout=8.0)
        resp.raise_for_status()
        raw = resp.json().get("response", "").strip()
        if raw.startswith("```"):
            raw = raw.split("```")[1]
            if raw.startswith("json"):
                raw = raw[4:]
        data = json.loads(raw)
        return _resolve_command(bot, data)
    except (httpx.RequestError, httpx.HTTPStatusError) as e:
        log.warning(f"[Bot {bot.bot_id}] Ollama failed: {e} -> fallback")
    except (json.JSONDecodeError, KeyError, ValueError, TypeError) as e:
        log.warning(f"[Bot {bot.bot_id}] bad AI JSON: {e} -> fallback")

    return _fallback_command(bot)


def _resolve_command(bot: BotState, data: dict) -> BotCommand:
    """Turn the LLM's target_index into a concrete target_id, and sanity-check."""
    action = str(data.get("action", "patrol")).lower()
    fire = bool(data.get("fire", False))
    sprint = bool(data.get("sprint", False))

    try:
        idx = int(data.get("target_index", -1))
    except (TypeError, ValueError):
        idx = -1

    target_id = -1
    enemy = None
    if 0 <= idx < len(bot.nearby_enemies):
        enemy = bot.nearby_enemies[idx]
    elif bot.nearby_enemies:                 
        enemy = bot.nearby_enemies[0]
        idx = 0

    if enemy is not None:
        target_id = int(enemy.get("id", -1))
        dist = enemy.get("distance", 999)
        los = bool(enemy.get("has_los", False))
        in_range = dist <= FIRE_RANGE
        low_hp = bot.health < 30

       
        if low_hp and action == "retreat":
            fire = los and in_range            
        elif in_range and los:
            action = "engage"
            fire = True                       
        else:
            action = "pursue"                  
            fire = False
    else:
        action = "patrol"
        fire = False

    return BotCommand(
        bot_id=bot.bot_id,
        action=action,
        target_id=target_id,
        fire=fire,
        sprint=sprint,
        action_label=str(data.get("action_label", action)),
    )


def _fallback_command(bot: BotState) -> BotCommand:
    """Deterministic rule-based brain when Ollama is slow/unavailable."""
    if not bot.is_alive:
        return BotCommand(bot_id=bot.bot_id, action="idle", action_label="dead")
    if not bot.nearby_enemies:
        return BotCommand(bot_id=bot.bot_id, action="patrol", action_label="patrol")

    target = bot.nearby_enemies[0]  
    tid = int(target.get("id", -1))
    dist = target.get("distance", 999)
    los = target.get("has_los", False)
    low_hp = bot.health < 30

    if low_hp:
        return BotCommand(bot_id=bot.bot_id, action="retreat", target_id=tid,
                          fire=(los and dist <= FIRE_RANGE), action_label="retreat")
    if dist <= FIRE_RANGE and los:
        return BotCommand(bot_id=bot.bot_id, action="engage", target_id=tid, fire=True, action_label="engage")
    return BotCommand(bot_id=bot.bot_id, action="pursue", target_id=tid, sprint=True, action_label="pursue")


class BotManager:
    def __init__(self):
        self.bots: dict[int, BotState] = {}
        self.http = httpx.AsyncClient()
        self._pending: dict[int, BotCommand] = {}

    def update_state(self, data: dict):
        if data.get("type") != "game_state":
            return
        for bd in data.get("bots", []):
            bid = bd.get("bot_id")
            if bid is None:
                continue
            enemies = bd.get("nearby_enemies_wrapper", {}).get("items", [])
            self.bots[bid] = BotState(
                bot_id=bid,
                position=bd.get("position", {"x": 0, "y": 0, "z": 0}),
                rotation_y=bd.get("rotation_y", 0.0),
                health=bd.get("health", 100.0),
                is_alive=bd.get("is_alive", True),
                nearby_enemies=enemies,
                nearby_allies=bd.get("nearby_allies", []),
                last_decision_time=self.bots[bid].last_decision_time if bid in self.bots else 0.0,
            )

    async def think(self):
        now = time.time()
        tasks = []
        for bot in self.bots.values():
            if not bot.is_alive:
                self._pending[bot.bot_id] = BotCommand(bot_id=bot.bot_id, action="idle", action_label="dead")
                continue
            if now - bot.last_decision_time >= AI_DECISION_INTERVAL:
                bot.last_decision_time = now
                tasks.append(self._decide(bot))

        if tasks:
            for r in await asyncio.gather(*tasks, return_exceptions=True):
                if isinstance(r, BotCommand):
                    self._pending[r.bot_id] = r
                elif isinstance(r, Exception):
                    log.error(f"decision error: {r}")

    async def _decide(self, bot: BotState) -> BotCommand:
        cmd = await ask_ollama(bot, self.http)
        log.info(f"[Bot {bot.bot_id}] -> {cmd.action} target={cmd.target_id} fire={cmd.fire} "
                 f"enemies_seen={len(bot.nearby_enemies)}")
        return cmd

    def latest_commands(self) -> list[dict]:
        return [asdict(c) for c in self._pending.values()]

    async def close(self):
        await self.http.aclose()


bot_manager = BotManager()
state_queue: "queue.Queue[dict]" = queue.Queue()
result_lock = threading.Lock()
latest_commands: list[dict] = []


def run_async_loop():
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)

    async def worker():
        global latest_commands
        while True:
            try:
                drained = False
                while True:
                    try:
                        bot_manager.update_state(state_queue.get_nowait())
                        drained = True
                    except queue.Empty:
                        break
                await bot_manager.think()
                cmds = bot_manager.latest_commands()
                with result_lock:
                    latest_commands = cmds
            except Exception as e:
                log.error(f"worker error: {e}")
            await asyncio.sleep(0.05)

    loop.run_until_complete(worker())


@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok", "bots": BOT_COUNT}), 200


@app.route("/command", methods=["POST"])
def command():
    try:
        data = request.get_json(silent=True)
        if not data:
            return jsonify({"error": "no json"}), 400
        if data.get("type") == "game_state":
            state_queue.put(data)
        with result_lock:
            cmds = list(latest_commands)
        return jsonify({"commands_wrapper": {"items": cmds}}), 200
    except Exception as e:
        log.error(f"/command error: {e}")
        return jsonify({"error": str(e)}), 500


if __name__ == "__main__":
    threading.Thread(target=run_async_loop, daemon=True).start()
    log.info(f"AI Bot Server on http://{HOST}:{PORT} | model={OLLAMA_MODEL} | bots={BOT_COUNT}")
    app.run(host=HOST, port=PORT, debug=False, use_reloader=False, threaded=True)