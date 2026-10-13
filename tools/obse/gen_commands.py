"""TamrielMerge: OBSE's script commands as a table, from xOBSE's source (https://github.com/llde/xOBSE).

Writes src/TamrielMerge.Cli/Oblivion/Data/obse_commands.json: every command OBSE adds to Oblivion's scripting, with
its opcode (OBSE registers commands in order from 0x1400 in CommandTable::Init), name and alternate name,
description, whether it is called on a reference, its parameters (name, type, optional) and return type, and whether
it takes OBSE expressions (its parse function is one of OBSE's expression parsers rather than the game's).

Definitions come in several forms: DEFINE_COMMAND / DEFINE_COMMAND_DEPRECATED / DEFINE_COMMAND_CONDITIONAL(name,
desc, ref, numParams, params), DEFINE_CMD_COND / DEFINE_CMD(name, desc, ref, params), DEFINE_CMD_ALT(name, alt,
desc, ref, params), and literal "CommandInfo kCommandInfo_X = { "name", "alt", 0, "desc", ref, numParams, params,
execute, parse, eval, flags };". Parameter lists are "ParamInfo kParams_X[n] = { { "name", type, optional }, ... };".

Usage: python gen_commands.py <xOBSE checkout> [out.json]
"""
import json, os, re, sys

src = os.path.join(sys.argv[1], "obse", "obse")
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "..", "..", "src", "TamrielMerge.Cli", "Oblivion", "Data", "obse_commands.json")

files = {}
for name in os.listdir(src):
    if name.endswith((".cpp", ".h")):
        with open(os.path.join(src, name), encoding="latin1") as f:
            # Drop comments so they don't confuse the field splitting.
            text = re.sub(r"/\*.*?\*/", "", f.read(), flags=re.S)
            text = re.sub(r"//[^\n]*", "", text)
            files[name] = text
everything = "\n".join(files.values())


def fields(body):
    """Top-level comma-separated fields, strings kept whole."""
    out, depth, cur, i = [], 0, "", 0
    while i < len(body):
        c = body[i]
        if c == '"':
            j = i + 1
            while j < len(body) and body[j] != '"':
                j += 2 if body[j] == "\\" else 1
            cur += body[i:j + 1]
            i = j + 1
            continue
        if c in "({[":
            depth += 1
        elif c in ")}]":
            depth -= 1
        if c == "," and depth == 0:
            out.append(cur.strip())
            cur = ""
        else:
            cur += c
        i += 1
    if cur.strip():
        out.append(cur.strip())
    return out


def unquote(s):
    s = s.strip()
    return s[1:-1] if s.startswith('"') and s.endswith('"') else s


# ---- parameter lists ----
params = {}
for m in re.finditer(r"ParamInfo\s+(\w+)\s*\[\s*\w*\s*\]\s*=\s*\{(.*?)\}\s*;", everything, re.S):
    entries = []
    for e in re.finditer(r"\{\s*\"([^\"]*)\"\s*,\s*(\w+)\s*,\s*(\w+)\s*\}", m.group(2)):
        entries.append({"name": e.group(1), "type": e.group(2), "optional": e.group(3) not in ("0", "false")})
    params[m.group(1)] = entries

# ---- command definitions ----
commands = {}


def add(key, name, alt, desc, ref, plist, parse=""):
    p = params.get(plist, []) if plist not in ("NULL", "0", "") else []
    commands[key] = {
        "name": unquote(name), "alt": unquote(alt), "description": unquote(desc).replace("\\\"", "\""),
        "reference": str(ref).strip() not in ("0", "false"), "params": p,
        "expression": "Expression" in parse or parse.strip() in ("Cmd_Let_Parse", "Cmd_Call_Parse", "Cmd_BeginLoop_Parse"),
    }


for m in re.finditer(r"DEFINE_(COMMAND|COMMAND_DEPRECATED|COMMAND_CONDITIONAL|CMD_COND|CMD|CMD_ALT|CMD_ALT_COND|COMMAND_ALT)\s*\((.*?)\)\s*;?\s*\n", everything, re.S):
    kind, f = m.group(1), fields(m.group(2))
    try:
        if kind in ("COMMAND", "COMMAND_DEPRECATED", "COMMAND_CONDITIONAL"):
            add(f[0], f[0], "", f[1], f[2], f[4])
        elif kind in ("CMD_COND", "CMD"):
            add(f[0], f[0], "", f[1], f[2], f[3])
        elif kind in ("CMD_ALT", "CMD_ALT_COND", "COMMAND_ALT"):
            add(f[0], f[0], f[1], f[2], f[3], f[4])
    except IndexError:
        pass

for m in re.finditer(r"CommandInfo\s+\(?kCommandInfo_(\w+)\)?\s*=\s*\{(.*?)\}\s*;", everything, re.S):
    f = fields(m.group(2))
    if len(f) >= 9 and f[0].startswith('"'):
        add(m.group(1), f[0], f[1], f[3], f[4], f[6], f[8])

# Flag functions made by macros: DEFINE_PKG_FLAG_FUNC(x) -> GetPackageX/SetPackageX, DEFINE_CS_FLAG_FUNC(x) -> GetCombatStyleX/SetCombatStyleX.
for macro, prefix in (("DEFINE_PKG_FLAG_FUNC", "Package"), ("DEFINE_CS_FLAG_FUNC", "CombatStyle")):
    for m in re.finditer(macro + r"\s*\(\s*(\w+)\s*\)", everything):
        for verb in ("Get", "Set"):
            key = f"{verb}{prefix}{m.group(1)}"
            commands.setdefault(key, {"name": key, "alt": "", "description": "", "reference": False, "params": [], "expression": False})

# ---- registration order: opcodes from 0x1400 ----
init = files["CommandTable.cpp"]
start = init.index("PadTo(kObseOpCodeStart)")
body = init[start:init.index("\n}", start)]
order = []   # (key, return type)
for m in re.finditer(r"g_scriptCommands\.Add\(\s*&\s*(\w+)\s*(?:,\s*(\w+))?|ADD_CMD(_RET)?\s*\(\s*(\w+)\s*(?:,\s*(\w+))?|ImportConsoleCommand\s*\(\s*\"(\w+)\"", body):
    if m.group(1):
        key, ret = m.group(1), m.group(2)
    elif m.group(4):
        key, ret = "kCommandInfo_" + m.group(4), m.group(5)
    else:
        key, ret = "console:" + m.group(6), None
    key = key[len("kCommandInfo_"):] if key.startswith("kCommandInfo_") else key
    order.append((key, (ret or "kRetnType_Default").replace("kRetnType_", "")))

table = []
for i, (key, ret) in enumerate(order):
    if key.startswith("console:"):
        info = {"name": "con_" + key[8:], "alt": "", "description": "console command " + key[8:] + ", made scriptable by OBSE", "reference": False, "params": [], "expression": False}
    else:
        info = commands.get(key) or {"name": key, "alt": "", "description": "", "reference": False, "params": [], "expression": False}
    table.append({"opcode": 0x1400 + i, **info, "returns": ret})
os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
with open(out, "w", encoding="utf-8") as f:
    json.dump(table, f, indent=0)
print(f"{len(table)} OBSE commands ({sum(1 for t in table if t['expression'])} take expressions), "
      f"{len(params)} parameter lists, {len(commands)} definitions found, {sum(1 for k, _ in order if k not in commands and not k.startswith("console:"))} registered without a definition")
