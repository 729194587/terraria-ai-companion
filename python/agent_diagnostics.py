"""Bounded runtime facts, observation summaries and action evidence; no raw prompts."""
from datetime import datetime
import hashlib
from pathlib import Path
import sys
import agent_trace as tracing
import evidence

log = tracing.Trace(enabled=False)


def configure(path=None):
    global log
    path = Path(path) if path else (Path(__file__).resolve().parents[1] / ".local" / "diagnostics"
        / ("agent-runtime-" + datetime.now().strftime("%Y%m%d-%H%M%S-%f") + ".jsonl"))
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("ab"):
            pass
    except OSError as error:
        log = tracing.Trace(enabled=False)
        print(f"Runtime log unavailable: {error}", file=sys.stderr, flush=True)
        return None
    log = tracing.Trace(path, enabled=True)
    return path


def emit(event, **data):
    # Reuse secrets registered by LLMClient, even when full tracing is disabled.
    log.emit(event, **tracing.trace._redact(data))
    evidence.record(event, **data)


def _fields(value, names):
    if not isinstance(value, dict):
        return {}
    return {key: value[key][:256] if isinstance(value[key], str) else value[key]
            for key in names if key in value}


def observation_summary(name, result):
    try:
        return _observation_summary(name,result)
    except Exception as error:
        # A malformed optional diagnostic field must not change game execution.
        return {'summaryUnavailable':type(error).__name__}


def _observation_summary(name, result):
    """Keep decision inputs and coverage, not complete grids/inventory or custom data."""
    summary=_fields(result, ('available','worldSessionId','gameUpdate','controlGeneration','reason','actorGeneration','actorSlot','observedAtUnixMs','clock'))
    data=result.get('data')
    if isinstance(data,dict):
        summary.update(_fields(data, ('status','matchedCount','returnedCount','truncated','unknownCount','complete',
            'scannedCount','bounds','radiusPixels','sort','kind','mode','count','typeId','contentKey','selectedSlot','selectedHotbarKey','scope')))
        for collection in ('items','candidates','inventory','slots'):
            if isinstance(data.get(collection),list):
                items=data[collection]
                summary[collection]=[_fields(item, ('x','y','kind','slot','typeId','contentKey','name','stack','prefix',
                    'maxStack','favorited','empty','inventoryToken','hotbarKey','classification','distancePixels','centerWorldPixels')) for item in items[:8]]
                for out,item in zip(summary[collection],items[:8]):
                    if isinstance(item,dict) and isinstance(item.get('use'),dict):
                        out['use']=_fields(item['use'],('pick','axe','hammer','damage','createTile','createWall','consumable','useTime'))
                    if isinstance(item,dict) and isinstance(item.get('details'),dict):
                        out['details']=_fields(item['details'],('entityRef','type','life','lifeMax','stack','contentKey'))
                summary[collection+'OmittedFromLog']=max(0,len(items)-8)
    if name in ('get_exploration_observation','observe_exploration','get_observation'):
        obs=result.get('exploration',result.get('observation')) or {}
        terrain=obs.get('terrain') or {}
        entities=obs.get('entities') or {}
        summary.update(_fields(obs, ('status','worldSessionId','gameUpdate','observationId','sampledAt','phase','completeness','projectionCoverage')))
        summary['camera']=_fields(obs.get('camera'),('status','sampleGameUpdate','ageGameUpdates','sampledAt'))
        summary['player']=_fields(obs.get('player'),('positionWorldPixels','boundsWorldPixels','velocityPixelsPerTick','life','dead','ghost'))
        summary['terrain']=_fields(terrain,('bounds','unknownCount','uninterpretedCount','underfoot'))
        summary['entitiesCoverage']=entities.get('coverage')
        summary['entities']=[_fields(e,('kind','slot','typeId','classification','boundsWorldPixels','life','lifeMax'))
                             for e in entities.get('items',[])[:8]]
        summary['entitiesOmittedFromLog']=max(0,len(entities.get('items',[]))-8)
    elif name in ('get_player_state','get_player_status','observe_terrain'):
        player=(data.get('state') if name=='observe_terrain' and isinstance(data,dict) else None) or result.get('player',result.get('state',data)) or {}
        summary['player']=_fields(player,('position','positionWorldPixels','boundsWorldPixels','velocity','velocityPixelsPerTick',
            'life','lifeMax','mana','manaMax','dead','ghost','selectedSlot','selectedHotbarKey','movement','inventorySummary'))
        if isinstance(player.get('selectedItem'),dict):
            item=player['selectedItem']
            summary['player']['selectedItem']=_fields(item,('slot','typeId','contentKey','name','stack','prefix','hotbarKey'))
            summary['player']['selectedItem']['use']=_fields(item.get('use'),('pick','axe','hammer','damage','createTile','createWall','consumable'))
        summary['motion']=result.get('motion') or _fields(result.get('terrain'),('grounded','supported','playerWidth','playerHeight'))
        if name=='observe_terrain' and isinstance(data,dict):
            summary['motion']=_fields(data.get('terrain'),('grounded','supported','playerWidth','playerHeight','originX','originY'))
    elif name=='get_world_state' and isinstance(data,dict):
        summary.update(_fields(data,('environment','depth','depthLayer','progression')))
    return summary


