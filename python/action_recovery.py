"""Model guidance and suppression of unchanged, proven ineffective actions."""
import json


STANCE_FAILURES = {
    'no_safe_mining_stance_in_local_window',
    'navigation_local_frontier_exhausted',
    'navigation_liquid_local_frontier_exhausted',
}
POSITION_ACTIONS = {'move_input', 'move_on_rope', 'navigate_to',
                    'navigate_to_x', 'navigate_to_landing_region'}
INVENTORY_ACTIONS = {'select_hotbar_slot', 'hold_inventory_item', 'swap_inventory_slots',
                     'transfer_inventory_item', 'equip_item', 'unequip_item'}
TERRAIN_ACTIONS = {'mine_tile', 'mine_target', 'place_tile', 'place_rope', 'place_object'}
SEARCH_LIMITS = {
    'navigation_trip_search_budget', 'navigation_search_timeout', 'navigation_search_expansion_budget',
    'navigation_surface_node_budget', 'navigation_local_coverage_exhausted',
    'navigation_edit_window_exceeded', 'navigation_edit_budget_exhausted',
}


def guidance(operation, reason, *, detail=None):
    if reason == 'bad_argument':
        kind, next_step = 'bad_argument', f'检查参数错误详情，调用 api("help",{{name="{operation}"}})，修正字段后再调用。'
    elif reason.startswith('unsupported_selected'):
        kind, next_step = 'unsupported', '读取选中物品并选择对应入口。放原版绳索用 place_tile 指定空格，延伸已有绳段用 place_rope。'
    elif reason.startswith('unsupported'):
        kind, next_step = 'unsupported', '检查该能力的 Help 和当前角色状态，改用已开放且适用的操作。'
    elif 'out_of_range' in reason:
        kind, next_step = 'out_of_range', '重新观察角色和目标位置，先安全接近并确认位移，再尝试该目标。'
    elif reason in SEARCH_LIMITS:
        kind, next_step = 'search_incomplete', '重新观察地形，选择更近且已知安全的落脚点作为下一段目标。'
    elif reason in STANCE_FAILURES:
        kind, next_step = 'no_stance', '重新观察目标周围，选择另一侧或不同高度的安全接近点，或改选可安全到达的目标。'
    elif reason == 'no_progress':
        kind, next_step = 'no_progress', '输入已执行但没有位移。观察阻挡或支撑，换方向、落脚点或移动方式。'
    else:
        kind, next_step = 'execution_failed', '根据当前观察和结束证据核对已发生的效果，再选择不同操作。'
    return dict(operation=operation, reason=reason, kind=kind, nextStep=next_step,
                **({'detail': detail[:512]} if isinstance(detail, str) and detail else {}))


def released_task_failure(reply):
    """The task monitor has already matched admission and terminal identity."""
    if not (reply.get('valid') is True and reply.get('status') == 'failed' and not reply.get('supervisorError')
            and reply.get('dispatchState') == 'admitted' and reply.get('endEvidenceStatus') == 'known'):
        return False
    task, end = reply.get('task') or {}, reply.get('endEvidence') or {}
    return (task.get('taskId') == reply.get('actionId') and bool(reply.get('actionId'))
            and task.get('phase') == 'terminal' and task.get('status') == 'failed'
            and task.get('currentChild') is None
            and end.get('phase') == 'ServerAI_game_thread_inputs_released'
            and end.get('operation') == reply.get('operation')
            and end.get('worldSessionId') == reply.get('worldSessionId') and bool(reply.get('worldSessionId'))
            and end.get('actorId') == reply.get('actorId') and bool(reply.get('actorId'))
            and type(end.get('actorGeneration')) is int and end['actorGeneration'] == reply.get('actionGeneration')
            and type(end.get('actorSlot')) is int and end['actorSlot'] == reply.get('actorSlot')
            and end.get('inputsReleased') is True and end.get('effectVerified') is False
            and (reply.get('reason') in STANCE_FAILURES
                 or reply.get('reason') == 'out_of_range' and end.get('inputsAcquired') is False))


class Recovery:
    def __init__(self, scope):
        self.scope = scope
        self.attempts = {}

    @staticmethod
    def key(name, args):
        target = args.get('target')
        if name == 'mine_target' and isinstance(target, dict) and {'x', 'y'} <= target.keys():
            args = {'x': target['x'], 'y': target['y'], **({'cluster': True} if args.get('cluster') is True else {})}
        return name, json.dumps(args, sort_keys=True, separators=(',', ':'))

    def blocked(self, name, args):
        row = self.attempts.get(self.key(name, args))
        return row[1] if row and row[0] >= (2 if row[1]['kind'] == 'no_progress' else 1) else None

    def observe(self, name, args, reply):
        key = self.key(name, args)
        proof = reply.get('evidence') or {}
        if reply.get('status') == 'completed' and reply.get('valid') is True:
            if name == 'move_input' and proof.get('noProgress') is True:
                reason = 'no_progress'
            else:
                changed = set()
                if name in POSITION_ACTIONS:
                    changed.update(('out_of_range', 'no_stance', 'no_progress', 'execution_failed'))
                if name in INVENTORY_ACTIONS:
                    changed.update(('unsupported', 'execution_failed'))
                if name in TERRAIN_ACTIONS:
                    changed.update(('no_stance', 'no_progress', 'execution_failed'))
                if changed:
                    self.attempts = {k: v for k, v in self.attempts.items()
                                     if v[1]['kind'] not in changed}
                return None
        elif reply.get('status') == 'failed':
            reason = reply.get('reason') or 'execution_failed'
        else:
            return None
        row = guidance(name, reason, detail=reply.get('reasonDetail'))
        if reason == 'no_progress' or (reply.get('continuationAllowed') is True
                and reason not in ('actor_busy', 'active_task_conflict')):
            count, old = self.attempts.pop(key, (0, {}))
            if len(self.attempts) >= 64:
                self.attempts.pop(next(iter(self.attempts)))
            self.attempts[key] = (count + 1 if old.get('reason') == reason else 1, row)
        return row
