-- Read-only smoke program: safe to run without selecting or using an item.
local state = api('get_player_state')
if not state.valid then return {ready=false} end
local inventory = api('inspect_inventory', {mode='hotbar'})
return {player=state.player, motion=state.motion, inventory=inventory}
