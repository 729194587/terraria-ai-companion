-- 扫描掉落的物品，找木头
local ents = api('scan_entities', {category = 'item', radius = 600, maxResults = 20})
local list = {}
local d = ents.data
for i = 1, #d.items do
  local it = d.items[i]
  list[#list + 1] = {key = it.details and it.details.contentKey, name = it.name,
    x = it.centerWorldPixels.x, y = it.centerWorldPixels.y,
    stack = it.details and it.details.stack, d = it.distancePixels}
end
local after = api('get_player_state', {})
return {count = d.returnedCount, items = list, position = after.player.position}
