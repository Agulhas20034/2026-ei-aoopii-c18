import asyncio
import json
import logging
import time
import threading
import queue
from dataclasses import dataclass, asdict
from typing import Optional

import os
import re
import uuid
from datetime import datetime, timezone

try:
    from dotenv import load_dotenv
    load_dotenv()                
except ImportError:
    pass

import httpx
from flask import Flask, request, jsonify

try:
    from pymongo import MongoClient
    _PYMONGO = True
except ImportError:
    _PYMONGO = False

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


MONGODB_URI = os.environ.get("MONGODB_URI", "")
MONGO_DB = os.environ.get("MONGODB_DB", "fps_bots")
sessions_col = None
if _PYMONGO and MONGODB_URI:
    try:
        _mongo = MongoClient(MONGODB_URI, serverSelectionTimeoutMS=5000)
        _mongo.admin.command("ping")
        sessions_col = _mongo[MONGO_DB]["sessions"]
        log.info("Connected to MongoDB Atlas")
    except Exception as e:
        log.warning(f"MongoDB unavailable ({e}); running in-memory only")
        sessions_col = None
elif not _PYMONGO:
    log.info("pymongo not installed; run 'pip install pymongo dnspython' for persistence")
else:
    log.info("MONGODB_URI not set; stats kept in memory only")

_session_lock = threading.Lock()
current_session = None


def _mongo_update(sid, update):
    if sessions_col is None:
        return
    try:
        sessions_col.update_one({"_id": sid}, update)
    except Exception as e:
        log.warning(f"mongo update failed: {e}")


def _load_prior_memory():
    """Build {bot_id: 'what you did/said last session'} from the most recent prior session."""
    out = {}
    if sessions_col is None:
        return out
    try:
        prev = list(sessions_col.find().sort("started_at", -1).limit(1))
        if not prev:
            return out
        doc = prev[0]
        kills = doc.get("stats", {}).get("kills_by_bot", {})
        agg = {}
        for line in doc.get("conversation", []):
            bid = str(line.get("bot_id"))
            agg.setdefault(bid, []).append(line.get("text", ""))
        for bid, said in agg.items():
            recent = " / ".join(s for s in said[-3:] if s)
            out[bid] = f"Last battle you scored {kills.get(bid, 0)} kill(s) and said: {recent}"
        log.info(f"[memory] loaded prior-session recall for {len(out)} bot(s)")
        return out
    except Exception as e:
        log.warning(f"[memory] load failed: {e}")
        return out


def _new_session():
    prior_memory = _load_prior_memory()      
    doc = {
        "_id": uuid.uuid4().hex[:12],
        "started_at": datetime.now(timezone.utc).isoformat(),
        "stats": {"enemies_killed": 0, "total_shots": 0, "shots_by_bot": {}, "kills_by_bot": {}},
        "kills": [],
        "conversation": [],
        "bot_memory": prior_memory,           
    }
    if sessions_col is not None:
        try:
            to_store = dict(doc)
            sessions_col.insert_one(to_store)
        except Exception as e:
            log.warning(f"mongo insert failed: {e}")
    log.info(f"[session] started {doc['_id']}")
    return doc


def _ensure_session():
    global current_session
    with _session_lock:
        if current_session is None:
            current_session = _new_session()
        return current_session


def _stats_summary(st) -> str:
    kb = st.get("kills_by_bot", {})
    sb = st.get("shots_by_bot", {})
    kills = ", ".join(f"Bot {k}: {v}" for k, v in sorted(kb.items())) or "none yet"
    shots = ", ".join(f"Bot {k}: {v}" for k, v in sorted(sb.items())) or "none yet"
    return (f"Enemies destroyed: {st.get('enemies_killed', 0)}. "
            f"Kills by bot - {kills}. Shots fired by bot - {shots}.")


def _norm(s: str) -> str:
    return re.sub(r"[^a-z0-9]", "", (s or "").lower())


def _clean_line(txt: str, prev: str) -> str:
    """Strip name prefixes and any echo of the previous speaker's line."""
    lines = [l.strip() for l in (txt or "").split("\n") if l.strip()]
    if not lines:
        return ""
    prevn = _norm(prev)
    chosen = None
    for l in lines:                         
        if prevn and _norm(l) == prevn:
            continue
        chosen = l
        break
    if chosen is None:
        chosen = lines[-1]
    chosen = re.sub(r"^\s*bot\s*\d+\s*[:\-]\s*", "", chosen, flags=re.I).strip().strip('"').strip()
    if prevn and _norm(chosen).startswith(prevn): 
        parts = re.split(r"(?<=[.!?])\s+", chosen)
        parts = [p for p in parts if _norm(p) != prevn]
        chosen = " ".join(parts).strip() or chosen
    return chosen[:160]


