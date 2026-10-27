-- Trusted, inspectable Lua composition. Every game read/action uses the original api callback.
local cell_at = ...
local used = false
local function abs(x) if x < 0 then return -x end return x end
local function min(a,b) if a < b then return a end return b end
local function max(a,b) if a > b then return a end return b end
local function integer(value, fallback, low, high)
    if value == nil then return fallback end
    assert(type(value) == 'number' and math.tointeger(value) and value >= low and value <= high,
        'explore: invalid bounded integer option')
    return math.tointeger(value)
end

return function(options)
    assert(not used, 'explore: one loop per Lua run; do not reset exploration memory')
    used = true
    options = options or {}
    assert(type(options) == 'table', 'explore: options must be a table')
    for key in pairs(options) do
        assert(key == 'direction' or key == 'maxSteps' or key == 'stepPixels' or key == 'maxDistance',
            'explore: unexpected option')
    end
    local asked = options.direction or 'auto'
    assert(asked == 'auto' or asked == 'left' or asked == 'right', 'explore: direction is auto/left/right')
    local maxSteps = integer(options.maxSteps, 6, 1, 8)
    local stride = integer(options.stepPixels, 128, 64, 128)
    local maxDistance = integer(options.maxDistance, 768, 64, 1024)
    local direction = asked == 'left' and -1 or 1
    local visits, landmarks, landmarkKeys = {}, {}, {}
    local report = {kind='exploration_report', status='stopped', reason='not_started',
        steps=json.array(), decisions=json.array(), landmarks=json.array(), distancePixels=0,
        reversals=0, noNovelSteps=0, memoryScope='this Lua run and bound actor only'}
    local function finish(reason)
        report.reason = reason
        report.landmarks = json.array(landmarks)
        log('explore stop=' .. reason .. ' steps=' .. tostring(#report.steps)
            .. ' distance=' .. tostring(report.distancePixels))
        return report
    end
    local function readPlayer()
        local r = api('get_player_state', {})
        if not r.valid or not r.player or not r.player.position then return nil end
        return r
    end
    local p = readPlayer()
    if not p then return finish('player_unavailable') end
    local world, generation, slot = p.worldSessionId, p.actorGeneration, p.actorSlot
    if type(world) ~= 'string' or type(generation) ~= 'number' or type(slot) ~= 'number'
        or p.player.source ~= 'server_ai_player' then return finish('server_ai_identity_required') end
    local function same(r)
        return r.valid and r.worldSessionId == world and r.actorGeneration == generation and r.actorSlot == slot
    end
    local view = api('observe_terrain', {})
    if not same(view) or not view.data or not view.data.terrain then return finish('observation_unavailable') end
    local x = p.player.position.x
    report.start = {x=x, y=p.player.position.y}
    report.finish = report.start
    report.identity = {worldSessionId=world, actorGeneration=generation, actorSlot=slot}
    report.visited = {minX=x, maxX=x}
    visits[math.floor(x/64)] = 1
    local content = api('resolve_content', {kind='tile', query='Terraria/Trees'})
    local treeType = nil
    if same(content) and content.data.status == 'ok' and #content.data.candidates == 1 then
        treeType = content.data.candidates[1].typeId
    end

    -- Conservative direction filter only. The original native navigator still plans and validates each move.
    -- Full/one-way flat supports, at most two tiles up/down; gaps, slopes, hazards and unknown cells reject.
    local function candidate(sign, distance)
        local t = view.data.terrain
        local function cell(tx,ty)
            local row = t.rows[ty-t.originY+1]
            if not row then return '?' end
            local index = tx-t.originX+1
            if index < 1 or index > #row then return '?' end
            -- Strings have no metatable in this sandbox: use the trusted grid helper.
            return cell_at(row, index, index)
        end
        local target = p.player.position.x + sign*distance
        if target < 0 then return nil, 'world_boundary' end
        local bucket = math.floor(target/64)
        if (visits[bucket] or 0) >= 2 then return nil, 'revisit_limit' end
        local feet = math.floor((p.player.position.y+t.playerHeight)/16)
        local first = math.floor(p.player.position.x/16)
        local last = math.floor((target+t.playerWidth-0.01)/16)
        if sign < 0 then first = math.floor(target/16); last = math.floor((p.player.position.x+t.playerWidth-0.01)/16) end
        for tx=first,last do
            for ty=feet-6,feet+2 do
                local c=cell(tx,ty)
                if c == '?' or c == '!' then return nil, 'unknown_or_hazard' end
            end
            local supported = false
            for ty=feet-2,feet+2 do
                local c=cell(tx,ty)
                if c == '#' or c == '=' then
                    if cell(tx,ty-1)=='.' and cell(tx,ty-2)=='.' and cell(tx,ty-3)=='.' then
                        supported = true
                        break
                    end
                end
            end
            if not supported then return nil, 'gap_tall_obstacle_or_unsupported_shape' end
        end
        return target, 'local_candidate'
    end

    for step=1,maxSteps do
        local player, t = p.player, view.data.terrain
        if player.dead or player.life <= 0 then return finish('actor_unavailable') end
        if not t.grounded or not t.supported then return finish('unsupported_motion_state') end
        if report.distancePixels >= maxDistance then return finish('distance_limit') end
        local threat = api('scan_entities', {category='npc',npcFilter='hostile_creature',radius=96,maxResults=1})
        if not same(threat) then return finish('entity_observation_unavailable') end
        if threat.data.matchedCount > 0 then return finish('nearby_hostile') end
        if treeType then
            local b = view.data.bounds
            local found = api('scan_tiles', {minX=max(0,b.minX),minY=max(0,b.minY),maxX=b.maxX,maxY=b.maxY,
                tileTypes={treeType},maxResults=4})
            if not same(found) then return finish('tile_observation_unavailable') end
            for _, item in ipairs(found.data.items) do
                local key=tostring(item.x)..':'..tostring(item.y)
                if not landmarkKeys[key] and #landmarks < 32 then
                    landmarkKeys[key]=true
                    landmarks[#landmarks+1]={x=item.x,y=item.y,typeId=item.typeId,
                        contentKey=item.contentKey,gameUpdate=found.gameUpdate,observedAtUnixMs=found.observedAtUnixMs}
                end
            end
            report.lastTileCoverage={bounds=found.data.bounds,complete=found.data.complete,
                unknownCount=found.data.unknownCount,truncated=found.data.truncated,matchedCount=found.data.matchedCount,
                countUnit='tile cells, not trees'}
        end
        local target, rejected = nil, nil
        local allowance=min(stride,maxDistance-report.distancePixels-12) -- reserve existing native arrival tolerance
        if allowance < 32 then return finish('distance_limit') end
        for attempt=1,2 do
            for offset=0,2 do
                local distance=allowance-offset*32
                if distance >= 32 then
                    target,rejected=candidate(direction,distance)
                    if target then break end
                end
            end
            if target or asked ~= 'auto' or attempt == 2 then break end
            if report.reversals >= 2 then return finish('reversal_limit') end
            direction=-direction; report.reversals=report.reversals+1
        end
        report.decisions[#report.decisions+1]={step=step,direction=direction,targetX=target or json.null,
            basis=target and 'observed_local_corridor' or rejected}
        if not target then return finish(rejected == 'revisit_limit' and rejected or 'no_safe_direction') end
        local beforeX, beforeLife = player.position.x, player.life
        log('explore step='..tostring(step)..' direction='..tostring(direction)..' targetX='..tostring(target))
        local action=api('navigate_to_x',{targetX=target})
        if not action.valid or not action.completed or action.endEvidenceStatus ~= 'known' then
            report.actionStatus=action.status; report.actionReason=action.reason
            return finish('action_'..tostring(action.status))
        end
        p=readPlayer()
        if not p or not same(p) then return finish('player_observation_unavailable') end
        local afterX=p.player.position.x
        local moved=abs(afterX-beforeX)
        local oldMin, oldMax=report.visited.minX, report.visited.maxX
        report.visited.minX=min(oldMin,afterX);report.visited.maxX=max(oldMax,afterX)
        local novel=(oldMin-report.visited.minX)+(report.visited.maxX-oldMax)
        report.noNovelSteps=novel < 16 and report.noNovelSteps+1 or 0
        report.distancePixels=report.distancePixels+moved
        report.finish={x=afterX,y=p.player.position.y}
        for bucket=math.floor(min(beforeX,afterX)/64),math.floor(max(beforeX,afterX)/64) do
            visits[bucket]=(visits[bucket] or 0)+1
        end
        report.steps[#report.steps+1]={step=step,direction=direction,targetX=target,fromX=beforeX,toX=afterX,
            movedPixels=moved,newHorizontalPixels=novel,gameUpdate=p.gameUpdate}
        log('explore progress='..tostring(moved)..' novel='..tostring(novel))
        if p.player.life < beforeLife or p.player.dead then return finish('health_decreased') end
        if direction*(afterX-beforeX) < 24 then return finish('no_progress') end
        if report.noNovelSteps >= 3 then return finish('no_new_ground') end
        view=api('observe_terrain',{})
        if not same(view) or not view.data or not view.data.terrain then return finish('observation_unavailable') end
    end
    return finish('step_limit')
end
