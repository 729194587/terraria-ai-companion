-- A：走近后用铜斧砍树根
local nav = api('navigate_to_x', {targetX = 33560})
local sel = api('select_hotbar_slot', {slot = 3})
local mine = api('mine_tile', {x = 2103, y = 210})
local use = api('use_item', {})
local cell = api('inspect_tile', {x = 2103, y = 210})
local inv = api('inspect_inventory', {mode = 'full'})
local items = {}
for i = 1, #inv.data.inventory do
  local s = inv.data.inventory[i]
  if s and not s.empty then items[#items + 1] = {slot = s.slot, key = s.contentKey, stack = s.stack} end
end
return {
  nav = nav and {completed = nav.completed, reason = nav.reason,
    evidence = nav.evidence and {x = nav.evidence.x, targetX = nav.evidence.targetX}},
  selected = sel and {completed = sel.completed, reason = sel.reason},
  mine = {valid = mine.valid, completed = mine.completed, status = mine.status, reason = mine.reason,
    evidence = mine.evidence, endEvidenceStatus = mine.endEvidenceStatus},
  use = {valid = use.valid, completed = use.completed, status = use.status, reason = use.reason,
    evidence = use.evidence, endEvidenceStatus = use.endEvidenceStatus},
  cellNow = {hasTile = cell.data.cell.hasTile, content = cell.data.content},
  items = items,
}
