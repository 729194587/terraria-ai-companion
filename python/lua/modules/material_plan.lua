local function plan(typeId, count)
    return api("plan_materials", {typeId=typeId, count=count})
end

return {plan=plan}
