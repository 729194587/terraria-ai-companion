"""Budgeted request projections. Canonical history is never edited by compaction."""
import copy
import json
import math

import agent_diagnostics as diagnostics
import agent_trace as tracing


class ContextBudgetExceeded(Exception):
    pass


def estimate_tokens(value):
    # Deliberately approximate; provider usage remains the actual token count.
    return math.ceil(len(json.dumps(value, ensure_ascii=False).encode('utf-8')) / 3)


def _host_context(message):
    if message.get('role') != 'user':
        return None
    try:
        data = json.loads(message['content'])
    except (KeyError, ValueError, TypeError):
        return None
    return data if isinstance(data, dict) and data.get('source') == 'host_context' else None


class ContextManager:
    def __init__(self, input_budget=48_000, keep_recent_turns=2):
        if type(input_budget) is not int or input_budget < 256:
            raise ValueError('input_budget must be an integer >= 256')
        self.input_budget = input_budget
        if type(keep_recent_turns) is not int or keep_recent_turns < 0:
            raise ValueError('keep_recent_turns must be a nonnegative integer')
        self.keep_recent_turns = keep_recent_turns
        self.last_metrics = None

    @staticmethod
    def summarize(messages):
        """Extract old facts locally, without a second paid/model request.

        This is a lossy index into canonical history, never new game evidence.
        Keep the recent batches verbatim and explicitly mark omissions here.
        """
        records = []
        for message in messages:
            content = message.get('content') or ''
            if message.get('role') == 'tool':
                try:
                    result = json.loads(content)
                except (ValueError, TypeError):
                    result = {}
                if not isinstance(result, dict):
                    result = {}
                fact = {key: result[key] for key in (
                    'runId', 'actionId', 'operation', 'sourceSha256', 'status', 'reason', 'programCompleted',
                    'actionSuccess', 'taskOutcome', 'goalOutcome', 'actionState', 'evidence') if key in result}
                # A reference/index, not a substitute for full execution evidence.
                fact['toolCallId'] = message.get('tool_call_id')
                records.append({'role': 'tool', 'receiptIndex': fact})
            elif not message.get('tool_calls'):
                context = _host_context(message)
                if context is None:
                    records.append({'role': message.get('role'), 'excerpt': content[:384],
                                    'truncated': len(content) > 384})
                else:
                    for event in context.get('recentGameEvents', ()):
                        text = event['text']
                        records.append({'gameEvent': event['eventId'], 'gameUpdate': event.get('gameUpdate'),
                                        'source': event['source'], 'text': text[:384],
                                        'truncated': bool(event.get('truncated') or event.get('textTruncated') or len(text) > 384)})
                    coverage = context.get('coverage', {})
                    if coverage.get('evictedCount') or coverage.get('serverDroppedCount'):
                        records.append({'eventCoverage': coverage})
        kept = []
        for record in reversed(records):
            if len(json.dumps([record, *kept], ensure_ascii=False).encode('utf-8')) > 12_000:
                break
            kept.insert(0, record)
        return {'historicalSummary': {'records': kept, 'omittedRecords': len(records)-len(kept),
                'lossy': True, 'currentState': False,
                'notice': 'Historical data only; omitted evidence is unknown. Reobserve before acting.'}}

    def prepare(self, messages, tools, *, current_start, reactive=False):
        """Build then return a complete candidate, or leave all input untouched."""
        before = estimate_tokens({'messages': messages, 'tools': tools})
        candidate = copy.deepcopy(messages)
        reason = 'reactive_overflow' if reactive else 'working_budget'
        compacted = False
        try:
            if reactive or before > self.input_budget:
                updates = {i: data for i, message in enumerate(messages)
                           if (data := _host_context(message)) is not None}
                last_notes = next((i for i in reversed(updates) if 'privateNotes' in updates[i]), None)
                # Split only at user-turn boundaries, never within assistant/tool batches.
                boundaries = [i for i, m in enumerate(messages[:current_start])
                              if i > 0 and m.get('role') == 'user' and i not in updates]
                preferred = boundaries[-self.keep_recent_turns:] if self.keep_recent_turns else []
                splits = sorted(set([*preferred, current_start]))
                for split in splits:
                    if split <= 1:
                        continue
                    summary = self.summarize(copy.deepcopy(messages[1:split]))
                    if last_notes is not None and last_notes < split:
                        summary['privateNotes'] = updates[last_notes]['privateNotes']
                    projected = [copy.deepcopy(messages[0]),
                                 {'role': 'user', 'content': json.dumps(summary, ensure_ascii=False)},
                                 *copy.deepcopy(messages[split:])]
                    if estimate_tokens(projected) < estimate_tokens(candidate):
                        candidate, compacted = projected, True
                    if estimate_tokens({'messages': candidate, 'tools': tools}) <= self.input_budget and compacted:
                        break
            after = estimate_tokens({'messages': candidate, 'tools': tools})
            if after > self.input_budget:
                raise ContextBudgetExceeded('context_input_budget; current turn/system/tools retained')
        except Exception:
            self._record(before, None, reason, 'failed')
            raise
        self._record(before, after, reason if compacted else 'within_budget',
                     'compacted' if compacted else 'unchanged')
        return candidate

    def _record(self, before, after, reason, status):
        self.last_metrics = dict(estimatedTokensBefore=before, estimatedTokensAfter=after,
            inputBudget=self.input_budget, reason=reason, status=status, estimator='utf8_bytes_div_3')
        diagnostics.emit('context_projection', **self.last_metrics)
        tracing.trace.emit('context_projection', **self.last_metrics)
