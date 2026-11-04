-- Read-only ordinary tree discovery around the bound server AI (not all tree variants).
local view = api('observe_terrain', {})
if not view.valid then return {observation=view} end
local content = api('resolve_content', {kind='tile',query='Terraria/Trees'})
if not content.valid or content.data.status ~= 'ok' then return {content=content} end
local b = view.data.bounds
local minX, minY = b.minX, b.minY
if minX < 0 then minX = 0 end
if minY < 0 then minY = 0 end
local found = api('scan_tiles', {minX=minX,minY=minY,maxX=b.maxX,maxY=b.maxY,
    tileTypes={content.data.candidates[1].typeId},maxResults=16})
return {trees=found}