@app.route("/session/start", methods=["POST"])
def session_start():
    global current_session
    with _session_lock:
        current_session = _new_session()
    return jsonify({"session_id": current_session["_id"]}), 200


@app.route("/event", methods=["POST"])
def event():
    data = request.get_json(silent=True) or {}
    sess = _ensure_session()
    etype = data.get("type")
    with _session_lock:
        st = sess["stats"]
        if etype == "shots":
            bid = str(data.get("bot_id"))
            cnt = int(data.get("count", 1))
            st["shots_by_bot"][bid] = st["shots_by_bot"].get(bid, 0) + cnt
            st["total_shots"] += cnt
            _mongo_update(sess["_id"], {"$inc": {f"stats.shots_by_bot.{bid}": cnt, "stats.total_shots": cnt}})
        elif etype == "kill":
            killer = str(data.get("killer_bot_id", -1))
            victim = data.get("victim", "enemy")
            st["enemies_killed"] += 1
            st["kills_by_bot"][killer] = st["kills_by_bot"].get(killer, 0) + 1
            sess["kills"].append({"killer": killer, "victim": victim})
            _mongo_update(sess["_id"], {
                "$inc": {"stats.enemies_killed": 1, f"stats.kills_by_bot.{killer}": 1},
                "$push": {"kills": {"killer": killer, "victim": victim}},
            })
    return jsonify({"ok": True}), 200


def _generate_chat_line(speaker, count, topic, transcript_text, prev, stats_text, persona=None, prior_memory=""):
    persona = persona or {}
    name = persona.get("name") or f"Bot {speaker}"
    seed = (persona.get("memory") or "").strip()
    prior = (prior_memory or "").strip()
    remember = " ".join(x for x in [seed, prior] if x) or "nothing in particular"
    system = (
        f"You are {name}, a combat robot in a squad of {count}. "
        f"Backstory: {persona.get('backstory') or 'classified'}. "
        f"Your goals: {persona.get('goals') or 'win and keep the squad alive'}. "
        f"How you feel about squadmates: {persona.get('relationships') or 'neutral'}. "
        f"What you remember: {remember}. "
        "You just won a battle and regrouped at base. Stay in character with short military banter. "
        "Either ask a squadmate a brief question or answer the previous line; you may reference the battle "
        "stats, your goals, your relationships, or what you remember from last time. "
        "Output ONLY your own new line, max 18 words. Never repeat or restate what a squadmate already said. "
        "No quotes, do not prefix your name."
    )
    prompt = (
        f"Battle stats: {stats_text}\n"
        f"Topic: {topic}\n"
        f"Conversation so far:\n{transcript_text}\n\n"
        f"Write ONLY {name}'s new line (do not echo the previous line):"
    )
    try:
        r = httpx.post(OLLAMA_URL, json={
            "model": OLLAMA_MODEL, "system": system, "prompt": prompt,
            "stream": False, "options": {"temperature": 0.9, "num_predict": 70},
        }, timeout=20.0)
        r.raise_for_status()
        return _clean_line(r.json().get("response", ""), prev) or "..."
    except Exception as e:
        log.warning(f"[chat] generation failed: {e}")
        return "Comms glitchy - good work out there."


@app.route("/chat", methods=["POST"])
def chat():
    data = request.get_json(silent=True) or {}
    sess = _ensure_session()
    speaker = int(data.get("speaker_id", 0))
    count = int(data.get("bot_count", 4))
    topic = data.get("topic", "the battle")
    items = data.get("transcript_wrapper", {}).get("items", [])
    lines = [f"Bot {it.get('bot_id')}: {it.get('text', '')}" for it in items]
    transcript_text = "\n".join(lines) if lines else "(nothing said yet)"
    prev = items[-1].get("text", "") if items else ""
    persona = data.get("persona", {}) or {}
    prior = (sess.get("bot_memory") or {}).get(str(speaker), "")
    text = _generate_chat_line(speaker, count, topic, transcript_text, prev, _stats_summary(sess["stats"]), persona, prior)
    with _session_lock:
        sess["conversation"].append({"bot_id": speaker, "text": text})
    _mongo_update(sess["_id"], {"$push": {"conversation": {"bot_id": speaker, "text": text}}})
    log.info(f"[chat] Bot {speaker}: {text}")
    return jsonify({"bot_id": speaker, "text": text}), 200


if __name__ == "__main__":
    threading.Thread(target=run_async_loop, daemon=True).start()
    log.info(f"AI Bot Server on http://{HOST}:{PORT} | model={OLLAMA_MODEL} | bots={BOT_COUNT}")
    app.run(host=HOST, port=PORT, debug=False, use_reloader=False, threaded=True)