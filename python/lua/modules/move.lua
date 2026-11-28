local function follow(teammateRef, options)
    options = options or {}
    return api("submit_task", {operation="follow_teammate", arguments={
        teammateRef=teammateRef, distanceTiles=options.distanceTiles, durationSeconds=options.durationSeconds}})
end

return {follow=follow}
