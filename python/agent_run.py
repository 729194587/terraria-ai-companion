"""Host-owned run and result semantics, independent of Lua return values."""
from dataclasses import dataclass, field
import re
import time
import threading

from lua_action_results import action_results


@dataclass
class AgentRun:
    generation: int
    chat_id: str
    phase: str = 'starting'
    outcome: str = 'running'
    cancel_reason: str | None = None
    started: float = field(default_factory=time.monotonic)
    goal_outcome: str = 'unknown'
    reason: str | None = None
    cancel: threading.Event = field(default_factory=threading.Event)
    metrics: dict = field(default_factory=dict)
    budgets: dict = field(default_factory=dict)

    @property
    def id(self):
        return f'{self.generation}:{self.chat_id}'


def task_outcome(actions, program_completed=True, action_state=None):
    """Only native composite-task contracts can establish task completion.

    Even a verified mine_target concerns one tile, not the entire player goal.
    """
    tasks = [a for a in actions if a.get('operation') == 'mine_target']
    from atomic_operations import verified_server_mine_task
    def verified(a):
        end = a.get('endEvidence') or {}
        raw = {**a, 'generation': a.get('actionGeneration'),
               'worldSessionId': end.get('worldSessionId'), 'actorSlot': end.get('actorSlot')}
        return (a.get('valid') is True and a.get('completed') is True and a.get('status') == 'completed'
                and a.get('endEvidenceStatus') == 'known' and end.get('inputsReleased') is True
                and end.get('effectVerified') is True and verified_server_mine_task(raw, a.get('evidence') or {}))
    successes = [a for a in tasks if verified(a)]
    failures = [a for a in actions if a.get('valid') is not True or a.get('status') != 'completed']
    if action_state == 'unknown' or any(a.get('status') == 'unknown' for a in failures):
        return 'partial' if successes else 'unknown'
    if failures or not program_completed:
        return 'partial' if successes else 'not_completed'
    return 'completed' if tasks and len(successes) == len(tasks) else 'unknown'


def goal_outcome(actions, program_completed=True, action_state=None):
    # There is no general natural-language goal contract. Failures can still
    # expose confirmed progress without upgrading a single task into that goal.
    failures = [a for a in actions if a.get('valid') is not True or a.get('status') != 'completed']
    if not failures and program_completed and action_state != 'unknown':
        return 'unknown'
    if any(a.get('valid') is True and a.get('status') == 'completed' for a in actions):
        return 'partial'
    return 'unknown' if action_state == 'unknown' or any(a.get('status') == 'unknown' for a in failures) else 'not_completed'


def interruption(run, receipt, actions):
    history = action_results(actions, receipt.get('lastAction'), receipt.get('cleanup'))
    cleanup = receipt.get('cleanup') or receipt.get('lastAction') or {}
    end = cleanup.get('endEvidence') or {}
    active = {key: cleanup[key] for key in ('operation', 'actionId', 'actionGeneration',
        'controlGeneration', 'status', 'reason', 'valid', 'endEvidenceStatus') if key in cleanup}
    active['endEvidence'] = {key: end[key] for key in ('worldSessionId', 'actorId', 'actorGeneration',
        'actorSlot', 'gameUpdate', 'phase', 'inputsReleased', 'effectVerified') if key in end}
    return {'runId': run.id, 'generation': run.generation, 'chatId': run.chat_id,
            'reason': run.cancel_reason or 'cancelled', 'programStatus': receipt.get('status'),
            'actionResults': history, 'activeCall': (receipt.get('failure') or {}).get('call'),
            'activeAction': active,
            'taskPhase': ((cleanup.get('evidence') or {}).get('task') or {}).get('phase'),
            'phase': run.phase, 'inputReleased': end.get('inputsReleased'),
            'actionState': receipt.get('actionState', 'unknown'),
            'unconfirmedEffects': [item['sequence'] for item in history['items']
                if item['result'].get('status') != 'completed'
                or item['result'].get('operation') == 'use_item'],
            'evidence': receipt.get('evidence'), 'retryAllowed': False}


def final_text(text, notice):
    """Retain explanations/suggestions, remove unqualified goal-success claims."""
    claims = re.compile(r'任务(?:已经|已|全部)?完成|目标(?:已经|已|全部)?(?:完成|达成)|全部完成|'
                        r'^(?:已完成[。！!\s]*$|完成了?[。！!\s]*$|成功了|召唤成功)|(?:声称|宣称)(?:已)?完成|'
                        r'^\s*(?:done|success)[.!\s]*$|'
                        r'\b(?:task|goal|everything)\s+(?:is\s+)?(?:completed|complete|done|achieved)\b', re.I)
    parts = re.split(r'(?<=[。！？!?；;，,\n])', text)
    def safe(part):
        matches = list(claims.finditer(part))
        return all(re.search(r'未|没|无法|不能|not |unknown', part[max(0, m.start()-8):m.start()], re.I)
                   for m in matches)
    retained = ''.join(p for p in parts if safe(p))
    return notice + retained.strip()
