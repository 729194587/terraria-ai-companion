local function find(contentKey, radiusTiles, offset)
    local r = api("find_container_item", {item=contentKey, radiusTiles=radiusTiles, offset=offset})
    if r.valid and r.available then return r.data end
    return {status="unavailable", reason=r.reason}
end

return {find=find}
