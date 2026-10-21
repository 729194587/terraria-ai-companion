"""Per-chat movement guards and result verification, never an action planner."""
import math
import time


class LoopBudgetExhausted(Exception):
    pass


def vector(value):
    return (isinstance(value, dict) and all(type(value.get(k)) in (int, float)
            and math.isfinite(value[k]) for k in ('x', 'y')))


class MovementLoop:
    def __init__(self, max_move_ms=6000, clock=time.monotonic):
        self.max_move_ms, self.clock = max_move_ms, clock
        self.reserved_ms = 0
        self.active = False
        self.pending_observation = False
        self.observed = None
        self.observed_at = None
        self.before = None
        self.target = None
        self.tolerance = 16
        self.no_progress = 0
        self.terminal = None
        self.reason = None
        self.expected_generation = None

    def finish(self, status, reason):
        if self.terminal is None:
            self.terminal, self.reason = status, reason

    def snapshot(self):
        status = self.terminal or ('awaiting_observation' if self.pending_observation or self.observed is None else 'ready')
        return {'status': status,
                'reason': self.reason, 'reservedMovementMs': self.reserved_ms,
                'remainingMovementMs': self.max_move_ms-self.reserved_ms,
                'needsObservation': self.pending_observation or self.observed is None,
                'targetPositionWorldPixels': self.target, 'arrivalTolerancePixels': self.tolerance,
                'positionConvention': 'player collision box top-left, world pixels',
                'consecutiveNoProgress': self.no_progress,
                'controlGeneration': self.expected_generation,
                'observationId': self.observed.get('observationId') if self.observed else None,
                'gameUpdate': self.observed.get('gameUpdate') if self.observed else None}

    def observe(self, result):
        source = result.get('exploration') or result.get('observation')
        if not isinstance(source, dict):
            return  # get_state is still readable; lacks world/tick/death metadata for this loop.
        player = source.get('player') or {}
        old = self.observed
        changed_world = old and old.get('worldSessionId') != source.get('worldSessionId')
        life = player.get('life')
        dead = player.get('dead') is True or player.get('ghost') is True or type(life) in (int,float) and life <= 0
        self.observed = None
        generation = result.get('controlGeneration')
        if self.expected_generation is not None and generation != self.expected_generation:
            self.finish('cancelled','control_generation_changed')
            return
        if changed_world:
            self.finish('cancelled', 'world_changed')
            return
        if dead:
            self.finish('cancelled', 'player_dead_or_ghost')
            self.reason = self.reason or 'player_dead_or_ghost'
            return
        position = player.get('positionWorldPixels')
        if (result.get('valid') is not True or source.get('status') not in ('ok','partial')
                or (source.get('camera') or {}).get('status') != 'recent_render'
                or not vector(position) or type(source.get('gameUpdate')) is not int
                or not source.get('worldSessionId') or not source.get('observationId')):
            if self.pending_observation:
                self.finish('failed', 'post_action_observation_unavailable_or_stale')
            return
        if self.pending_observation and self.before:
            if source['worldSessionId'] != self.before['worldSessionId']:
                self.finish('cancelled', 'world_changed')
                return
            if source['gameUpdate'] <= self.before['gameUpdate'] or source['observationId'] == self.before['observationId']:
                self.finish('failed', 'post_action_observation_not_newer')
                return
            if self.target:
                progress = math.dist(tuple(self.before['position'][k] for k in ('x','y')), tuple(self.target[k] for k in ('x','y')))
                progress -= math.dist(tuple(position[k] for k in ('x','y')), tuple(self.target[k] for k in ('x','y')))
            else:
                progress = abs(position['x']-self.before['position']['x'])
            self.no_progress = self.no_progress+1 if progress < 1 else 0
        self.observed = {k:source[k] for k in ('worldSessionId','observationId','gameUpdate')}
        self.observed['position'] = dict(position)
        self.observed['controlGeneration'] = generation
        self.expected_generation = generation
        self.observed_at = self.clock()
        self.pending_observation = False
        self.reason = self.reason if self.terminal else None
        if self.active and self.at_target():
            self.finish('arrived', 'verified_by_fresh_observation')
        elif self.no_progress >= 2:
            self.finish('no_progress', 'two_actions_without_one_pixel_progress')

    def at_target(self):
        return self.target is not None and self.observed is not None and math.dist(
            tuple(self.observed['position'][k] for k in ('x','y')),
            tuple(self.target[k] for k in ('x','y'))) <= self.tolerance

    def prepare(self, arguments):
        self.active = True
        if (not isinstance(arguments,dict) or not {'direction','duration_ms'} <= set(arguments)
                or set(arguments)-{'direction','duration_ms','target_position','arrival_tolerance'}
                or arguments['direction'] not in ('left','right')
                or type(arguments['duration_ms']) is not int or not 50 <= arguments['duration_ms'] <= 2000):
            raise ValueError('move requires direction left/right, duration_ms 50..2000; optional target_position and arrival_tolerance')
        if self.terminal:
            return self.blocked('movement_loop_terminated')
        if self.pending_observation or self.observed is None:
            if self.reason == 'player_dead_or_ghost':
                self.finish('cancelled','player_dead_or_ghost')
            return self.blocked('fresh_exploration_or_full_observation_required')
        if self.clock()-self.observed_at > 30:
            self.observed = None
            return self.blocked('observation_older_than_30_seconds')
        if type(self.observed['controlGeneration']) is not int or self.observed['controlGeneration'] < 0:
            return self.blocked('control_generation_unavailable_update_bridge')
        target = arguments.get('target_position')
        tolerance = arguments.get('arrival_tolerance', self.tolerance)
        if type(tolerance) not in (int,float) or not math.isfinite(tolerance) or not 1 <= tolerance <= 32:
            raise ValueError('arrival_tolerance must be 1..32 pixels')
        if target is not None:
            if not vector(target) or set(target) != {'x','y'}:
                raise ValueError('target_position requires finite world-pixel x/y')
            if self.target is None and self.reserved_ms:
                return self.blocked('target_must_be_set_before_first_action')
            if self.target is not None and (self.target != target or self.tolerance != tolerance):
                self.finish('failed','target_changed_mid_chat')
                return self.blocked('target_changed_mid_chat')
            self.target = {k:target[k] for k in ('x','y')}
            self.tolerance = tolerance
        elif 'arrival_tolerance' in arguments and tolerance != self.tolerance:
            raise ValueError('arrival_tolerance can only be set with the first target_position')
        if self.at_target():
            self.finish('arrived','already_within_target_tolerance')
            return self.blocked('already_at_target_no_action_started')
        duration = arguments['duration_ms']
        if self.reserved_ms+duration > self.max_move_ms:
            self.finish('budget_exhausted','cumulative_movement_time_limit')
            return self.blocked('cumulative_movement_time_limit')
        # Reserve requested duration even for rejected/uncertain results, never refund or guess elapsed time.
        self.reserved_ms += duration
        self.before = self.observed.copy()
        self.pending_observation = True
        return None

    def moved(self, result):
        if result.get('status') == 'unknown':
            self.finish('unknown','action_result_unknown_no_retry')
        elif result.get('status') == 'cancelled':
            self.finish('cancelled',result.get('reason','action_cancelled'))
        elif result.get('valid') is not True or result.get('status') != 'completed' or result.get('completed') is not True:
            self.finish('failed',result.get('reason','action_failed'))
        elif result.get('controlGeneration') != self.before['controlGeneration'] + 2:
            self.finish('cancelled','control_generation_changed_or_unconfirmed')
        else:
            self.expected_generation = result['controlGeneration']

    def blocked(self, reason):
        return {'valid':False, 'status':'not_started', 'completed':False, 'cancelled':False,
                'reason':reason, 'movementLoop':self.snapshot()}

    def final_text(self):
        if self.terminal is None:
            if self.pending_observation:
                self.finish('failed','missing_post_action_observation')
            elif self.reserved_ms >= self.max_move_ms:
                self.finish('budget_exhausted','cumulative_movement_time_limit')
            else:
                self.finish('stopped','model_finished_without_verified_arrival')
        messages = {'arrived':'已根据最新观察确认到达本轮设定的坐标目标。',
                    'stopped':'本轮移动已停止，未确认抵达目标。',
                    'cancelled':'本轮移动已取消，不会自动恢复；未确认抵达目标。',
                    'unknown':'动作结果未知，已停止继续移动；未确认抵达目标。',
                    'no_progress':'连续两次行动没有足够进展，已停止；未确认抵达目标。',
                    'failed':'行动或后续观察未通过检查，已停止；未确认抵达目标。',
                    'budget_exhausted':'本轮行动预算已用尽，已停止；未确认抵达目标。'}
        return messages[self.terminal]
