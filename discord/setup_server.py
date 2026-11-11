#!/usr/bin/env python3
"""Builds the M3AT Discord server's roles, channels and first messages through
Discord's REST API, as a bot invited to an empty server.

    python3 discord/setup_server.py [--workshop-id N] [--dry-run]

The bot's token is read from `discordBotToken` at the repository's root and the
server's id from `discordServerId` (both gitignored), or from the environment
variables DISCORD_TOKEN and DISCORD_SERVER_ID.

It can be run again safely: a role or channel that already exists by name is left
as it is, and messages are only posted into channels made in that run. Run it
again after turning Community on (Server Settings > Enable Community) and it turns
an ANNOUNCEMENT channel made as a text channel into one (only Community servers
have them; the layout has none at the moment).

Standard library only. No gateway connection: everything is a REST call.
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HERE = os.path.dirname(os.path.abspath(__file__))
API = "https://discord.com/api/v10"

# Channel types.
TEXT, CATEGORY, ANNOUNCEMENT, FORUM = 0, 4, 5, 15

# Permission bits.
VIEW = 1 << 10
SEND = 1 << 11
ADD_REACTIONS = 1 << 6
SEND_IN_THREADS = 1 << 38
CREATE_PUBLIC_THREADS = 1 << 35
KICK = 1 << 1
BAN = 1 << 2
MANAGE_MESSAGES = 1 << 13
MANAGE_NICKNAMES = 1 << 27
MANAGE_THREADS = 1 << 34
MODERATE_MEMBERS = 1 << 40
MUTE_MEMBERS = 1 << 22
MOVE_MEMBERS = 1 << 24

# Roles, top first. Colours as 0xRRGGBB. `perms` are server-wide extras on top of
# what @everyone has.
ROLES = [
    {"name": "Moderator", "color": 0x4FA3E0, "hoist": True, "mentionable": True,
     "perms": KICK | BAN | MANAGE_MESSAGES | MANAGE_NICKNAMES | MANAGE_THREADS
              | MODERATE_MEMBERS | MUTE_MEMBERS | MOVE_MEMBERS},
    {"name": "Tester", "color": 0x6CCB7A, "hoist": True, "mentionable": True, "perms": 0},
    {"name": "Modder", "color": 0xE0A84F, "hoist": False, "mentionable": False, "perms": 0},
]

# Who may do what in a channel. `read_only`: everyone reads, only moderators
# write. `private`: only the named roles see it at all.
LAYOUT = [
    {"category": "INFO", "channels": [
        {"name": "welcome", "type": TEXT, "access": "read_only",
         "topic": "What M3AT is, what it needs, the rules and the known issues.",
         "messages": ["welcome.md", "rules.md", "known_issues.md"]},
        {"name": "dev-log", "type": TEXT, "access": "read_only",
         "topic": "New versions, and work in progress: clips and pictures of what's being made.",
         "messages": ["first_announcement.md"]},
    ]},
    {"category": "SUPPORT", "channels": [
        {"name": "bug-reports", "type": FORUM, "access": "open",
         "guidelines": "bug_guidelines.md",
         "tags": [("Crash or error", "💥"), ("Visual glitch", "👀"), ("Animation", "🏃"),
                  ("Clothes", "👕"), ("Hair", "💇"), ("Mod conflict", "🧩"),
                  ("Performance", "🐢"), ("Fixed", "✅"), ("Can't reproduce", "❓")],
         "moderated_tags": ["Fixed", "Can't reproduce"],
         "pinned_post": ("How to report a bug", "bug_howto.md")},
        {"name": "help", "type": TEXT, "access": "open",
         "topic": "Questions about installing or using M3AT, and whether it works with other mods."},
    ]},
    {"category": "COMMUNITY", "channels": [
        {"name": "general", "type": TEXT, "access": "open",
         "topic": "Chat about anything, and show off your colony and your pawns."},
        {"name": "suggestions", "type": FORUM, "access": "open",
         "guidelines_text": "One idea per post. React to the ideas you'd like most.",
         "tags": [("Animation", "🏃"), ("Clothing", "👕"), ("Genes and races", "🧬"),
                  ("Options", "⚙️"), ("Other", "💡"), ("Planned", "📌"), ("Done", "✅")],
         "moderated_tags": ["Planned", "Done"]},
    ]},
    {"category": "DEV", "channels": [
        {"name": "admin", "type": TEXT, "access": "private", "roles": ["Moderator"],
         "topic": "Moderators only. Discord's community updates come here."},
    ]},
]


def read_secret(file_name, env_name):
    value = os.environ.get(env_name)
    if value:
        return value.strip()
    path = os.path.join(ROOT, file_name)
    if os.path.exists(path):
        with open(path) as f:
            return f.read().strip()
    sys.exit(f"No {env_name}: put it in {path} or the environment variable {env_name}.")


class Discord:
    def __init__(self, token, dry):
        self.token = token
        self.dry = dry
        self.fake = 0

    def call(self, method, path, body=None, writes=True):
        if self.dry and writes:
            self.fake += 1
            print(f"  [dry run] {method} {path}")
            return {"id": f"dry{self.fake}"}
        data = json.dumps(body).encode() if body is not None else None
        while True:
            req = urllib.request.Request(API + path, data=data, method=method, headers={
                "Authorization": f"Bot {self.token}",
                "Content-Type": "application/json",
                "User-Agent": "DiscordBot (https://discord.com, 1.0)",
            })
            try:
                with urllib.request.urlopen(req) as r:
                    text = r.read()
                    return json.loads(text) if text else None
            except urllib.error.HTTPError as e:
                text = e.read().decode(errors="replace")
                if e.code == 429:
                    wait = json.loads(text).get("retry_after", 1.0)
                    time.sleep(wait + 0.1)
                    continue
                raise RuntimeError(f"{method} {path} failed: {e.code} {text}") from None


def message_text(file_name, workshop_id):
    with open(os.path.join(HERE, "messages", file_name)) as f:
        text = f.read().strip()
    if workshop_id:
        return text.replace("WORKSHOP_ID", workshop_id)
    # Not on the Workshop yet: leave the link out.
    lines = text.split("\n")
    kept = []
    skip = False
    for line in lines:
        if line.startswith("**Get the mod**"):
            skip = True
            continue
        if skip:
            if "WORKSHOP_ID" in line:
                continue
            if line.strip() == "":
                skip = False
                continue
        kept.append(line)
    return "\n".join(kept)


def split_message(text, limit=2000):
    """Splits on blank lines so no message is over Discord's limit."""
    parts, current = [], ""
    for para in text.split("\n\n"):
        candidate = (current + "\n\n" + para) if current else para
        if len(candidate) <= limit:
            current = candidate
        else:
            if current:
                parts.append(current)
            current = para
    if current:
        parts.append(current)
    return parts


