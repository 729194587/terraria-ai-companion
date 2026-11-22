local function talk_to(contentKey)
    assert(type(contentKey) == "string" and #contentKey > 0, "expected NPC content key")
    local targetRef, targetType
    local query = {category="npc", radius=1600}
    for attempt=1,5 do
        local scan = api("scan_entities", query)
        if not scan.valid or not scan.available then
            return {status="unavailable", reason=scan.reason, entityRef=targetRef}
        end
        local npc
        for _,row in ipairs(scan.data.items) do
            local details = row.details
            if details.contentKey == contentKey and type(details.entityRef) == "string"
                and (not targetRef or details.entityRef == targetRef) then
                npc = row
                break
            end
        end
        if not npc then
            return {status="unavailable", reason="target_not_observed", entityRef=targetRef,
                    truncated=scan.data.truncated, complete=scan.data.complete}
        end
        targetRef = npc.details.entityRef
        if not targetType then
            targetType = npc.typeId
            query.typeIds = {targetType}
        end
        if npc.distancePixels <= 80 then
            local dialogue = api("open_npc_dialogue", {entityRef=targetRef})
            local e = dialogue.evidence
            if dialogue.valid and dialogue.completed and e and e.open and e.entityRef == targetRef
                and type(e.text) == "string" and #e.text > 0 then
                return {status="completed", entityRef=targetRef, contentKey=contentKey, text=e.text}
            end
            if not dialogue.continuationAllowed or dialogue.reason ~= "npc_out_of_range" then
                return {status="failed", reason=dialogue.reason or "dialogue_unconfirmed", entityRef=targetRef}
            end
        elseif attempt < 5 then
            local feet = npc.details.bottomWorldPixels
            local nav = api("navigate_to", {x=feet.x,y=feet.y})
            if not nav.completed then
                return {status=nav.status, reason=nav.reason, entityRef=targetRef}
            end
        end
    end
    return {status="unavailable", reason="approach_limit", entityRef=targetRef}
end
return {talk_to=talk_to}
