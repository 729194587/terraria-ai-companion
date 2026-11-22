"""Isolated Lua 5.4 worker, adapted from spike/lua-orchestrator-prototype.

No Bridge or game handles enter this process. Only bounded JSON crosses the pipe.
"""
import json
import math
import re

import lua_modules
from pathlib import Path

SOURCE_BYTES = 16_384
REQUEST_BYTES = 16_384
VALUE_BYTES = 262_144
FRAME_BYTES = VALUE_BYTES + 1024
MAX_NODES = 20_000
MAX_DEPTH = 24
EXPLORATION_SOURCE = (Path(__file__).parent / "lua" / "explore.lua").read_text(encoding="utf-8")


SEMANTIC_SOURCE = (Path(__file__).parent / "lua" / "semantic.lua").read_text(encoding="utf-8")


class ValueLimit(ValueError):
    pass


class SerializationError(ValueError):
    """Unsupported Lua structure, distinct from exhausted JSON resource bounds."""
    pass


class ForcedTermination(Exception):
    """Private host termination; never turned into a recoverable Lua error."""
    pass


class LocalArgumentRejected(Exception):
    def __init__(self, receipt):
        self.receipt = receipt


TABLE_ENCODING_KEY = '$luaTable'


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
    local function run(source, callback, progress, protected, terminate, budget, exploration_source, semantic_source, modules, module_done)
        local env = {type=type, tonumber=tonumber, tostring=tostring,
                     pairs=pairs, ipairs=ipairs, next=next, assert=assert, error=error}
        -- Explicit pure Lua allowlist; no random state, loaders or host objects.
        env.math = {abs=math.abs, min=math.min, max=math.max, sqrt=math.sqrt,
                    ceil=math.ceil, floor=math.floor, tointeger=math.tointeger,
                    huge=math.huge, pi=math.pi}
        env.table = {insert=table.insert, remove=table.remove, sort=table.sort,
                     concat=table.concat, pack=table.pack, unpack=table.unpack}
        env.string = {sub=string.sub, len=string.len}
        env.json = {null=null, array=function(t) return mark(t or {}, "array") end,
                              object=function(t) return mark(t or {}, "object") end}
        env.api = function(name, args) return callback(name, args or {}, current_line) end
        env.log = function(message) return progress(message, current_line) end
        env.pcall = function(f, ...) return protected(f, ...) end
        local helper = assert(load(exploration_source, "exploration.lua", "t", env))
        env.explore = helper(string.sub)
        env.sense_world = assert(load(semantic_source, "semantic.lua", "t", env))()
        env.terraria = {}
        for _, m in ipairs(modules or {}) do
            local private = {api=function(name,args) return callback(name, args or {}, current_line, m.name) end}
            for _, k in ipairs({"type","assert","ipairs","pairs","math","json"}) do
                if type(env[k]) == "table" then
                    private[k] = {}
                    for key,v in pairs(env[k]) do private[k][key] = v end
                else private[k] = env[k] end
            end
            local exports = assert(load(m.source, "terraria."..m.name, "t", private))()
            assert(type(exports) == "table" and type(exports[m.export]) == "function", "invalid module exports")
            for k in pairs(exports) do assert(k == m.export, "unregistered module export") end
            env.terraria[m.name] = {[m.export]=function(...)
                local report = exports[m.export](...)
                module_done(report)
                return report
            end}
        end
        local f, err = load(source, "companion.lua", "t", env)
        if not f then error(err, 0) end
        local used = 0
        debug.sethook(function(event)
            if event == "line" then
                local info = debug.getinfo(2, "Sl")
                if info.source == "companion.lua" then current_line = info.currentline end
            else
                used = used + 100
                if used >= budget then terminate("instruction_limit") end
            end
        end, "l", 100)
        return f()
    end
    return run, mark, kind, null, function() return current_line end
