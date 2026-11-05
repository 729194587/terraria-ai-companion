"""Single-call mine_target through real Lua/IPC/native updates in a copied world."""
import argparse
import asyncio
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
import atomic_operations as atomic


async def verify(args):
    profile = args.profile.resolve()
    rows = []
    def save():
        (profile/'mine-target-results.json').write_text(json.dumps(dict(records=rows), ensure_ascii=False, indent=2), encoding='utf-8')
    async def fixture(case):
        ready = profile/'mine-task-ready.json'
        ready.unlink(missing_ok=True)
        temp = profile/'mine-task-request.tmp'
        temp.write_text(json.dumps(dict(caseId=case)))
        temp.replace(profile/'mine-task-request.json')
        for _ in range(300):
            if ready.exists():
                return json.loads(ready.read_text())
            await asyncio.sleep(.03)
        raise TimeoutError(case)
    async def run(case, source):
        (profile/f'mine-target-{case}.lua').write_text(source, encoding='utf-8')
        bridge = ServerAiBridge(args.port)
        try:
            r = await LuaRuntime(bridge, evidence_root=profile/'mine-target-lua').execute(source, limits=Limits(actions=1, calls=4, seconds=58))
        finally:
            bridge.close()
        rows.append(dict(case=case, result=r));save()
        expected = {'no_tool': 'failed', 'deadline': 'timeout'}
        if case == 'underpowered':
            assert r['lastAction']['status'] == 'failed', (case, r)
        elif case == 'blocked':
            # The local planner may exhaust a frontier or spend the whole trip
            # searching. A budget exit does not prove this wall globally impassable.
            assert r['status'] in ('failed', 'timeout'), (case, r)
            if r['status'] == 'timeout':
                assert r['lastAction']['reason'] in ('execution_deadline', 'task_game_update_deadline'), r
        else:
            assert r['status'] == expected.get(case, 'completed'), (case, r)
        assert r['actions'] == 1, r
        return r['value'] if r['status'] == 'completed' else r['lastAction']
    async def verify_navigation():
        for case, edit in (
            ('navigation_flat', None), ('navigation_jump', None), ('navigation_drop', None), ('navigation_trip', None),
            ('navigation_surface_wide', None), ('navigation_surface_detour', None),
            ('navigation_step_half_roof', None), ('navigation_step_up_roof', None), ('navigation_step_down_roof', None),
            ('navigation_single', None), ('navigation_single_platform', None),
            ('navigation_half', None), ('navigation_slope', None),
            ('navigation_dig', 'mine_tile'), ('navigation_down', 'mine_tile'),
            ('navigation_place', 'place_tile'),
            ('navigation_long_dig', 'mine_tile'), ('navigation_long_place', 'place_tile'),
        ):
            spec = await fixture(case)
            if case.startswith('navigation_step_'):
                proof=spec['stepEvidence']
                assert proof['candidate']=='Walk' and proof['walkSafe'] and proof['walkArrived'] and not proof['walkJump'],proof
                assert 0 < proof['jumpRise'] <= 16,proof
                assert proof['feetChange']==(16 if case=='navigation_step_down_roof' else -8 if case=='navigation_step_half_roof' else -16),proof
                (profile/f'{case}-proof.json').write_text(json.dumps(proof,indent=2),encoding='utf-8')
            if case in ('navigation_single', 'navigation_single_platform', 'navigation_half', 'navigation_slope'):
                await asyncio.sleep(.25)  # Let native Player.Update land from above the support.
                if case == 'navigation_slope':
                    bridge = ServerAiBridge(args.port)
                    try:
                        q = bridge.command('query_atomic', operation='get_player_state', arguments={})
                        pos = q['data']['data']['player']['position']
                        assert abs(pos['x'] + 10 - spec['destinationX']) < 1, q
                        surface = spec['destinationY'] // 16 * 16
                        assert surface <= pos['y'] + 42 < surface + 16, q
                        spec['destinationY'] = pos['y'] + 42
                    finally:
                        bridge.close()
            source = f"return api('navigate_to',{{x={spec['destinationX']},y={spec['destinationY']}}})"
            result = await run(case, source)
            task = result['task']
            children = task['progress']['children']
            assert result['valid'] and result['completed'] and result['endEvidence']['inputsReleased'], result
            assert task['currentChild'] is None and all(c['parentTaskId'] == task['taskId'] for c in children), task
            ticks = [c['observedGameUpdate'] for c in children]
            assert ticks == sorted(set(ticks)), task
            names = [c['taskName'] for c in children]
            edits = [name for name in names if name != 'navigate_to_landing_region']
            assert (not edits if edit is None else edit in edits), task
            if case == 'navigation_jump':
                assert any(c['result']['evidence']['navigation']['CompletedJumps'] > 0 for c in children), task
            if case == 'navigation_trip':
                assert names == ['navigate_to_landing_region'], task
                motion = children[0]['result']['evidence']
                assert motion['x'] - motion['startX'] > 192 and motion['navigation']['PlanNumber'] > 1, motion
            if case in ('navigation_surface_wide', 'navigation_surface_detour'):
                assert names == ['navigate_to_landing_region'], task
                motion = children[0]['result']['evidence']
                assert motion['arrived'] and motion['grounded'] and motion['safeSupport'] and motion['settled'], motion
                assert motion['navigation']['SurfaceGraph']['CandidateEdges'] > 0, motion
                if case == 'navigation_surface_wide':
                    assert motion['x'] - motion['startX'] > 24 * 16, motion
                else:
                    assert motion['navigation']['CompletedJumps'] > 0, motion
            if case == 'navigation_down':
                assert task['progress']['opening']['drops'], task
            if case == 'navigation_place':
                proof = next(c['result']['evidence'] for c in children if c['taskName'] == 'place_tile')
                assert proof['placedByActor'] and proof['consumedCount'] == 1, proof
            if case == 'navigation_long_dig':
                assert task['progress']['dugTiles'] > 18, task
            if case == 'navigation_long_place':
                assert task['progress']['placedTiles'] > 8, task
            print(json.dumps(dict(case=case, children=names, reason=result['reason'])), flush=True)
    if args.navigation_only:
        await verify_navigation()
        return

    choose = "local q=api('observe_scene',{layers={'objects'}}); for _,o in ipairs(q.data.objects.items) do if o.kind=='ore_component' and o.target then return api('mine_target',{target=o.target}) end end; error('no observed ore')"
    for case in ('approach', 'near', 'blocked', 'no_tool'):
        spec = await fixture(case)
        result = await run(case, choose)
        assert result['valid'] and result['endEvidence']['inputsReleased'], result
        task = result['evidence']['task']
        if case in ('approach', 'near'):
            assert result['completed'] and task['status'] == 'completed', result
            children = task['progress']['children']
            names = [c['taskName'] for c in children]
            assert names == (['navigate_to_landing_region', 'mine_tile'] if case == 'approach' else ['mine_tile']), task
            proof = task['lastResult']['evidence']
            assert proof['X'] == spec['x'] and proof['Y'] == spec['y'] and proof['destroyedByActor'] and proof['targetAbsent'], proof
        else:
            assert result['status'] in ('failed','timeout') and not result['completed'], result
            assert 'mine_tile' not in [c['taskName'] for c in task['progress']['children']], task
        print(json.dumps(dict(case=case, status=result['status'], reason=result['reason'])), flush=True)
    for case in ('hotbar', 'bag', 'reach_corner', 'workbench', 'workbench_task', 'underpowered', 'cutout', 'deadline'):
        spec = await fixture(case)
        op = 'mine_target' if case in ('cutout', 'workbench_task') else 'mine_tile'
        source = f"return api('{op}',{{x={spec['x']},y={spec['y']}}})"
        result = await run(case, source)
        assert result['endEvidence']['inputsReleased'], result
        if case == 'underpowered':
            assert result['reason'] == 'insufficient_pick_power' and result['evidence']['targetUnchanged'], result
            continue
        if case == 'deadline':
            assert result['status'] == 'timeout' and result['evidence']['targetUnchanged'], result
        else:
            assert result['completed'] and result['effectVerified'], result
        proof = result['evidence'] if op == 'mine_tile' else result['task']['lastResult']['evidence']
        if case != 'deadline':
            assert proof['targetAbsent'] and proof['destroyedByActor'], proof
        if case in ('workbench', 'workbench_task'):
            assert proof['furnitureCells'] == 2 and proof['furnitureAbsent'], proof
        bridge = ServerAiBridge(args.port)
        try:
            q = bridge.command('query_atomic', operation='get_player_state', arguments={})
            assert q['data']['data']['player']['selectedSlot'] == 0, q
            if case in ('bag', 'deadline'):
                q = bridge.command('query_atomic', operation='inspect_inventory', arguments=dict(mode='full'))
                slots = q['data']['data']['inventory']
                assert any(i['slot']==20 and i['contentKey']=='Terraria/CopperPickaxe' for i in slots), q
        finally:
            bridge.close()
        print(json.dumps(dict(case=case, status=result['status'], reason=result['reason'])), flush=True)

    spec = await fixture('bag')
    bridge = ServerAiBridge(args.port)
    try:
        bridge.command('start_atomic', operation='mine_tile', arguments=dict(x=spec['x'], y=spec['y']),
                       expectedGeneration=bridge.binding['generation'])
        for _ in range(100):
            action = bridge.command('get_movement')['movement']['action']
            if (action.get('evidence') or {}).get('Strikes', 0) > 0:
                break
            assert action['status'] == 'running', action
            await asyncio.sleep(.01)
        else:
            raise AssertionError('mining never struck')
        stopped = bridge.command('cancel_move', actionId=bridge._action_id)['movement']['action']
        assert stopped['status'] == 'cancelled' and stopped['reason'] == 'explicit_stop', stopped
        assert stopped['endEvidence']['inputsReleased'], stopped
        q = bridge.command('query_atomic', operation='inspect_inventory', arguments=dict(mode='full'))
        slots = {i['slot']: i.get('contentKey') for i in q['data']['data']['inventory']}
        assert slots[0] == 'Terraria/CopperShortsword' and slots[20] == 'Terraria/CopperPickaxe', q
        rows.append(dict(case='cancel-bag-mining', result=stopped));save()
    finally:
        bridge.close()

    for case, status in (
        ('opening_across', 'completed'), ('opening_down', 'completed'), ('opening_probe', 'completed'),
        ('opening_hazard', 'failed'), ('opening_hard', 'failed'), ('opening_unsafe', 'timeout'),
        ('opening_deadline', 'timeout'), ('opening_unknown', 'unknown'),
    ):
        spec = await fixture(case)
        source = f"return api('mine_target',{{x={spec['x']},y={spec['y']}}})"
        (profile/f'mine-target-{case}.lua').write_text(source, encoding='utf-8')
        bridge = ServerAiBridge(args.port)
        try:
            raw = await LuaRuntime(bridge, evidence_root=profile/'mine-target-lua').execute(
                source, limits=Limits(actions=1, calls=1, seconds=58))
            result = raw.get('value') if raw['status'] == 'completed' else raw.get('lastAction')
            rows.append(dict(case=case, result=raw));save()
            # The original shaft is unsafe, but a graph detour may find a
            # supported side shaft. Completion needs independent native proof.
            allowed = ('failed','timeout','completed') if case == 'opening_unsafe' else (status,)
            assert raw['actions'] == 1 and result and result['status'] in allowed, (case, raw)
            status = result['status']
            if case == 'opening_unsafe' and status == 'timeout':
                assert result['reason'] in ('execution_deadline','task_game_update_deadline'), result
            assert result['endEvidence']['inputsReleased'], result
            task = result['task']
            history = task['progress']['children']
            assert all(c['parentTaskId'] == task['taskId'] for c in history), history
            assert len({c['taskId'] for c in history}) == len(history), history
            ticks = [c['observedGameUpdate'] for c in history]
            assert ticks == sorted(ticks) and task['currentChild'] is None, task
            if status == 'completed':
                cuts = [c for c in history if c['taskName']=='mine_tile']
                assert len(cuts)>1 and task['progress']['clearedTiles']==len(cuts)-1, task
                for cut in cuts[:-1]:
                    proof = cut['result']['evidence']
                    assert (proof['X'], proof['Y']) != (spec['x'], spec['y']) and proof['destroyedByActor'], proof
                proof = cuts[-1]['result']['evidence']
                assert (proof['X'], proof['Y']) == (spec['x'], spec['y']) and proof['destroyedByActor'], proof
                if case in ('opening_down', 'opening_probe', 'opening_unsafe'):
                    drops = task['progress']['opening']['drops']
                    assert drops and all(d['toFeetY']-d['fromFeetY']==16 for d in drops), task
                    if case == 'opening_unsafe':
                        assert any(c['taskName']=='navigate_to_landing_region' and c['status']=='completed'
                                   and c['result']['evidence']['safeSupport'] and c['result']['evidence']['settled'] for c in history), task
                        assert all(d['playerLeftX'] < spec['x'] * 16 for d in drops), task
                else:
                    assert any(c['taskName']=='navigate_to_landing_region' and c['status']=='completed' for c in history), task
            else:
                assert result['reasonDetail'], result
                assert all(c['taskName']!='mine_tile' or c['status']!='completed' for c in history), task
                if case == 'opening_hard':
                    assert 'insufficient_pick_power' in result['reasonDetail'], result
            print(json.dumps(dict(case=case, status=status, reason=result['reason'],
                                  cleared=task['progress']['clearedTiles'])), flush=True)
        finally:
            bridge.close()

    spec = await fixture('opening_cancel')
    bridge = ServerAiBridge(args.port)
    try:
        bridge.command('start_atomic', operation='mine_target', arguments=dict(x=spec['x'], y=spec['y']),
                       expectedGeneration=bridge.binding['generation'])
        for _ in range(100):
            action = bridge.command('get_movement')['movement']['action']
            child = (action.get('task') or {}).get('currentChild') or {}
            if child.get('taskName') == 'mine_tile' and child.get('phase') != 'accepted':
                break
            assert action['status'] == 'running', action
            await asyncio.sleep(.01)
        else:
            raise AssertionError('opening child never started')
        stopped = bridge.command('cancel_move', actionId=bridge._action_id)['movement']['action']
        assert stopped['status'] == 'cancelled' and stopped['endEvidence']['inputsReleased'], stopped
        await asyncio.sleep(.15)
        final = bridge.command('get_movement')['movement']['action']
        assert final['task']['progress']['children'] == stopped['task']['progress']['children'], final
        rows.append(dict(case='opening_cancel', result=final));save()
    finally:
        bridge.close()

    spec = await fixture('approach')
    autonomous = ServerAiBridge(args.port)
    try:
        autonomous.command('start_atomic', operation='mine_target', arguments=dict(x=spec['x'], y=spec['y']), expectedGeneration=autonomous.binding['generation'])
        await asyncio.sleep(8)  # No action polls, renewals or further decisions during execution.
        action = autonomous.command('get_movement')['movement']['action']
        assert action['status'] == 'completed' and action['endEvidence']['effectVerified'], action
        assert [c['taskName'] for c in action['task']['progress']['children']] == ['navigate_to_landing_region', 'mine_tile'], action
        rows.append(dict(case='autonomous-without-polls', result=action));save()
    finally:
        autonomous.close()
    spec = await fixture('approach')
    absolute = await run('absolute', f"return api('mine_target',{{x={spec['x']},y={spec['y']}}})")
    assert absolute['completed'], absolute
    for change in ('change', 'replace_same', 'modified', 'modified_world', 'modified_slot', 'modified_type', 'modified_coordinate', 'expired'):
        await fixture('approach')
        bridge = ServerAiBridge(args.port)
        try:
            q = bridge.command('query_atomic', operation='observe_scene', arguments=dict(layers=['objects']))
            target = next(o['target'] for o in q['data']['data']['objects']['items'] if o['kind'] == 'ore_component')
            if change in ('change', 'replace_same'):
                await fixture(change)
            elif change.startswith('modified'):
                field = {'modified':'actorGeneration','modified_world':'worldSessionId','modified_slot':'actorSlot','modified_type':'typeId','modified_coordinate':'x'}[change]
                target[field] = 'other-world' if field == 'worldSessionId' else target[field] - 1 if field == 'actorSlot' else target[field] + 1
            else:
                (profile/'task-freeze-updates').write_text('freeze natural updates for observation expiry only')
                await asyncio.sleep(30.1)
                (profile/'task-freeze-updates').unlink()
            result = await atomic.dispatch(bridge, 'mine_target', dict(target=target))
            rows.append(dict(case=change, result=result));save()
            assert result['valid'] and result['status'] == 'failed', result
            assert result['evidence']['task']['progress']['completedChildren'] == 0, result
            assert result['reason'] == ('observed_target_expired' if change == 'expired' else 'observed_target_modified' if change.startswith('modified') else 'target_changed_since_observation'), result
        finally:
            (profile/'task-freeze-updates').unlink(missing_ok=True)
            bridge.close()
    await fixture('approach')
    bridge = ServerAiBridge(args.port)
    try:
        frozen = profile/'task-freeze-updates'
        frozen.write_text('prove idle IPC cannot advance task')
        ack = profile/'task-freeze-ready.json'
        for _ in range(100):
            if ack.exists():
                break
            await asyncio.sleep(.03)
        bridge.command('start_atomic', operation='mine_target', arguments=dict(x=spec['x'], y=spec['y']), expectedGeneration=bridge.binding['generation'])
        snapshots = []
        for _ in range(8):
            a = bridge.command('get_movement')['movement']['action']
            snapshots.append(a)
            assert a['status'] == 'running' and (a.get('task') or {}).get('currentChild') is None, a
            await asyncio.sleep(.05)
        ticks = {s['task']['observedGameUpdate'] for s in snapshots if 'observedGameUpdate' in (s.get('task') or {})}
        assert len(ticks) == 1, snapshots
        stopped = bridge.command('cancel_move', actionId=bridge._action_id)['movement']['action']
        assert stopped['status'] == 'cancelled' and stopped['endEvidence']['inputsReleased'], stopped
        rows.append(dict(case='idle-and-cancel', snapshots=snapshots, result=stopped));save()
    finally:
        (profile/'task-freeze-updates').unlink(missing_ok=True)
        bridge.close()
    await fixture('approach')
    bridge = ServerAiBridge(args.port)
    try:
        bridge.command('start_atomic', operation='mine_target', arguments=dict(x=spec['x'], y=spec['y']), expectedGeneration=bridge.binding['generation'])
        for _ in range(100):
            action = bridge.command('get_movement')['movement']['action']
            child = (action.get('task') or {}).get('currentChild') or {}
            if child.get('taskName') == 'navigate_to_landing_region' and child.get('phase') != 'accepted':
                break
            assert action['status'] == 'running', action
            await asyncio.sleep(.02)
        else:
            raise AssertionError('navigation child never started')
        stopped = bridge.command('cancel_move', actionId=bridge._action_id)['movement']['action']
        assert stopped['status'] == 'cancelled' and stopped['endEvidence']['inputsReleased'], stopped
        assert stopped['task']['lastResult']['status'] == 'cancelled', stopped
        rows.append(dict(case='cancel-navigation-child', result=stopped));save()
    finally:
        bridge.close()
    print(f'Mine target: {len(rows)} isolated cases passed; one external action per task, no model', flush=True)
    await verify_navigation()



if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--port', type=int, required=True)
    p.add_argument('--profile', type=Path, required=True)
    p.add_argument('--navigation-only', action='store_true')
    asyncio.run(verify(p.parse_args()))
