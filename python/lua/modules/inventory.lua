local function read(name, args)
    local r, budget = api(name, args)
    if r.valid and r.available then return r.data, budget end
end

local function availableStacks(found)
    local stacks, slots, held, budget = {}, {}, nil, nil
    for index, row in ipairs(found.slots) do
        if row.slot < 50 then slots[#slots+1] = row.slot end
        if #slots == 16 or (#slots > 0 and index == #found.slots) then
            local page
            page, budget = read("inspect_inventory", {mode="slots", slots=slots})
            if not page then return nil end
            for _, s in ipairs(page.inventory) do
                if s.typeId == found.typeId and not s.favorited and s.stack > 0 then
                    if s.slot == page.selectedSlot then held = s
                    else stacks[#stacks+1] = s end
                end
            end
            slots = {}
        end
    end
    local first = held or stacks[1]
    local matching, total = {held}, held and held.stack or 0
    for _, s in ipairs(stacks) do
        if s.prefix == first.prefix then
            matching[#matching+1] = s
            total = total + s.stack
        end
    end
    return matching, total, budget
end

local function give(teammateRef, contentKey, count)
    assert(type(teammateRef) == "string" and #teammateRef == 32, "expected a teammateRef from list_teammates")
    assert(type(contentKey) == "string" and #contentKey > 0, "expected an item content key")
    assert(type(count) == "number" and math.tointeger(count) and count >= 1 and count <= 9999, "count must be 1..9999")
    local result = {teammateRef=teammateRef, requested=count, dropped=0, approach="none", drops=json.array()}
    local function stop(reason, status)
        result.status = status == "unknown" and "unknown" or (result.dropped > 0 and "partial" or "unavailable")
        result.reason = reason
        return result
    end
    local team = read("list_teammates", {})
    local target
    for _, player in ipairs(team and team.players or {}) do
        if player.teammateRef == teammateRef then target = player.handoff end
    end
    if not target or target == json.null then return stop("teammate_unavailable") end

    local found = read("inspect_inventory", {mode="item", item=contentKey})
    if not found or found.status ~= "ok" then return stop(found and found.status or "inventory_unavailable") end
    local coins = {["Terraria/CopperCoin"]=true, ["Terraria/SilverCoin"]=true,
                  ["Terraria/GoldCoin"]=true, ["Terraria/PlatinumCoin"]=true}
    if string.sub(found.contentKey, 1, 9) ~= "Terraria/" or coins[found.contentKey] then return stop("unsupported_drop_item") end
    local stacks, total, budget = availableStacks(found)
    if not stacks then return stop("inventory_unavailable") end
    if total == 0 then return stop("no_available_stack") end
    if stacks[1].maxStack <= 1 then return stop("unsupported_drop_item") end

    local potionCargo = found.contentKey == "Terraria/WormholePotion"
    local move
    if team.hasWormholePotion and (not potionCargo or total > count) then
        result.approach = "wormhole"
        move, budget = api("teleport_to_teammate", {teammateRef=teammateRef})
    elseif not target.inRange then
        result.approach = "walk"
        move, budget = api("navigate_to", {x=target.x, y=target.y})
    end
    if move and not move.completed then return stop(move.reason, move.status) end
    if result.approach == "wormhole" and potionCargo then
        stacks, total, budget = availableStacks(found)
        if not stacks then return stop("inventory_changed") end
    end
    for _, s in ipairs(stacks) do
        if budget.actions < 1 or budget.calls < 1 then return stop("insufficient_budget") end
        if type(s.inventoryToken) ~= "string" then return stop("inventory_changed") end
        local amount = math.min(s.stack, count - result.dropped)
        local drop
        drop, budget = api("drop_inventory_item", {sourceSlot=s.slot, sourceToken=s.inventoryToken,
            count=amount, teammateRef=teammateRef, nearbyOnly=true})
        if not drop.completed then return stop(drop.reason, drop.status) end
        local e = drop.evidence
        result.dropped = result.dropped + e.spawnedCount
        result.entityRef = e.entityRef
        result.drops[#result.drops+1] = {entityRef=e.entityRef, spawnedCount=e.spawnedCount}
        if result.dropped == count then
            result.status = "completed"
            return result
        end
    end
    return stop("insufficient_items")
end
return {give=give}
