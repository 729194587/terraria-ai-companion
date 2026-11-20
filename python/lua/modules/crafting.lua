local function make(resultType, desiredCount, options)
    options = options or {}
    assert(type(resultType) == "number" and math.tointeger(resultType) and resultType > 0, "expected positive item type")
    assert(type(desiredCount) == "number" and math.tointeger(desiredCount) and desiredCount > 0, "expected positive desired count")
    local limit = options.maxBatches or 8
    assert(math.tointeger(limit) and limit >= 1 and limit <= 32, "maxBatches must be 1..32")
    local made, batches = 0, 0
    local function report(status, reason, receipt, recipes)
        return {status=status, reason=reason, resultType=resultType, producedCount=made,
                remaining=math.max(0, desiredCount-made), batches=batches, receipt=receipt, recipes=recipes}
    end
    local last
    while batches < limit do
        local offset = 0
        local recipe, observation
        repeat
            observation = api("query_recipes", {resultType=resultType, availableOnly=false, limit=24, offset=offset})
            if not observation.valid or not observation.available then
                return report("unknown", "recipe_observation_unavailable", last, observation)
            end
            local page = observation.data
            for _, row in ipairs(page.items) do
                if row.output.typeId == resultType and row.supported and row.available and row.hasOutputSpace then
                    recipe = row
                    break
                end
            end
            if not recipe and (not page.truncated or #page.items == 0) then
                return report(made > 0 and "partial" or "unavailable", "no_available_recipe", last, observation)
            end
            offset = offset + #page.items
        until recipe
        local yieldCount = recipe.output.count
        assert(math.tointeger(yieldCount) and yieldCount > 0, "invalid recipe output")
        local count = math.min(20, math.ceil((desiredCount-made)/yieldCount))
        last = api("craft_recipe", {recipeId=recipe.recipeId, recipeToken=recipe.recipeToken, crafts=count})
        batches = batches + 1
        local e = last.evidence
        local consistent = e and e.recipeId == recipe.recipeId and e.recipeToken == recipe.recipeToken
            and e.outputTypeId == resultType and e.outputCountPerCraft == yieldCount and e.requestedCrafts == count
            and math.tointeger(e.completedCrafts) and e.completedCrafts >= 0 and e.completedCrafts <= count
            and e.producedCount == e.completedCrafts * yieldCount
        if not last.valid or last.status == "unknown" then return report("unknown", last.reason, last) end
        if not last.completed then
            -- The native completed counter advances only after the inventory delta is verified.
            if consistent then made = made + e.producedCount end
            return report(made > 0 and "partial" or last.status, last.reason, last)
        end
        if not consistent or not e.inventoryVerified or e.completedCrafts ~= count then
            return report("unknown", "craft_evidence_mismatch", last)
        end
        made = made + e.producedCount
        if made >= desiredCount then return report("completed", nil, last) end
    end
    return report("partial", "batch_limit", last)
end
return {make=make}
