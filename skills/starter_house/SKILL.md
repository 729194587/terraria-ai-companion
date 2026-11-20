---
name: starter_house
description: 出生点早期基地。按地形生成 8×8 住宅蓝图，核对家具锚点、材料与阻挡，原生验房后确认向导住所。
game_version: Terraria 1.4.4.9
scope: Server AI 的 build_diff、build_place、住宅查询和分房能力。
---

先用 observe_scene 选择可安全站立、接近和进出的地段，查看已有建筑和容器。下例的 ox、oy 为房间外框左上角的绝对 Tile 坐标，底排 y=oy+7。底排若铺在地表之上，门外须有可通行的台阶或落脚点。frame、wall 是从当前注册表/背包确认的方块和背景墙物品 ID。下例家具 ID 对应原版木门、工作台、木椅、火把和箱子。

```lua
local function starter_house(ox, oy, frame, wall, includeChest)
    local b = {origin={x=ox,y=oy}, tiles={}, walls={}, objects={}}
    local function add(rows,x,y,id)
        rows[#rows+1]={x=x,y=y,itemType=id}
    end
    for x=0,7 do
        for y=0,7 do
            if x>0 and x<7 and y>0 and y<7 then
                add(b.walls,x,y,wall)
            elseif x~=0 or y<4 or y>6 then
                add(b.tiles,x,y,frame)
            end
        end
    end
    -- 用分散的探测锚点批量读取家具 Origin。最终图使用设计占地的左上角。
    local furniture={{0,4,25},{5,6,36},{1,5,34}}
    if includeChest then furniture[#furniture+1]={3,5,48} end
    local probes={}
    for i,f in ipairs(furniture) do add(probes,(i-1)*4,0,f[3]) end
    local torch={x=6,y=3,itemType=8}
    local probe=api('build_diff',{blueprint={origin=b.origin,objects=probes,tiles={torch}}})
    if not probe.valid then return nil,probe end
    for _,row in ipairs(probe.data.entries) do
        if row.status=='unknown_or_not_loaded' then return nil,row end
        if row.layer=='objects' then
            if row.status=='unsupported_content' then return nil,row end
            local f=furniture[(row.x-ox)/4+1]
            add(b.objects,f[1]+row.x-row.left,f[2]+row.y-row.top,f[3])
        elseif row.status=='unsupported_content' then
            add(b.objects,6,3,8)
        else
            add(b.tiles,6,3,8)
        end
    end
    return b
end
```

用上述函数生成计划后调用 `api('build_diff',{blueprint=plan})` 做整屋比对。保存选定原点、材料和箱子选项以便分批重建同一计划。材料清单之外还需计入合成家具的原料，已有可用箱子可作为储物区。新箱子占两格宽，应核对家具之间的净空及最终住所评分。

先解决明确的占地冲突。对 object_footprint_blocked，仅查询条目 left、top、width、height 覆盖的格；对 occupied_requires_dig，仅检查目标格。确认是妨碍施工的普通草株后，用 mine_target 清除并核验。遇到玩家设施则调整选址或布局。blocked_by_player 先确认谁占位，仅移动绑定 AI。plannedSupportCandidate 表示图中有候选支撑，可先施工支撑后复核。

需要工作台合成时先完成可支撑工作台的地板并放台，靠近后按材料缺口合成。通过 submit_task 分批提交同一计划的条目。部分完成先读 remainingByReason 并只处理对应目标。unknown 后停止变更。完工在室内 (ox+3,oy+3) 调 inspect_housing，通过后用新鲜向导 entityRef 执行 assign_npc_home，再用 inspect_npc_housing 核对房间登记与住所格。
