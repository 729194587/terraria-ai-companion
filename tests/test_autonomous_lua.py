"""Model-style inline program through Agent/HTTP substitute/Worker/real loopback IPC.

Synthetic game effects only. No external model, native physics or fixed task helper.
"""
import json
import unittest
import test_server_ai_agent as server_fixture

SOURCE = '''
local function done(r)
    return r.valid and r.completed and r.endEvidenceStatus=='known'
end
local function read(name,args)
    local r=api(name,args)
    assert(r.valid and r.available~=false, 'observation unavailable')
    return r
end
local state=read('get_player_state',{})
local inventory=read('inspect_inventory',{mode='hotbar'})
local key=nil
for _,item in ipairs(inventory.data.inventory) do
    if not item.empty and item.use.pick>0 then key=item.hotbarKey; break end
end
if key==nil then return {reason='no_tool'} end
local before=read('inspect_inventory',{mode='item',item='Terraria/CopperOre'})
local visited={}
local steps=json.array()
for step=1,2 do
    local world=sense_world({trees=false,oreNames={'Copper'},drops=false,maxObjects=4})
    local candidates={}
    for _,o in ipairs(world.objects) do
        local c=o.candidate
        local id=tostring(c.x)..':'..tostring(c.y)
        if c.status=='observed_present' and not visited[id] then
            local dx=c.x*16+8-(state.player.position.x+10)
            local dy=c.y*16+8-(state.player.position.y+21)
            table.insert(candidates,{id=id,x=c.x,y=c.y,typeId=o.typeId,distance=math.sqrt(dx*dx+dy*dy)})
        end
    end
    table.sort(candidates,function(a,b) return a.distance<b.distance end)
    if #candidates==0 then return {reason='no_fresh_target',steps=steps} end
    local target=candidates[1]
    visited[target.id]=true
    local move=api('navigate_to_x',{targetX=target.x*16-20})
    if not done(move) then return {reason='navigation_stopped',action=move} end
    state=read('get_player_state',{})
    if math.abs(state.player.position.x-(target.x*16-20))>16 then return {reason='no_progress'} end
    local fresh=read('inspect_tile',{x=target.x,y=target.y})
    if fresh.data.cell.knowledge~='read' or not fresh.data.cell.hasTile or fresh.data.cell.tileType~=target.typeId then return {reason='target_changed'} end
    local selected=api('select_hotbar_slot',{slot=key})
    if not done(selected) then return {reason='selection_stopped',action=selected} end
    local mined=api('mine_tile',{x=target.x,y=target.y})
    if not done(mined) then return {reason='mining_stopped',action=mined} end
    local check=read('inspect_tile',{x=target.x,y=target.y})
    table.insert(steps,{x=target.x,y=target.y,absent=check.data.cell.knowledge=='read' and not check.data.cell.hasTile,
        destroyedByActor=mined.evidence.destroyedByActor,inputsReleased=mined.endEvidence.inputsReleased})
    state=read('get_player_state',{})
end
local after=read('inspect_inventory',{mode='item',item='Terraria/CopperOre'})
return {reason='step_limit',steps=steps,inventoryDelta=after.data.count-before.data.count,visited=visited}
'''


class OrchestrationHost(server_fixture.AiHost):
    def __init__(self):
        super().__init__()
        self.perception=self.actions=True
        self.x=1600; self.tick=0; self.count=0
        self.tiles=[(108,198),(104,198),(100,190)]
        self.applied=set(); self.changed_on_arrival=False; self.inventory_gain=True

    def query(self,operation,args):
        if self.action and self.action['status']=='completed' and self.action['actionId'] not in self.applied:
            self.applied.add(self.action['actionId'])
            request=next(r for r in self.sent if r['op']=='start' and r['actionId']==self.action['actionId'])
            a=request['arguments']
            if request['operation']=='navigate_to_x':
                self.x=a['targetX']
                if self.changed_on_arrival: self.tiles=[]
            elif request['operation']=='mine_tile' and self.mode=='complete':
                self.tiles.remove((a['x'],a['y']))
                if self.inventory_gain: self.count+=1
        base=super().query(operation,args)
        self.tick+=1
        base.update(gameUpdate=self.tick,observedAtUnixMs=1793080000000+self.tick,clock='server_utc_at_projection')
        if operation=='get_player_state': base['data']['player']['position']=dict(x=self.x,y=3158)
        elif operation=='resolve_content': base['data']=dict(status='ok',truncated=False,candidates=[dict(typeId=7)])
        elif operation=='observe_terrain': base['data']=dict(bounds=dict(minX=78,maxX=122,minY=180,maxY=208),complete=True)
        elif operation=='scan_tiles':
            base['data']=dict(items=[dict(x=x,y=y,typeId=7,contentKey='Terraria/Copper',distancePixels=abs(x*16-self.x)) for x,y in self.tiles],
                complete=True,truncated=False,unknownCount=0)
        elif operation=='inspect_tile': base['data']=dict(cell=dict(knowledge='read',hasTile=(args['x'],args['y']) in self.tiles,tileType=7))
        elif operation=='inspect_inventory' and args.get('mode')=='item': base['data']['count']=self.count
        return base


