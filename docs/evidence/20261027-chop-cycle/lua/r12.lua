-- D：铜镐挖 AI 右侧两格的地面
local st = api('get_player_state', {})
local px, py = st.player.position.x, st.player.position.y
local tx = math.floor(px / 16)
local footTy = math.floor((py + 42) / 16)
local gx, gy = tx + 2, footTy
local cell = api('inspect_tile', {x = gx, y = gy})
local sel = api('select_hotbar_slot', {slot = 2})
local mine = api('mine_tile', {x = gx, y = gy})
local cell2 = api('inspect_tile', {x = gx, y = gy})
local inv = api('inspect_inventory', {mode = 'full'})
local items = {}
for i = 1, #inv.data.inventory do
  local s = inv.data.inventory[i]
  if s and not s.empty then items[#items + 1] = {slot = s.slot, key = s.contentKey, stack = s.stack} end
end
return {
  target = {x = gx, y = gy}, before = {hasTile = cell.data.cell.hasTile, content = cell.data.content},
  selected = sel and {completed = sel.completed, reason = sel.reason},
  mine = {valid = mine.valid, completed = mine.completed, status = mine.status, reason = mine.reason,
    evidence = mine.evidence, endEvidenceStatus = mine.endEvidenceStatus},
  after = {hasTile = cell2.data.cell.hasTile, content = cell2.data.content},
  items = items,
}
