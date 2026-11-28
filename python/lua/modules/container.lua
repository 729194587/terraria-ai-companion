local function observe(name, args)
    local r, budget = api(name, args)
    if r.valid and r.available then return r.data, budget end
    return nil, budget
end

local function transfer(direction, target, contentKey, count)
    assert(type(target) == "table", "expected chest Tile coordinates {x,y}")
    for _, v in ipairs({"x","y"}) do
        local n = target[v]
        assert(type(n) == "number" and math.tointeger(n) and n >= 0 and n <= 100000, "invalid chest Tile coordinate")
    end
    assert(type(contentKey) == "string" and #contentKey > 0, "expected an item content key")
    assert(type(count) == "number" and math.tointeger(count) and count > 0, "expected a positive integer count")
    local report = {requested=count, moved=0, remaining=count}
    local function stop(reason, status)
        report.reason = reason
        report.status = status or (report.moved > 0 and "partial" or "unavailable")
        return report
    end
    local item = observe("inspect_inventory", {mode="item", item=contentKey})
    if not item or item.status ~= "ok" then return stop(item and item.status or "inventory_unavailable") end
    local tile = observe("inspect_tile", {x=target.x, y=target.y})
    if not tile or not tile.cell or tile.cell.knowledge ~= "read" then return stop("tile_unavailable") end
    local cell = tile.cell
    if not cell.hasTile or cell.actuated or (cell.tileType ~= 21 and cell.tileType ~= 467) then
        return stop("unsupported_container")
    end
    -- Normalize any of the four cells of an ordinary Terraria chest to its anchor.
    local x = target.x - math.floor(cell.frameX / 18) % 2
    local y = target.y - math.floor(cell.frameY / 18) % 2
    report.x, report.y = x, y
    local view, budget = observe("inspect_container", {})
    if not view then return stop("container_observation_unavailable") end
    local ownSession = view.status ~= "open"
    local index
    if ownSession then
        if view.reason ~= "container_not_open" then return stop(view.reason) end
        local walk = not tile.preflight.withinConservativeReach
        -- Reserve open, inspect, one transfer, refresh and close before moving.
        if budget.actions < (walk and 4 or 3) or budget.calls < (walk and 6 or 5) then
            return stop("insufficient_budget")
        end
        if walk then
            local nav = api("navigate_to", {x=(x + 1) * 16, y=(y + 2) * 16})
            if not nav.completed then return stop(nav.reason, nav.status) end
        end
        local opened = api("open_container", {x=x, y=y})
        if not opened.completed then return stop(opened.reason, opened.status) end
        index = opened.evidence.chestIndex
        view, budget = observe("inspect_container", {})
    end
    local function sameChest(box)
        return box and box.x == x and box.y == y and box.typeId == cell.tileType
            and (not index or box.chestIndex == index)
    end
    if not view or view.status ~= "open" or not sameChest(view.container) then
        return stop(ownSession and "container_context_changed" or "different_container_open")
    end
    if budget.actions < 1 or budget.calls < 1 then return stop("insufficient_budget") end
    local box = view.container
    index = box.chestIndex
    local move = api("transfer_container_amount", {direction=direction, typeId=item.typeId,
        count=count, containerToken=box.containerToken})
    local reason
    if move.completed then
        local e = move.evidence
        if not (move.valid and move.effectVerified and type(e) == "table"
                and math.tointeger(e.movedCount) and e.movedCount >= 0 and e.movedCount <= count) then
            return stop("transfer_evidence_mismatch", "unknown")
        end
        -- The host verifies that every move has the requested type and a single chest index.
        if e.movedCount > 0 and e.moves[1].chestIndex ~= index then
            return stop("transfer_evidence_mismatch", "unknown")
        end
        report.moved = e.movedCount
        report.remaining = count - e.movedCount
        if report.remaining > 0 then reason = move.reason end
    elseif move.dispatchState == "not_admitted" and move.continuationAllowed then
        reason = move.reason
    else
        return stop(move.reason, move.status == "unknown" and "unknown" or nil)
    end
    if ownSession then
        view = observe("inspect_container", {})
        if not view or view.status ~= "open" or not sameChest(view.container) then
            return stop("container_context_changed")
        end
        local closed = api("close_container", {containerToken=view.container.containerToken})
        if not closed.completed then return stop(closed.reason, closed.status) end
    end
    if reason then return stop(reason) end
    report.status = "completed"
    return report
end

return {
    put=function(target, contentKey, count) return transfer("put", target, contentKey, count) end,
    take=function(target, contentKey, count) return transfer("take", target, contentKey, count) end,
}
