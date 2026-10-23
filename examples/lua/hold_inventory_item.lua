-- Single player. Run only when the user has authorized moving this item to
-- the chosen hotbar key; any previous destination item returns to the source.
local wanted = 'Terraria/CopperPickaxe'
local hotbarKey = 0
local targetSlot = hotbarKey == 0 and 9 or hotbarKey - 1
local found = api('inspect_inventory', {mode='item', item=wanted})
if not found.valid or found.data.status ~= 'ok' or #found.data.slots == 0 then
    return {reason='item_not_resolved_or_not_owned', observation=found}
end
local sourceSlot = found.data.slots[1].slot
if sourceSlot > 49 then return {reason='source_outside_normal_inventory'} end
local indices = {sourceSlot}
if sourceSlot ~= targetSlot then indices[2] = targetSlot end
local slots = api('inspect_inventory', {mode='slots', slots=indices})
if not slots.valid or slots.data.status ~= 'ok' then return slots end
local source = slots.data.inventory[1]
local destination = sourceSlot == targetSlot and source or slots.data.inventory[2]
if source.empty or source.typeId ~= found.data.typeId then
    return {reason='source_changed', observation=slots}
end
local action = api('hold_inventory_item', {sourceSlot=sourceSlot, hotbarKey=hotbarKey,
    sourceToken=source.inventoryToken, destinationToken=destination.inventoryToken})
if not action.valid or not action.completed or action.endEvidenceStatus ~= 'known' then
    return {action=action}
end
return {action=action, after=api('inspect_inventory', {mode='selected'})}
