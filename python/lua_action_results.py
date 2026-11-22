"""Bounded model projection of host action receipts; never authorizes execution."""
import json


ACTION_RESULTS_BYTES = 32_768


def _remaining_build_items(result):
    if (result.get('operation'), result.get('status')) != ('build_place', 'failed') or result.get('reason') not in (
            'build_partial', 'blueprint_blocked'):
        return None
    terminal = (result.get('task') or {}).get('result') or {}
    evidence = terminal.get('evidence') or {}
    if evidence.get('outcome') not in ('partial', 'failed') or not isinstance(evidence.get('entries'), list):
        return None
    groups = {}
    for entry in evidence['entries']:
        if entry.get('status') in ('matched', 'placed'):
            continue
        reason = entry.get('reason') or 'unspecified'
        group = groups.setdefault(reason, {'count': 0, 'examples': []})
        group['count'] += 1
        if len(group['examples']) < 3:
            group['examples'].append({k: entry.get(k) for k in ('layer', 'x', 'y', 'itemType', 'status')})
    return groups


def action_results(actions, last_action=None, cleanup=None):
    records = [(result, 'api') for result in actions]
    if last_action is not None and last_action not in actions:
        # Runtime may retain only a bounded projection of the last callback.
        duplicate = (last_action.get('payloadOmitted') is True and any(
            r.get('actionId') is not None and r.get('actionId') == last_action.get('actionId')
            for r, _ in records))
        if not duplicate:
            records.append((last_action, 'terminal'))
    if cleanup is not None and all(cleanup != r for r, _ in records):
        records.append((cleanup, 'cleanup'))

    history = dict(items=[], totalCount=len(records), returnedCount=0, omittedCount=len(records),
                   summaryOnlyCount=0, truncated=False, byteLimit=ACTION_RESULTS_BYTES, statusCounts={})
    for result, _ in records:
        status = result.get('status')
        category = status if status in ('completed', 'failed', 'unknown', 'cancelled', 'timeout') else 'other'
        history['statusCounts'][category] = history['statusCounts'].get(category, 0) + 1

    def measured():
        history['returnedCount'] = len(history['items'])
        history['omittedCount'] = len(records) - len(history['items'])
        history['summaryOnlyCount'] = sum(item['summaryOnly'] for item in history['items'])
        history['truncated'] = bool(history['omittedCount'] or history['summaryOnlyCount'])
        return len(json.dumps(history, ensure_ascii=False, allow_nan=False).encode('utf-8'))

    def summary(result):
        fields = {}
        for key in ('operation', 'actionId', 'valid', 'status', 'completed', 'reason', 'reasonDetail',
                    'endEvidenceStatus', 'blocked', 'dispatchState', 'supervisorError'):
            value = result.get(key)
            if isinstance(value, str):
                fields[key] = value.encode('utf-8')[:512].decode('utf-8', errors='ignore')
            elif value is None or type(value) in (bool, int):
                if key in result:
                    fields[key] = value
        end = result.get('endEvidence')
        if isinstance(end, dict) and type(end.get('inputsReleased')) is bool:
            fields['endEvidence'] = {'inputsReleased': end['inputsReleased']}
        if 'recovery' in result:
            fields['recovery'] = result['recovery']
        if result.get('operation') == 'build_place':
            proof = ((result.get('task') or {}).get('result') or {}).get('evidence') or {}
            for field in ('scheduler', 'timing', 'completedByLayer'):
                if field in proof:
                    fields[field] = proof[field]
        remaining = _remaining_build_items(result)
        if remaining:
            for key in ('worldSessionId', 'actorId', 'actorSlot', 'actorGeneration', 'inputsReleased', 'effectVerified'):
                if key in result:
                    fields[key] = result[key]
            fields['remainingByReason'] = remaining
        return fields

    # Reserve compact evidence for the last failure/cleanup and early progress
    # before spending the remaining space on complete payloads.
    priority = [len(records)-1, *range(len(records)-1)] if records else []
    for index in priority:
        result, phase = records[index]
        item = dict(sequence=index+1, phase=phase, summaryOnly=True, result=summary(result))
        history['items'].append(item)
        history['items'].sort(key=lambda item: item['sequence'])
        if measured() > ACTION_RESULTS_BYTES:
            history['items'].remove(item)
    for index in priority:
        item = next((i for i in history['items'] if i['sequence'] == index+1), None)
        if item is None:
            continue
        compact = item['result']
        if 'remainingByReason' in compact or 'scheduler' in compact:
            continue
        item.update(result=records[index][0], summaryOnly=False)
        try:
            fits = measured() <= ACTION_RESULTS_BYTES
        except (TypeError, ValueError):
            fits = False
        if not fits:
            item.update(result=compact, summaryOnly=True)
    measured()
    # Detach from callback objects, including nested native evidence.
    return json.loads(json.dumps(history, ensure_ascii=False, allow_nan=False))
