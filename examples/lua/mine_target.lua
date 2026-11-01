-- Pass the observed target unchanged; the server owns coordinates and approach.
local scene = api('observe_scene', {layers={'objects'}})
for _, ore in ipairs(scene.data.objects.items) do
    if ore.kind == 'ore_component' and ore.contentKey == 'Terraria/Copper' and ore.target then
        return api('mine_target', {target=ore.target})
    end
end
return {status='no_returned_copper_target', truncated=scene.data.objects.truncated}