def overwrites(access, guild_id, role_ids, roles):
    everyone = guild_id
    mod = role_ids["Moderator"]
    if access == "read_only":
        return [
            {"id": everyone, "type": 0, "allow": "0",
             "deny": str(SEND | CREATE_PUBLIC_THREADS | SEND_IN_THREADS)},
            {"id": mod, "type": 0, "allow": str(SEND | SEND_IN_THREADS), "deny": "0"},
        ]
    if access == "private":
        result = [{"id": everyone, "type": 0, "allow": "0", "deny": str(VIEW)}]
        for name in roles:
            result.append({"id": role_ids[name], "type": 0, "allow": str(VIEW | SEND), "deny": "0"})
        return result
    return []


def main():
    args = sys.argv[1:]
    dry = "--dry-run" in args
    workshop_id = None
    if "--workshop-id" in args:
        workshop_id = args[args.index("--workshop-id") + 1]

    token = read_secret("discordBotToken", "DISCORD_TOKEN")
    api = Discord(token, dry)
    try:
        guild_id = read_secret("discordServerId", "DISCORD_SERVER_ID")
    except SystemExit:
        guild_id = ""
    if not guild_id:
        # No id given: the one server the bot is in.
        guilds = api.call("GET", "/users/@me/guilds", writes=False)
        if len(guilds) != 1:
            sys.exit(f"The bot is in {len(guilds)} servers; put the one to set up in discordServerId.")
        guild_id = guilds[0]["id"]

    guild = api.call("GET", f"/guilds/{guild_id}", writes=False)
    community = "COMMUNITY" in guild.get("features", [])
    print(f"Server: {guild['name']} (Community {'on' if community else 'off'})")

    # Roles. New roles go to the bottom, just above @everyone, so they are made
    # bottom first to come out in ROLES' order.
    existing_roles = {r["name"]: r for r in api.call("GET", f"/guilds/{guild_id}/roles", writes=False)}
    role_ids = {}
    for role in reversed(ROLES):
        if role["name"] in existing_roles:
            role_ids[role["name"]] = existing_roles[role["name"]]["id"]
            print(f"Role {role['name']}: already there")
            continue
        made = api.call("POST", f"/guilds/{guild_id}/roles", {
            "name": role["name"], "color": role["color"], "hoist": role["hoist"],
            "mentionable": role["mentionable"], "permissions": str(role["perms"]),
        })
        role_ids[role["name"]] = made["id"]
        print(f"Role {role['name']}: made")

    channels = api.call("GET", f"/guilds/{guild_id}/channels", writes=False)
    by_name = {(c["name"], c["type"] == CATEGORY): c for c in channels}

    made_now = []
    for position, group in enumerate(LAYOUT):
        cat = by_name.get((group["category"].lower(), True)) or by_name.get((group["category"], True))
        if cat:
            print(f"Category {group['category']}: already there")
        else:
            cat = api.call("POST", f"/guilds/{guild_id}/channels",
                           {"name": group["category"], "type": CATEGORY, "position": position})
            print(f"Category {group['category']}: made")

        for spec in group["channels"]:
            have = by_name.get((spec["name"], False))
            kind = spec["type"]
            if kind == ANNOUNCEMENT and not community:
                kind = TEXT
            if have:
                print(f"  #{spec['name']}: already there")
                if spec["type"] == ANNOUNCEMENT and community and have["type"] == TEXT:
                    api.call("PATCH", f"/channels/{have['id']}", {"type": ANNOUNCEMENT})
                    print(f"  #{spec['name']}: now an announcement channel")
                continue

            body = {
                "name": spec["name"], "type": kind, "parent_id": cat["id"],
                "permission_overwrites": overwrites(spec["access"], guild_id, role_ids,
                                                    spec.get("roles", [])),
            }
            if kind == FORUM:
                guidelines = spec.get("guidelines_text")
                if "guidelines" in spec:
                    guidelines = message_text(spec["guidelines"], workshop_id)
                body["topic"] = guidelines
                body["available_tags"] = [
                    {"name": n, "emoji_name": e, "moderated": n in spec.get("moderated_tags", [])}
                    for n, e in spec["tags"]]
                body["default_reaction_emoji"] = {"emoji_name": "👍"}
            elif spec.get("topic"):
                body["topic"] = spec["topic"]

            try:
                made = api.call("POST", f"/guilds/{guild_id}/channels", body)
            except RuntimeError as e:
                if kind != FORUM:
                    raise
                # Forums may need Community on: fall back to a plain channel.
                print(f"  #{spec['name']}: no forum allowed ({e}); making a text channel")
                body["type"] = TEXT
                for k in ("available_tags", "default_reaction_emoji"):
                    body.pop(k, None)
                body["topic"] = (body.get("topic") or "")[:1024]
                made = api.call("POST", f"/guilds/{guild_id}/channels", body)
                kind = TEXT
            print(f"  #{spec['name']}: made")
            made_now.append((spec, made, kind))

    # "#bug-reports" in the texts becomes a clickable link to that channel.
    ids = {name: c["id"] for (name, is_cat), c in by_name.items() if not is_cat}
    ids.update({s["name"]: m["id"] for s, m, k in made_now})

    def linked(text):
        for name in sorted(ids, key=len, reverse=True):
            text = text.replace(f"#{name}", f"<#{ids[name]}>")
        return text

    # Messages, only into channels made in this run, so a second run posts nothing twice.
    for spec, made, kind in made_now:
        for file_name in spec.get("messages", []):
            parts = split_message(linked(message_text(file_name, workshop_id)))
            first = None
            for part in parts:
                msg = api.call("POST", f"/channels/{made['id']}/messages", {
                    "content": part, "allowed_mentions": {"parse": []}})
                first = first or msg
            pin(api, made["id"], first["id"])
            print(f"  #{spec['name']}: posted {file_name}")
        if "pinned_post" in spec:
            title, file_name = spec["pinned_post"]
            text = linked(message_text(file_name, workshop_id))
            if kind == FORUM:
                thread = api.call("POST", f"/channels/{made['id']}/threads", {
                    "name": title, "message": {"content": text, "allowed_mentions": {"parse": []}}})
                api.call("PATCH", f"/channels/{thread['id']}", {"flags": 2})  # pinned
            else:
                msg = api.call("POST", f"/channels/{made['id']}/messages", {"content": text})
                pin(api, made["id"], msg["id"])
            print(f"  #{spec['name']}: posted and pinned \"{title}\"")

    # A link that never runs out, from the welcome channel.
    welcome = next((m for s, m, k in made_now if s["name"] == "welcome"), None) \
        or by_name.get(("welcome", False))
    if welcome:
        invite = api.call("POST", f"/channels/{welcome['id']}/invites",
                          {"max_age": 0, "max_uses": 0, "unique": False})
        print(f"\nInvite link: https://discord.gg/{invite.get('code', '?')}")

    if not community:
        print("\nNext: Server Settings > Enable Community. Pick #welcome for the rules "
              "and #admin for community updates.")


def pin(api, channel_id, message_id):
    try:
        api.call("PUT", f"/channels/{channel_id}/messages/pins/{message_id}")
    except RuntimeError:
        api.call("PUT", f"/channels/{channel_id}/pins/{message_id}")


if __name__ == "__main__":
    main()
