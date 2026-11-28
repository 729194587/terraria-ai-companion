-- Executed by the existing LuaRuntime; testKey is nil for read-only acceptance.
local before = api('get_player_state', {})
assert(before.valid and before.player.inWorld)
assert(before.player.source == 'server_ai_player')
local world
if testWorld then
    local observed = api('get_world_state', {})
    assert(observed.valid and observed.binding.world == before.binding.world)
    world = observed.data
    local e = world.environment
    assert(e.source == 'server_world' and type(e.dayTime) == 'boolean')
    assert(type(e.raining) == 'boolean' and type(e.bloodMoon) == 'boolean')
    assert(type(world.progression.hardMode) == 'boolean' and type(world.depth.tileY) == 'number')
    local minutes = (math.floor(e.time / 60) + (e.dayTime and 270 or 1170)) % 1440
    assert(#e.timeOfDay == 5 and string.sub(e.timeOfDay, 3, 3) == ':')
    assert(tonumber(string.sub(e.timeOfDay, 1, 2)) == math.floor(minutes / 60))
    assert(tonumber(string.sub(e.timeOfDay, 4, 5)) == minutes % 60)
end
local selected = api('inspect_inventory', {mode='selected'})
local hotbar = api('inspect_inventory', {mode='hotbar'})
local slots = api('inspect_inventory', {mode='slots', slots={0,9,50,58}})
local full = api('inspect_inventory', {mode='full'})
local item = api('inspect_inventory', {mode='item', item='Terraria/CopperPickaxe'})
assert(selected.valid and hotbar.valid and slots.valid and full.valid and item.valid)
assert(#hotbar.data.inventory == 10 and #full.data.inventory == 59)
assert(slots.data.inventory[1].slot == 0 and slots.data.inventory[4].slot == 58)
assert(selected.data.inventory[1].slot == before.player.selectedSlot)
assert(selected.data.inventoryTokenStatus == 'unsupported_server_ai_inventory_mutation' or #selected.data.inventory[1].inventoryToken == 64)
local occupied = {}
for _, entry in ipairs(full.data.inventory) do
    if entry.empty == false then occupied[#occupied + 1] = entry end
end
local action = nil
if testKey ~= nil then
    action = api('select_hotbar_slot', {slot=testKey})
    assert(action.valid and action.completed and action.endEvidenceStatus == 'known')
end
local after = api('get_player_state', {})
local selectedAfter = api('inspect_inventory', {mode='selected'})
assert(after.valid and selectedAfter.valid)
assert(before.binding.actor == after.binding.actor and before.binding.world == after.binding.world)
assert(before.binding.generation == after.binding.generation)
assert(selectedAfter.data.selectedSlot == after.player.selectedSlot)
if testKey ~= nil then
    assert(after.player.selectedSlot == (testKey + 9) % 10)
    assert(selectedAfter.data.selectedHotbarKey == testKey)
else
    assert(before.player.selectedSlot == after.player.selectedSlot)
end
return {
    binding=after.binding, worldSessionId=after.worldSessionId, gameUpdate=after.gameUpdate,
    before=before.player, after=after.player, motion=after.motion, world=world,
    selected=selectedAfter.data, hotbarCount=#hotbar.data.inventory,
    fullInventorySlots=#full.data.inventory, equipmentSlots=#full.data.equipment,
    inventoryAtFullQuery=occupied, occupiedInventorySlots=#occupied, inventoryGameUpdate=full.gameUpdate,
    explicitSlots={slots.data.inventory[1].slot,slots.data.inventory[2].slot,slots.data.inventory[3].slot,slots.data.inventory[4].slot},
    itemLookup=item.data, selection=action,
    customState=full.data.customState, inventoryTokenStatus=selectedAfter.data.inventoryTokenStatus
}
