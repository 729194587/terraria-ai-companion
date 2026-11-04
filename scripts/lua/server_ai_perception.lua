local terrain = api('observe_terrain', {})
assert(terrain.valid and terrain.available)
local map = terrain.data.terrain
assert(#map.rows == 29 and terrain.data.scannedCount == 1305)
local dirt = api('resolve_content', {kind='tile',query='Terraria/Dirt'})
assert(dirt.valid and dirt.data.status == 'ok')
local id = dirt.data.candidates[1].typeId
local tiles = api('scan_tiles', {minX=map.originX,minY=map.originY,
    maxX=map.originX+44,maxY=map.originY+28,tileTypes={id},maxResults=1})
assert(tiles.valid and tiles.data.complete and tiles.data.truncated)
assert(tiles.data.returnedCount == 1 and tiles.data.matchedCount > 1)
local found = tiles.data.items[1]
local tile = api('inspect_tile', {x=found.x,y=found.y})
assert(tile.valid and tile.data.cell.tileType == id and tile.data.content == 'Terraria/Dirt')
assert(tile.data.preflight.selectedItem.inventoryToken == json.null)
local unknown = api('scan_tiles', {minX=99998,minY=99998,maxX=99999,maxY=99999,tileTypes={id}})
assert(unknown.valid and not unknown.data.complete and unknown.data.unknownCount == 4)
local outside = api('inspect_tile', {x=100000,y=100000})
assert(outside.valid and outside.data.cell.knowledge == 'out_of_world')
local players = api('scan_entities', {category='player',radius=1600})
assert(players.valid)
for _, player in ipairs(players.data.items) do assert(player.slot ~= terrain.actorSlot) end
local npcs = api('scan_entities', {category='npc',radius=1600,typeIds={1}})
assert(npcs.valid and npcs.data.returnedCount >= 1)
local drops = api('scan_entities', {category='item',radius=1600,typeIds={3}})
assert(drops.valid and drops.data.returnedCount >= 1)
local again = api('observe_terrain', {})
assert(again.valid and again.gameUpdate >= terrain.gameUpdate)
assert(again.observedAtUnixMs >= terrain.observedAtUnixMs)
return {terrain=terrain, tiles=tiles, inspected=tile, unknown=unknown,
    outside=outside, players=players, npcs=npcs, drops=drops, content=dirt,
    finalTick=again.gameUpdate, finalTime=again.observedAtUnixMs}
