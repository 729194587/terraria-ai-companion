"""Isolated native navigation profile refusal -> fresh scene -> bounded direct input."""
import argparse
import asyncio
import json
from pathlib import Path
import sys

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
from native_direct_input import wait_file

SOURCE='''
local before=api('get_player_state',{})
local goal=before.player.position.x+32
local navigation=api('navigate_to_x',{targetX=goal})
if navigation.completed then return {navigation=navigation,alreadyArrived=true} end
assert(navigation.status=='failed' and navigation.continuationAllowed)
local scene=api('observe_scene',{layers={'geometry','hazards'}})
if scene.data.actor.dead or not scene.data.coverage.geometryComplete then return {stopped=true} end
local position=scene.data.actor.boundsWorldPixels
local direction=position.x<goal and 'right' or 'left'
local movement=api('move_input',{horizontal=direction,vertical='none',jumpHoldUpdates=8,maxUpdates=40})
local after=api('get_player_state',{})
return {navigation=navigation,movement=movement,startX=position.x,goalX=goal,
    finalX=after.player.position.x,moved=movement.completed and after.player.position.x>=goal}
'''


async def verify(args):
    profile=args.profile
    await wait_file(profile/'action-fixture.json',90)
    ready=profile/'direct-input-ready.json'
    temp=profile/'direct-input-request.tmp';temp.write_text(json.dumps(dict(scene='profile_refused')))
    temp.replace(profile/'direct-input-request.json')
    fixture=await wait_file(ready)
    bridge=ServerAiBridge(args.port)
    calls=[]
    try:
        result=await LuaRuntime(bridge,evidence_root=profile/'fallback-lua').execute(SOURCE,
            limits=Limits(seconds=15,calls=5,actions=2),on_result=lambda n,a,r:calls.append(dict(operation=n,args=a,result=r)))
        report=dict(fixture=fixture,source=SOURCE,result=result,calls=calls)
        (profile/'navigation-fallback-result.json').write_text(json.dumps(report,indent=2))
        assert result['status']=='completed',result
        value=result['value'];nav=value['navigation'];move=value['movement']
        assert nav['status']=='failed' and nav['continuationAllowed'] and nav['evidence']['failureKind']=='profile_refused',nav
        assert nav['endEvidence']['inputsReleased'] and nav['evidence']['settled'],nav
        assert value['moved'] and value['finalX']>=value['goalX'],value
        assert move['evidence']['updatesExecuted']==40 and move['endEvidence']['inputsReleased'],move
        assert [c['operation'] for c in calls]==['get_player_state','navigate_to_x','observe_scene','move_input','get_player_state'],calls
        assert result['actions']==2 and result['calls']==5,result
        print(json.dumps(dict(passed=True,fromX=value['startX'],goalX=value['goalX'],finalX=value['finalX'],navigation=nav['reasonDetail'],updates=40)),flush=True)
    finally:bridge.close()


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--port',type=int,required=True);p.add_argument('--profile',type=Path,required=True)
    asyncio.run(verify(p.parse_args()))
