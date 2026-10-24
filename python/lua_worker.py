"""Isolated Lua 5.4 worker, adapted from spike/lua-orchestrator-prototype.

No Bridge or game handles enter this process. Only bounded JSON crosses the pipe.
"""
import json
import math
import re

SOURCE_BYTES = 16_384
REQUEST_BYTES = 16_384
VALUE_BYTES = 262_144
FRAME_BYTES = VALUE_BYTES + 1024
MAX_NODES = 20_000
MAX_DEPTH = 24


class ValueLimit(ValueError):
    pass


def encode(value, maximum=FRAME_BYTES):
    """Validate before serializing; reject rather than truncate evidence or arrays."""
    nodes = 0
    def visit(v, depth):
        nonlocal nodes
        nodes += 1
        if nodes > MAX_NODES or depth > MAX_DEPTH:
            raise ValueLimit('value_limit')
        if v is None or type(v) is bool:
            return
        if type(v) is int and -2**63 <= v <= 2**63 - 1:
            return
        if type(v) is float and math.isfinite(v):
            return
        if type(v) is str and len(v.encode('utf-8')) <= VALUE_BYTES:
            return
        if type(v) is dict:
            for key, item in v.items():
                if type(key) is not str or len(key.encode('utf-8')) > 256:
                    raise ValueLimit('value_limit')
                visit(item, depth + 1)
            return
        if type(v) is list:
            for item in v:
                visit(item, depth + 1)
            return
        raise ValueLimit('value_limit')
    visit(value, 0)
    raw = json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(',', ':')).encode('utf-8')
    if len(raw) > maximum:
        raise ValueLimit('value_limit')
    return raw


def decode(raw, maximum=FRAME_BYTES):
    if len(raw) > maximum:
        raise ValueLimit('value_limit')
    value = json.loads(raw)
    encode(value, maximum)
    return value


BOOTSTRAP = '''
    debug.setmetatable("", nil)
    local tags = setmetatable({}, {__mode="k"})
    local null = {}; tags[null] = "null"
    local current_line = 0
    local function mark(t, kind) tags[t] = kind; return t end
    local function kind(t) return tags[t] end
    local function run(source, callback, progress, budget)
        local env = {type=type, tonumber=tonumber, tostring=tostring,
                     pairs=pairs, ipairs=ipairs, next=next, assert=assert, error=error}
        env.json = {null=null, array=function(t) return mark(t or {}, "array") end,
                              object=function(t) return mark(t or {}, "object") end}
        env.api = function(name, args) return callback(name, args or {}, current_line) end
        env.log = function(message) return progress(message, current_line) end
        local f, err = load(source, "companion.lua", "t", env)
        if not f then error(err, 0) end
        local used = 0
        debug.sethook(function(event)
            if event == "line" then
                local info = debug.getinfo(2, "Sl")
                if info.source == "companion.lua" then current_line = info.currentline end
            else
                used = used + 100
                if used >= budget then error("instruction_limit", 0) end
            end
        end, "l", 100)
        return f()
    end
    return run, mark, kind, null, function() return current_line end
'''


def worker(connection, source, limits):
    from lupa import lua54 as module
    def deny(*args):
        raise AttributeError('Python access denied')
    lua = module.LuaRuntime(register_eval=False, register_builtins=False,
                           attribute_handlers=(deny, deny), max_memory=limits.memory)
    run, mark, kind, null, last_line = lua.execute(BOOTSTRAP)

    def to_lua(value):
        if value is None:
            return null
        if type(value) in (dict, list):
            table = mark(lua.table(), 'object' if type(value) is dict else 'array')
            for key, item in (value.items() if type(value) is dict else enumerate(value, 1)):
                table[key] = to_lua(item)
            return table
        return value

    def plain(value, maximum):
        nodes = 0
        def visit(v, depth):
            nonlocal nodes
            nodes += 1
            if nodes > MAX_NODES or depth > MAX_DEPTH:
                raise ValueLimit('value_limit')
            if module.lua_type(v) != 'table':
                if v is None or type(v) in (bool, int, float, str):
                    return v
                raise ValueLimit('value_limit')
            tag = kind(v)
            if tag == 'null':
                return None
            objects, arrays = {}, {}
            for key, item in v.items():
                if type(key) is str and len(key.encode('utf-8')) <= 256:
                    objects[key] = visit(item, depth + 1)
                elif type(key) is int and 1 <= key <= MAX_NODES:
                    arrays[key] = visit(item, depth + 1)
                else:
                    raise ValueLimit('value_limit')
            if objects and arrays or tag == 'array' and objects or tag == 'object' and arrays:
                raise ValueLimit('value_limit')
            if arrays or tag == 'array':
                if len(arrays) != max(arrays, default=0):
                    raise ValueLimit('value_limit')
                return [arrays[i] for i in range(1, len(arrays) + 1)]
            return objects
        out = visit(value, 0)
        encode(out, maximum)
        return out

    sequence = 0
    def call(name, arguments, line):
        nonlocal sequence
        if type(name) is not str or len(name) > 64:
            raise ValueLimit('bad_argument')
        sequence += 1
        connection.send_bytes(encode({'type': 'call', 'id': sequence, 'name': name,
                                      'line': line, 'arguments': plain(arguments, REQUEST_BYTES)}, REQUEST_BYTES))
        reply = decode(connection.recv_bytes(FRAME_BYTES))
        if reply.get('type') != 'result' or reply.get('id') != sequence:
            raise ValueLimit('protocol_error')
        return to_lua(reply['value'])

    logs = 0
    def progress(message, line):
        nonlocal logs
        if type(message) is not str or len(message.encode('utf-8')) > 512:
            raise ValueLimit('bad_argument')
        logs += 1
        if logs > 32:
            raise ValueLimit('log_limit')
        connection.send_bytes(encode({'type': 'log', 'id': logs, 'line': line, 'message': message}, REQUEST_BYTES))

    try:
        value = plain(run(source, call, progress, limits.instructions), VALUE_BYTES)
        out = {'type': 'done', 'status': 'completed', 'value': value}
    except ValueLimit as exc:
        out = {'type': 'done', 'status': str(exc)}
    except module.LuaMemoryError:
        out = {'type': 'done', 'status': 'memory_limit'}
    except module.LuaError as exc:
        detail = str(exc)[:512]
        out = {'type': 'done', 'status': 'instruction_limit' if 'instruction_limit' in detail else 'script_error',
               'error': detail}
        found = re.search(r'companion\.lua(?:"\])?:(\d+)', detail)
        if found:
            out['line'] = int(found[1])
    except Exception as exc:
        out = {'type': 'done', 'status': 'worker_error', 'error': type(exc).__name__ + ': ' + str(exc)[:256]}
    if out['status'] != 'completed' and 'line' not in out:
        try:
            out['line'] = int(last_line())
        except Exception:
            out['line'] = 0
    try:
        connection.send_bytes(encode(out))
    finally:
        connection.close()