def exploration_report(value):
    """Bounded Lua self-report for diagnostics; never upgrades task/action evidence."""
    if not isinstance(value,dict) or value.get('kind')!='exploration_report':
        return
    summary=_fields(value,('kind','status','reason','distancePixels','reversals','noNovelSteps','start','finish','visited','identity','lastTileCoverage','lastThreatCheck','actionStatus','actionReason'))
    for collection,limit,fields in (
        ('steps',8,('step','direction','targetX','fromX','toX','movedPixels','newHorizontalPixels','gameUpdate')),
        ('decisions',8,('step','direction','targetX','basis')),
        ('landmarks',32,('x','y','typeId','contentKey','gameUpdate','observedAtUnixMs'))):
        items=value.get(collection)
        if isinstance(items,list):
            summary[collection]=[_fields(item,fields) for item in items[:limit]]
    emit('lua_exploration_report', authority='script_self_report; correlate lua_api_result/endEvidence', report=summary)


def tool_result(name, arguments, result, *, recovery=False):
    keys = ("valid", "status", "reason", "error", "operation", "actionId", "session",
            "actionGeneration", "controlGeneration", "endEvidenceStatus", "endEvidence",
            "evidence", "recovery", "blocked", "previousFailure")
    summary = {key: result[key] for key in keys if key in result}
    if name in ('scan_tiles','scan_entities','resolve_content','inspect_inventory','observe_exploration',
                'get_exploration_observation','get_observation','get_player_state','get_player_status','get_world_state','observe_terrain'):
        summary['observation']=observation_summary(name,result)
    if name == 'execute_lua':
        source = arguments.get('source') if isinstance(arguments, dict) else None
        arguments = {'sourceSha256': hashlib.sha256(source.encode()).hexdigest()
                     if isinstance(source, str) else None}
        for key in ('runId', 'calls', 'programCompleted', 'actionSuccess', 'actionState',
                    'taskOutcome', 'lastAction', 'cleanup', 'sourceSha256','actions','failure','errorLine','evidence'):
            if key in result:
                summary[key] = result[key]
    if name == "inspect_tile":
        for key in ("data", "worldSessionId", "gameUpdate"):
            if key in result:
                summary[key] = result[key]
    if name == "get_exploration_observation":
        exploration = result.get("exploration") or {}
        terrain = exploration.get("terrain") or {}
        summary["footingObservation"] = {"worldSessionId": exploration.get("worldSessionId"),
            "gameUpdate": exploration.get("gameUpdate"), "player": exploration.get("player"),
            "underfoot": terrain.get("underfoot")}
    if recovery:
        for key in ("state", "terrain", "data", "worldSessionId", "gameUpdate"):
            if key in result:
                summary[key] = result[key]
    emit("mining_recovery_step" if recovery else "tool_result", name=name,
         arguments=arguments, result=summary)
