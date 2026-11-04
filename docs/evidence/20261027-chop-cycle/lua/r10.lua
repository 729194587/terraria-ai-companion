-- 走到木头掉落点，看原生拾取是否归 AI
local nav = api('navigate_to_x', {targetX = 33668})
local mid = api('get_player_state', {})
local inv = api('inspect_inventory', {mode = 'full'})
local items = {}
for i = 1, #inv.data.inventory do
  local s = inv.data.inventory[i]
  if s and not s.empty then items[#items + 1] = {slot = s.slot, key = s.contentKey, stack = s.stack} end
end
local ents = api('scan_entities', {category = 'item', radius = 200, maxResults = 8})
local left = {}
for i = 1, #ents.data.items do
  local it = ents.data.items[i]
  left[#left + 1] = {key = it.details and it.details.contentKey, x = it.centerWorldPixels.x, d = it.distancePixels}
end
return {
  nav = nav and {completed = nav.completed, evidence = nav.evidence and {x = nav.evidence.x, targetX = nav.evidence.targetX}},
  position = mid.player.position, items = items, nearbyItems = left,
}