class AutonomousLuaTests(unittest.IsolatedAsyncioTestCase):
    host_type=OrchestrationHost
    program=server_fixture.ServerAiAgentTests.program
    receive=server_fixture.ServerAiAgentTests.receive
    asyncSetUp=server_fixture.ServerAiAgentTests.asyncSetUp
    asyncTearDown=server_fixture.ServerAiAgentTests.asyncTearDown

    async def execute_program(self):
        self.answers=[self.program(SOURCE),dict(role='assistant',content='已按回执停止；目标结果见实际观察。')]
        await self.receive('autonomous','采集附近铜矿，最多处理两个目标，核对实际结果')
        await self.agent.task
        self.assertEqual(2,len(self.requests))
        return json.loads(next(m['content'] for m in self.requests[-1]['messages'] if m['role']=='tool'))

    def starts(self): return [r for r in self.aihost.sent if r['op']=='start']

    async def test_dynamic_selection_reobserve_navigate_mine_verify(self):
        r=await self.execute_program()
        self.assertTrue(r['programCompleted'],r)
        self.assertEqual([104,108],[s['x'] for s in r['value']['steps']])
        self.assertTrue(all(s['absent'] and s['destroyedByActor'] and s['inputsReleased'] for s in r['value']['steps']))
        self.assertEqual(2,r['value']['inventoryDelta'])
        self.assertEqual(6,r['actions']); self.assertLessEqual(r['calls'],40)
        self.assertEqual('unknown',r['taskOutcome'])
        self.assertNotIn('explore(',SOURCE)
        self.assertEqual(2,sum(q.get('operation')=='resolve_content' for q in self.aihost.sent))

    async def test_same_program_selects_different_observed_target(self):
        self.aihost.tiles=[(96,198),(88,198)]
        r=await self.execute_program()
        self.assertEqual([96,88],[s['x'] for s in r['value']['steps']],r)

    async def test_changed_target_stops_before_select_or_mine(self):
        self.aihost.changed_on_arrival=True
        r=await self.execute_program()
        self.assertEqual('target_changed',r['value']['reason'],r)
        self.assertEqual(['navigate_to_x'],[s['operation'] for s in self.starts()])

    async def test_empty_observation_starts_no_action(self):
        self.aihost.tiles=[]
        r=await self.execute_program()
        self.assertEqual('no_fresh_target',r['value']['reason'],r)
        self.assertEqual([],self.starts())

    async def test_inventory_no_gain_is_not_invented_from_mining(self):
        self.aihost.inventory_gain=False
        r=await self.execute_program()
        self.assertEqual(0,r['value']['inventoryDelta'],r)
        self.assertEqual('unknown',r['taskOutcome'])

    async def test_known_failure_never_starts_following_action(self):
        self.aihost.mode='failed'
        r=await self.execute_program()
        self.assertEqual(1,len(self.starts()),r)
        self.assertFalse(r['actionSuccess'])
        self.assertEqual('navigation_stopped',r['value']['reason'])

    async def test_unknown_release_aborts_program_and_fences_bridge(self):
        self.aihost.mode='no_release'
        r=await self.execute_program()
        self.assertEqual(1,len(self.starts()),r)
        self.assertEqual('unknown',r['actionState'])
        self.assertFalse(r['programCompleted'])
        self.assertTrue(self.ai._lua_poisoned)


if __name__=='__main__': unittest.main()
