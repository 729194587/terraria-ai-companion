-- Changes the selected hotbar key, then observes its real result.
local before = api('inspect_inventory', {mode='selected'})
if not before.valid then return {ready=false, observation=before} end
local selected = api('select_hotbar_slot', {slot=1})
if not selected.valid or not selected.completed then
    return {selected=selected}
end
local after = api('inspect_inventory', {mode='selected'})
return {before=before, selected=selected, after=after}
