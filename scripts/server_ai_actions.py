"""Explicit Lua server-AI action acceptance; never creates tools, terrain or a new actor."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def verify(args):
    source = 'local evidence = {actions=json.array()}\n'
    source += "local before = api('get_player_state', {})\nassert(before.valid)\nevidence.before={position=before.player.position,life=before.player.life,binding=before.binding}\n"
    def action(name, parameters):
        nonlocal source
        fields = ','.join(f'{key}={value}' for key, value in parameters.items())
        source += (f"local a=api('{name}', {{{fields}}})\n"
                   "assert(a.valid and a.completed, a.reason or a.status)\n"
                   "local e=a.evidence or {}\nevidence.actions[#evidence.actions+1]={operation=a.operation,actionId=a.actionId,status=a.status,endEvidence=a.endEvidence,"
                   "x=e.x,y=e.y,arrived=e.arrived,cycleVerified=e.cycleVerified,consumptionObserved=e.consumptionObserved,"
                   "animationPeak=e.AnimationPeak,starts=e.Starts,strikes=e.Strikes,peakDamage=e.PeakDamage,"
                   "destroyedByActor=e.destroyedByActor,targetAbsent=e.targetAbsent,effectStatus=e.effectStatus}\n")
    if args.target_x is not None:
        action('navigate_to_x', dict(targetX=args.target_x))
    for key in (args.use_key, args.consume_key):
        if key is not None:
            action('select_hotbar_slot', dict(slot=key))
            action('use_item', {})
    if args.mine_x is not None:
        if args.pick_key is not None:
            action('select_hotbar_slot', dict(slot=args.pick_key))
        action('mine_tile', dict(x=args.mine_x, y=args.mine_y))
    source += "local after=api('get_player_state', {})\nevidence.after={position=after.player.position,life=after.player.life,binding=after.binding}\nreturn evidence"
    bridge = ServerAiBridge(port=args.port)
    try:
        return await LuaRuntime(bridge, evidence_root=ROOT / '.local/server-ai-action-evidence').execute(
            source, limits=Limits(seconds=55, calls=24, actions=10))
    finally:
        bridge.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=18779)
    parser.add_argument('--target-x', type=float, help='Player left edge in world pixels; one tile = 16 pixels')
    parser.add_argument('--use-key', type=int, choices=range(10), help='explicit hotbar game key, then use once')
    parser.add_argument('--consume-key', type=int, choices=range(10), help='optional second explicit selection/use')
    parser.add_argument('--mine-x', type=int)
    parser.add_argument('--mine-y', type=int)
    parser.add_argument('--pick-key', type=int, choices=range(10), help='explicit selection before mining; omitted uses current item')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if (args.mine_x is None) != (args.mine_y is None):
        parser.error('--mine-x and --mine-y must be given together')
    if args.target_x is not None and not 0 <= args.target_x <= 1_000_000:
        parser.error('--target-x must be finite and within 0..1000000')
    result = asyncio.run(verify(args))
    output = args.output or ROOT / ('.local/server-ai-actions-' + uuid.uuid4().hex + '.json')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=True, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(status=result['status'], actions=result['actions'], actionState=result['actionState'],
                          failure=result.get('failure'), output=str(output)), ensure_ascii=True, indent=2))
    return 0 if result['status'] == 'completed' and result['actionState'] != 'unknown' else 1


if __name__ == '__main__':
    raise SystemExit(main())
