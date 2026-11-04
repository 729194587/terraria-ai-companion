"""Host-owned run and result semantics, independent of Lua return values."""
from dataclasses import dataclass, field
import re
import time
import threading

from lua_action_results import action_results
import copy


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


def task_outcome(actions, program_completed=True, action_state=None, active_tasks=()):
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
    if any(t.get('status') == 'unknown' for t in active_tasks):
        return 'partial' if successes else 'unknown'
    if any(t.get('status') in ('submitting', 'running') for t in active_tasks):
        return 'running'
    if tasks and len(successes) == len(tasks):
        return 'completed'  # Run/Program/other actions cannot rewrite this task fact.
    failures = [a for a in tasks if a.get('valid') is not True or a.get('status') != 'completed']
    if action_state == 'unknown' or any(a.get('status') == 'unknown' for a in failures):
        return 'partial' if successes else 'unknown'
    if failures:
        return 'partial' if successes else 'not_completed'
    return 'completed' if tasks and len(successes) == len(tasks) else 'unknown'


def goal_outcome(actions, program_completed=True, action_state=None, goal_contract=None):
    # There is no general natural-language goal contract. Failures can still
    # expose confirmed progress without upgrading a single task into that goal.
    failures = [a for a in actions if a.get('valid') is not True or a.get('status') != 'completed']
    # No inferred natural-language goal relevance. Only an explicit host target
    # contract may turn verified destructive progress into goal partial.
    if goal_contract and any(a.get('operation') == goal_contract.get('operation')
            and a.get('targetTask') == goal_contract.get('targetTask')
            and task_outcome([a]) == 'completed' for a in actions):
        return 'partial'
    if not failures and program_completed and action_state != 'unknown':
        return 'unknown'
    return ('not_completed' if goal_contract and failures and action_state != 'unknown'
            and not any(a.get('status') == 'unknown' for a in failures) else 'unknown')


def facts_reply(actions=(), program=None, tasks=None, error=None):
    """Construct execution feedback from host receipts, never from model prose.

    One clause per important class prevents a success/failure priority branch
    hiding another fact. Bounded reasons are diagnostics, never instructions.
    """
    rows = list(actions)
    task_views = [] if tasks is None else tasks.get('results', []) + tasks.get('active', [])
    for view in task_views:
        if view.get('result'):
            rows.append(view['result'])
    unique = {}
    for i, row in enumerate(rows):
        unique[(row.get('actionId') or ('local', i), row.get('operation'))] = row
    rows = list(unique.values())
    clauses = []
    completed = [a for a in rows if a.get('valid') is True and a.get('status') == 'completed']
    if completed:
        ops = ','.join(dict.fromkeys(a.get('operation', 'action') for a in completed))
        clauses.append('已核验动作：' + ops[:64] + '。')
    children = []
    for row in rows:
        task = row.get('task') or (row.get('evidence') or {}).get('task') or {}
        for child in (task.get('progress') or {}).get('children', [])[:16]:
            proof = child.get('result') or {}
            if child.get('status') == 'completed' and proof.get('effectVerified') is True and proof.get('inputsReleased') is True:
                children.append(str(child.get('taskName', 'child')))
    if children:
        clauses.append('已核验子步骤：' + ','.join(dict.fromkeys(children))[:64] + '；不单独证明目标进展。')
    failed = [a for a in rows if a.get('status') in ('failed', 'timeout', 'cancelled', 'unknown') or a.get('valid') is False]
    if failed:
        states = ','.join(dict.fromkeys(str(a.get('status', 'unknown')) for a in failed))
        reasons = ','.join(dict.fromkeys(str(a.get('reason', 'unconfirmed'))[:50] for a in failed))
        clauses.append('行动未完成或未确认：' + states[:40] + '（' + reasons[:72] + '）。')
    if any(a.get('status') == 'unknown' for a in failed):
        clauses.append('执行或输入释放无法确认，不自动重试。')
    if any(a.get('operation') == 'use_item' for a in completed):
        clauses.append('仅确认物品使用周期，命中或其他实际效果未确认。')
    if any(v.get('status') in ('submitting', 'running') for v in task_views):
        clauses.append('已受理游戏任务仍在执行；程序结束不代表任务完成。')
    if program is not None and program.get('status') != 'completed':
        clauses.append('Lua 程序未完成（' + str(program.get('status'))[:32] + '）。')
    elif program is not None and not rows:
        observation = program.get('lastObservation') or {}
        clauses.append('Lua 程序已正常结束。')
        if observation.get('operation'):
            clauses.append('已取得观察：' + str(observation['operation'])[:48] + '；观察不证明目标已完成。')
    if error:
        clauses.append('本轮决策/回复未完成；已确认的执行事实保留。')
    clauses.append('整体目标完成情况尚未确认。')
    return ''.join(clauses)


