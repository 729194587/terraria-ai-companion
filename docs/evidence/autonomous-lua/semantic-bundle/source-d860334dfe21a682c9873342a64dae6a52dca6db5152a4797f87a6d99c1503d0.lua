-- L1 read-only composition. No persistent map or cross-sample entity identity.
return function(options)
    options = options or {}
    for k in pairs(options) do
        assert(k == 'maxObjects' or k == 'radius' or k == 'trees' or k == 'ores'
            or k == 'drops' or k == 'oreNames' or k == 'dropTypes', 'sense_world: unknown option')
    end
    for _,k in ipairs({'trees','ores','drops'}) do
        assert(options[k] == nil or type(options[k]) == 'boolean', 'sense_world: category must be boolean')
    end
    local trees, ores, drops = options.trees ~= false, options.ores ~= false, options.drops ~= false
    local allOres = {'Copper','Tin','Iron','Lead','Silver','Tungsten','Gold','Platinum',
        'Demonite','Crimtane','Meteorite','Hellstone','Cobalt','Palladium','Mythril','Orichalcum','Adamantite','Titanium','Chlorophyte'}
    local known = {}; for _,name in ipairs(allOres) do known[name]=true end
    local function list(value, maximum, valid)
        assert(type(value) == 'table' and #value >= 1 and #value <= maximum, 'sense_world: invalid filter list')
        local seen, count = {}, 0
        for k,v in pairs(value) do
            assert(type(k) == 'number' and math.tointeger(k) and k >= 1 and k <= #value
                and valid(v) and not seen[v], 'sense_world: invalid or duplicate filter')
            seen[v]=true; count=count+1
        end
        assert(count == #value, 'sense_world: sparse filter')
        return value
    end
    local oreNames = allOres
    if options.oreNames ~= nil then
        assert(ores, 'sense_world: oreNames requires ores')
        oreNames=list(options.oreNames,19,function(v) return type(v) == 'string' and known[v] == true end)
    end
    if options.dropTypes ~= nil then
        assert(drops, 'sense_world: dropTypes requires drops')
        list(options.dropTypes,32,function(v) return type(v) == 'number' and math.tointeger(v) and v >= 0 and v <= 65535 end)
    end
    local cap = options.maxObjects or 8
    local radius = options.radius or 640
    assert(math.tointeger(cap) and cap >= 1 and cap <= 16, 'sense_world: maxObjects 1..16')
    assert(type(radius) == 'number' and radius >= 1 and radius <= 1600, 'sense_world: radius 1..1600')
    local view = api('observe_terrain', {})
    assert(view.valid and view.worldSessionId and view.actorGeneration and view.actorSlot, 'sense_world: server identity required')
    local report = {kind='semantic_world', identity={worldSessionId=view.worldSessionId,
        actorGeneration=view.actorGeneration, actorSlot=view.actorSlot, binding=view.binding}, sources=json.array(),
        requested={trees=trees,ores=ores,drops=drops,oreNames=ores and oreNames or json.array(),dropTypes=options.dropTypes},
        drops=json.array(), objects=json.array(), unresolvedContent=json.array(), omittedObjects=0,
        scope='local observations; no line-of-sight, reachability or pickup guarantee; no persistent object IDs'}
    local function read(name,args)
        local r = api(name,args)
        assert(r.valid and r.available ~= false and r.worldSessionId == view.worldSessionId
            and r.actorGeneration == view.actorGeneration and r.actorSlot == view.actorSlot,
            'sense_world: unavailable or changed identity')
        assert(r.binding and view.binding and r.binding.actor == view.binding.actor
            and r.binding.world == view.binding.world and r.binding.generation == view.binding.generation
            and r.binding.slot == view.binding.slot, 'sense_world: changed binding')
        assert(type(r.gameUpdate) == 'number' and type(r.observedAtUnixMs) == 'number', 'sense_world: missing sample time')
        return r
    end
    local function source(r,operation)
        local d=r.data
        report.sources[#report.sources+1]={operation=operation, gameUpdate=r.gameUpdate,
            observedAtUnixMs=r.observedAtUnixMs, clock=r.clock, bounds=d.bounds,
            complete=d.complete, truncated=d.truncated, unknownCount=d.unknownCount,
            matchedCount=d.matchedCount, returnedCount=d.returnedCount, radiusPixels=d.radiusPixels, originWorldPixels=d.originWorldPixels}
        return #report.sources
    end
    source(view,'observe_terrain')
    local types, treeType = {}, nil
    local names={}
    if trees then names[#names+1]='Trees' end
    if ores then for _,name in ipairs(oreNames) do names[#names+1]=name end end
    for _,name in ipairs(names) do
        local r=read('resolve_content',{kind='tile',query='Terraria/'..name})
        local d=r.data
        if d.status == 'ok' and not d.truncated and #d.candidates == 1 then
            local id=d.candidates[1].typeId
            types[#types+1]=id
            if name == 'Trees' then treeType=id end
        else report.unresolvedContent[#report.unresolvedContent+1]='Terraria/'..name end
    end
    local b=view.data.bounds
    if #types > 0 then
        local scan=read('scan_tiles',{minX=b.minX,minY=b.minY,maxX=b.maxX,maxY=b.maxY,tileTypes=types,maxResults=256})
        local d=scan.data
        local sample=source(scan,'scan_tiles')
        local cells,seen={},{}
        local function key(x,y) return tostring(x)..':'..tostring(y) end
        for _,t in ipairs(d.items) do cells[key(t.x,t.y)]=t end
        for _,seed in ipairs(d.items) do
            local sk=key(seed.x,seed.y)
            if not seen[sk] then
                local tree=seed.typeId == treeType
                local queue={seed}; seen[sk]=true
                local head=1
                local bounds={minX=seed.x,maxX=seed.x,minY=seed.y,maxY=seed.y}
                local target=seed
                local edge=false
                local lateral=false
                local coords=json.array()
                while head <= #queue do
                    local t=queue[head]; head=head+1
                    coords[#coords+1]={t.x,t.y}
                    if t.x < bounds.minX then bounds.minX=t.x end
                    if t.x > bounds.maxX then bounds.maxX=t.x end
                    if t.y < bounds.minY then bounds.minY=t.y end
                    if t.y > bounds.maxY then bounds.maxY=t.y end
                    if tree and t.y > target.y or not tree and t.distancePixels < target.distancePixels then target=t end
                    if t.x <= b.minX or t.x >= b.maxX or t.y <= b.minY or t.y >= b.maxY then edge=true end
                    for _,offset in ipairs({{0,-1},{0,1},{-1,0},{1,0}}) do
                        local k=key(t.x+offset[1],t.y+offset[2])
                        local n=cells[k]
                        if n and n.typeId == seed.typeId then
                            if tree and offset[1] ~= 0 then lateral=true
                            elseif not seen[k] then seen[k]=true; queue[#queue+1]=n end
                        end
                    end
                end
                if #report.objects < cap then
                    local o={kind=tree and 'tree_trunk' or 'ore_vein', source=sample,
                        typeId=seed.typeId, contentKey=seed.contentKey, observedTileCount=#queue,
                        bounds=bounds, inference=tree and 'vertical contiguous Trees column; lateral ownership unresolved' or 'same-type four-neighbour component',
                        complete=not tree and d.complete == true and d.truncated == false and d.unknownCount == 0 and not edge,
                        boundaryTouched=edge, ownership=tree and (lateral and 'ambiguous_lateral_tiles' or 'inferred_column') or 'observed_connectivity',
                        candidate={x=target.x,y=target.y, status='unconfirmed'}}
                    -- Trees are losslessly represented by a vertical inclusive range; ores need exact coordinates.
                    if not tree then o.tiles=coords end
                    local check=read('inspect_tile',{x=target.x,y=target.y})
                    o.candidate.source=source(check,'inspect_tile')
                    local c=check.data.cell
                    o.candidate.status=c and c.knowledge == 'read' and c.hasTile and c.tileType == seed.typeId and 'observed_present' or 'changed_or_unknown'
                    if o.candidate.status ~= 'observed_present' then o.complete=false end
                    local p=check.data.preflight
                    if p and p ~= json.null then o.candidate.withinConservativeReach=p.withinConservativeReach end
                    o.candidate.action='mine_tile'; o.candidate.actionable='unknown; select suitable tool and re-inspect before action'
                    report.objects[#report.objects+1]=o
                else report.omittedObjects=report.omittedObjects+1 end
            end
        end
    end
    if drops then
    local drops=read('scan_entities',{category='item',radius=radius,maxResults=cap,typeIds=options.dropTypes})
    local sample=source(drops,'scan_entities')
    report.drops=json.array()
    for _,e in ipairs(drops.data.items) do
        report.drops[#report.drops+1]={source=sample, observationSlot=e.slot, typeId=e.typeId,
            contentKey=e.details.contentKey, stack=e.details.stack, centerWorldPixels=e.centerWorldPixels,
            velocityPixelsPerTick=e.velocityPixelsPerTick, distancePixels=e.distancePixels,
            navigationHint={targetCenterX=e.centerWorldPixels.x, targetCenterY=e.centerWorldPixels.y},
            verification='re-scan; inventory item count delta required; disappearance does not prove pickup',
            identity='sample only; slot can be reused', reachable='unknown'}
    end
    end
    return report
end
