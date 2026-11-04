-- 检查新版本：树是否还在、背包字段是否精简
local scan = api('scan_tiles', {minX = 2090, minY = 200, maxX = 2105, maxY = 215,
  tileTypes = {5}, maxResults = 24})
local inv = api('inspect_inventory', {mode = 'full'})
local d = inv.data
local items = {}
local empty, sampleEmpty = 0, nil
for i = 1, #d.inventory do
  local s = d.inventory[i]
  if s and not s.empty then
    items[#items + 1] = {slot = s.slot, key = s.contentKey, stack = s.stack, name = s.name}
  else
    empty = empty + 1
    if sampleEmpty == nil then sampleEmpty = s end
  end
end
local keys = {}
for k, _ in pairs(d) do keys[#keys + 1] = k end
return {treeCount = scan.data.matchedCount, trees = scan.data.items,
  items = items, emptySlots = empty, sampleEmpty = sampleEmpty,
  inventoryKeys = keys, equipmentSlots = #d.equipment, buffs = #(d.buffs or {})}