def direct_action_receipt(operation, lifecycle, result=None):
    """Project a direct call's existing cancellation supervision; never stop/retry here."""
    action = lifecycle.get('action') or {}
    if not action and not lifecycle.get('start_attempted') and not (result or {}).get('actionId'):
        return None  # No owned action or possible start; keep the preceding execution receipt.
    cleanup = lifecycle.get('cleanup')
    if lifecycle.get('dispatchState') == 'not_admitted' and not action and not lifecycle.get('uncertain'):
        rejected = copy.deepcopy(result or cleanup)
        return {'kind': 'direct_action', 'status': 'cancelled' if result is None else rejected['status'],
                'programStatus': None, 'lastAction': rejected, 'cleanup': rejected if result is None else None,
                'actionState': 'idle', 'inputReleased': None}
    terminal = copy.deepcopy(result if result is not None else cleanup) if result is not None or cleanup is not None else {
        'actionId': action.get('actionId'), 'operation': action.get('operation', operation),
        'actionGeneration': action.get('generation'), 'controlGeneration': lifecycle.get('control_generation'),
        'worldSessionId': action.get('worldSessionId'), 'session': action.get('session'),
        'endEvidence': copy.deepcopy(action.get('endEvidence')),
        'endEvidenceStatus': action.get('endEvidenceStatus', 'unknown'),
        'reason': lifecycle.get('cleanupError', 'cleanup_unconfirmed')}
    # A failed stop can lack identity even though the start ACK established it.
    for key, value in (('actionId', action.get('actionId')), ('operation', action.get('operation', operation)),
                       ('actionGeneration', action.get('generation')), ('worldSessionId', action.get('worldSessionId'))):
        if terminal.get(key) is None:
            terminal[key] = value
    released = (terminal.get('valid') is True and terminal.get('status') in ('completed', 'cancelled', 'failed', 'timeout')
                and terminal.get('endEvidenceStatus') == 'known' and isinstance(terminal.get('endEvidence'), dict)
                and terminal['endEvidence'].get('inputsReleased') is not False
                and not lifecycle.get('uncertain'))
    if operation in ('move', 'stop_movement'):
        released = released and (terminal.get('execution') or {}).get('inputReleased') is True
    if result is None and not released:
        terminal.setdefault('terminalActionStatus', terminal.get('status', action.get('status')))
        terminal.update(valid=False, status='unknown', completed=False)
    return {'kind': 'direct_action', 'status': 'cancelled' if result is None else terminal.get('status'),
            'programStatus': None, 'lastAction': terminal, 'cleanup': terminal if result is None else None,
            'actionState': 'released' if released else 'unknown', 'inputReleased': True if released else None,
            'failure': {'call': {'operation': operation}} if result is None else None}


def interruption(run, receipt, actions):
    history = action_results(actions, receipt.get('lastAction'), receipt.get('cleanup'))
    cleanup = receipt.get('cleanup') or receipt.get('lastAction') or {}
    end = cleanup.get('endEvidence') or {}
    active = {key: cleanup[key] for key in ('operation', 'actionId', 'actionGeneration', 'worldSessionId', 'session',
        'controlGeneration', 'status', 'reason', 'valid', 'endEvidenceStatus') if key in cleanup}
    active['endEvidence'] = {key: end[key] for key in ('worldSessionId', 'actorId', 'actorGeneration',
        'actorSlot', 'gameUpdate', 'phase', 'inputsReleased', 'effectVerified') if key in end}
    return {'runId': run.id, 'generation': run.generation, 'chatId': run.chat_id,
            'reason': run.cancel_reason or 'cancelled', 'programStatus': receipt.get('programStatus', receipt.get('status')),
            'actionResults': history, 'activeCall': (receipt.get('failure') or {}).get('call'),
            'activeAction': active,
            'taskPhase': ((cleanup.get('evidence') or {}).get('task') or {}).get('phase'),
            'phase': run.phase, 'inputReleased': receipt.get('inputReleased', end.get('inputsReleased'))
                if receipt.get('actionState') == 'released' else None,
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
