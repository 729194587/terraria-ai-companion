local before = api('observe_scene', {layers={'objects'}})
for _, object in ipairs(before.data.objects.items) do
    if object.kind == 'ore_component' and object.target and object.target.typeId == 7 then
        local mining = api('mine_target', {target=object.target, cluster=true})
        local after = api('observe_scene', {layers={'objects'}})
        return {mining=mining, observedAfter=after.available}
    end
end
error('No observed copper target')