'''


def worker(connection, source, limits, helper_source=None, module_names=()):
    from lupa import lua54 as module
    def deny(*args):
        raise AttributeError('Python access denied')
    lua = module.LuaRuntime(register_eval=False, register_builtins=False,
                           attribute_handlers=(deny, deny), max_memory=limits.memory,
                           unpack_returned_tuples=True)
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

    same_table = lua.eval('rawequal')  # Private identity check; not a sandbox permission.
    invoke = lua.eval('function(f, ...) return table.pack(f(...)) end')
    fatal_error, protected_depth = None, 0

    def check_fatal():
        if fatal_error is not None:
            raise fatal_error

    def terminate(status):
        nonlocal fatal_error
        fatal_error = ForcedTermination(status)
        raise fatal_error

    def guarded(callback):
        def guarded_call(*args):
            nonlocal fatal_error
            check_fatal()
            try:
                return callback(*args)
            except LocalArgumentRejected:
                raise
            except BaseException as exc:
                fatal_error = exc
                raise
        return guarded_call

    def protected(function, *args):
        nonlocal protected_depth
        check_fatal()
        protected_depth += 1
        try:
            try:
                values = invoke(function, *args)
            except LocalArgumentRejected as exc:
                check_fatal()
                return False, to_lua(exc.receipt)
            except module.LuaMemoryError:
                terminate('memory_limit')
            except module.LuaError as exc:
                check_fatal()
                return False, str(exc)[:512]
            check_fatal()
            count = values['n']
            if count > MAX_NODES:
                terminate('value_limit')
            return (True, *(values[i] for i in range(1, count+1)))
        finally:
            protected_depth -= 1

    def plain(value, maximum, *, return_value=False):
        nodes = 0
        active = []
        def visit(v, depth, path):
            nonlocal nodes
            nodes += 1
            if nodes > MAX_NODES or depth > MAX_DEPTH:
                raise ValueLimit('value_limit')
            lua_type = module.lua_type(v)
            if lua_type != 'table':
                if type(v) is float and not math.isfinite(v):
                    raise SerializationError(path+': non-finite number is not JSON')
                if v is None or type(v) in (bool, int, float, str):
                    return v
                raise SerializationError(path+': unsupported value type '+(lua_type or type(v).__name__))
            tag = kind(v)
            if tag == 'null':
                return None
            if any(same_table(v, parent) for parent in active):
                raise SerializationError(path+': cyclic table reference')
            active.append(v)
            try:
                objects, arrays = {}, {}
                for key, item in v.items():
                    if type(key) is str:
                        if len(key.encode('utf-8')) > 256:
                            raise ValueLimit('value_limit')
                        objects[key] = visit(item, depth+1, (path+'['+repr(key[:48])+']')[:200])
                    elif type(key) is int and key >= 1:
                        arrays[key] = visit(item, depth+1, (path+'['+str(key)+']')[:200])
                    else:
                        raise SerializationError(path+': unsupported table key; use strings or contiguous positive integer keys starting at 1')
                if arrays and len(arrays) != max(arrays):
                    raise SerializationError(path+': sparse integer keys; use contiguous 1..n and json.null for explicit nulls')
                if tag == 'array' and objects or tag == 'object' and arrays:
                    raise SerializationError(path+': json.array/json.object tag conflicts with table keys')
                if objects and arrays:
                    if not return_value:
                        raise SerializationError(path+': mixed API argument is not plain JSON; API parameters require pure objects/arrays')
                    return {TABLE_ENCODING_KEY: {'version': 1, 'kind': 'mixed',
                            'array': [arrays[i] for i in range(1,len(arrays)+1)], 'fields': objects}}
                if arrays or tag == 'array':
                    return [arrays[i] for i in range(1,len(arrays)+1)]
                if return_value and TABLE_ENCODING_KEY in objects:
                    # Escape literal objects containing the reserved key. No user
                    # object can impersonate a codec envelope, even when nested.
                    return {TABLE_ENCODING_KEY: {'version': 1, 'kind': 'object', 'fields': objects}}
                return objects
            finally:
                active.pop()
        out = visit(value, 0, 'return' if return_value else 'arguments')
        encode(out, maximum)
        return out

    sequence = 0
    terminal_module = False
    terminal_report = None

    def module_done(value):
        nonlocal terminal_report
        if terminal_module:
            terminal_report = plain(value, VALUE_BYTES, return_value=True)
            terminate('module_terminal')

    def call(name, arguments, line, module_name=None):
        nonlocal sequence, terminal_module
        if type(name) is not str or len(name) > 64:
            raise ValueLimit('bad_argument')
        sequence += 1
        connection.send_bytes(encode({'type': 'call', 'id': sequence, 'name': name,
                                      'line': line, 'protected': protected_depth > 0, 'module': module_name,
                                      'arguments': plain(arguments, REQUEST_BYTES)}, REQUEST_BYTES))
        reply = decode(connection.recv_bytes(FRAME_BYTES))
        if reply.get('id') != sequence:
            raise ValueLimit('protocol_error')
        if reply.get('type') == 'local_argument_rejected':
            if protected_depth <= 0 or type(reply.get('value')) is not dict:
                raise ValueLimit('protocol_error')
            raise LocalArgumentRejected(reply['value'])
        if reply.get('type') == 'module_terminal' and module_name is not None:
            terminal_module = True
        elif reply.get('type') != 'result':
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
        returned = run(source, guarded(call), guarded(progress), protected, terminate, limits.instructions,
                       EXPLORATION_SOURCE if helper_source is None else helper_source, SEMANTIC_SOURCE,
                       to_lua(lua_modules.sources(module_names)), module_done)
        check_fatal()
        value = plain(returned, VALUE_BYTES, return_value=True)
        out = {'type': 'done', 'status': 'completed', 'value': value}
    except ForcedTermination as exc:
        out = {'type': 'done', 'status': str(exc)}
        if terminal_module and terminal_report is not None:
            out['moduleResult'] = terminal_report
    except SerializationError as exc:
        out = {'type': 'done', 'status': 'serialization_error', 'error': str(exc)[:512]}
    except UnicodeError:
        out = {'type': 'done', 'status': 'serialization_error', 'error': 'invalid UTF-8 string cannot be represented as JSON'}
    except ValueLimit as exc:
        out = {'type': 'done', 'status': str(exc)}
        if terminal_module and terminal_report is not None:
            out['moduleResult'] = terminal_report
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
