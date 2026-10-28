-- 往东分段走 60 格，每段记录落点、地形与附近实体
local st = api('get_player_state', {})
local startX = st.player.position.x
local segPx = 160
local out = {}
for i = 1, 6 do
  local target = startX + segPx * i
  local nav = api('navigate_to_x', {targetX = target})
  local st2 = api('get_player_state', {})
  local terr = api('observe_terrain', {})
  local ents = api('scan_entities', {category = 'npc', radius = 500, maxResults = 6})
  local nearby = {}
  for j = 1, #ents.data.items do
    local it = ents.data.items[j]
    nearby[#nearby + 1] = {name = it.name, kind = it.classification, d = it.distancePixels}
  end
  out[#out + 1] = {
    target = target,
    x = st2.player.position.x,
    y = st2.player.position.y,
    completed = nav.completed,
    reason = nav.reason,
    updates = nav.evidence and nav.evidence.updates,
    jumps = nav.evidence and nav.evidence.navigation and nav.evidence.navigation.CompletedJumps,
    grounded = terr.data.terrain.grounded,
    rows = terr.data.terrain.rows,
    nearby = nearby,
  }
  if not nav.completed then break end
end
return {startX = startX, segments = out}
