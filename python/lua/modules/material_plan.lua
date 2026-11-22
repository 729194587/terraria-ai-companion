local pick = "Terraria/MoltenPickaxe"
local bar = "Terraria/HellstoneBar"
local ore = "Terraria/Hellstone"
local obsidian = "Terraria/Obsidian"

local function read(name, args)
    local result = api(name, args)
    if result.valid and result.available then return result.data end
end

local function failure(status, reason, key)
    return {status=status, reason=reason, contentKey=key}
end

local function recipe_for(key, ingredients)
    local found = read("resolve_content", {kind="item", query=key})
    if not found or found.status ~= "ok" or found.truncated or #found.candidates ~= 1
        or found.candidates[1].contentKey ~= key then
        return nil, failure("unknown", "item_resolution", key)
    end
    local id = found.candidates[1].typeId
    local offset, selected = 0, nil
    for pageNumber = 1, 3 do
        local page = read("query_recipes", {resultType=id, availableOnly=false, offset=offset, limit=24})
        if not page or page.offset ~= offset then return nil, failure("unknown", "recipe_page", key) end
        for _, row in ipairs(page.items) do
            if selected then return nil, failure("unsupported", "multiple_recipes", key) end
            selected = row
        end
        if not page.truncated then break end
        if #page.items == 0 or pageNumber == 3 then return nil, failure("unknown", "incomplete_pages", key) end
        offset = offset + #page.items
    end
    if not selected then return nil, failure("unknown", "no_recipe", key) end
    if not selected.supported then return nil, failure("unsupported", selected.reason, key) end
    local amounts = {}
    for _, entry in ipairs(selected.ingredients) do
        local item = entry.item
        if not ingredients[item.contentKey] or amounts[item.contentKey] or #entry.acceptedGroups > 0
            or not math.tointeger(item.count) or item.count < 1 then
            return nil, failure("unsupported", "ingredient_shape", key)
        end
        amounts[item.contentKey] = item.count
    end
    for required in pairs(ingredients) do
        if not amounts[required] then return nil, failure("unsupported", "ingredient_shape", key) end
    end
    local out = selected.output
    if out.typeId ~= id or out.contentKey ~= key or not math.tointeger(out.count) or out.count < 1 then
        return nil, failure("unknown", "recipe_output", key)
    end
    return {yieldCount=out.count, amounts=amounts, stations=selected.stations, conditions=selected.conditions,
            available=selected.available, reason=selected.reason, hasOutputSpace=selected.hasOutputSpace}
end

local function requirements(recipes, held)
    local barsNeeded = recipes[pick].amounts[bar] * math.ceil(1 / recipes[pick].yieldCount)
    local barDeficit = math.max(0, barsNeeded - held[bar])
    local batches = math.ceil(barDeficit / recipes[bar].yieldCount)
    local deficits = {}
    for key, perBatch in pairs(recipes[bar].amounts) do
        deficits[key] = math.max(0, batches * perBatch - held[key])
    end
    return {status="planned", inventory=held, recipes=recipes, barDeficit=barDeficit, rawDeficits=deficits}
end

local function molten_pickaxe()
    local pickRecipe, err = recipe_for(pick, {[bar]=true})
    if not pickRecipe then return err end
    local barRecipe, barError = recipe_for(bar, {[ore]=true, [obsidian]=true})
    if not barRecipe then return barError end
    local held = {}
    for _, key in ipairs({pick, bar, ore, obsidian}) do
        local bag = read("inspect_inventory", {mode="item", item=key})
        if not bag or bag.status ~= "ok" or bag.contentKey ~= key or type(bag.slots) ~= "table" then
            return failure("unknown", "inventory_unavailable", key)
        end
        local count = 0
        for _, slot in ipairs(bag.slots) do
            -- ActorCrafting allocates ingredients and verifies outputs in slots 0..57.
            if slot.slot >= 0 and slot.slot < 58 then count = count + slot.stack end
        end
        held[key] = count
    end
    return requirements({[pick]=pickRecipe, [bar]=barRecipe}, held)
end

return {molten_pickaxe=molten_pickaxe}
