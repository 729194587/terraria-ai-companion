local function base(contentKey, offset)
    local result = api("list_base_chests", {item=contentKey, offset=offset})
    if result.valid and result.available then return result.data end
    return {status="unavailable", reason=result.reason}
end

return {base=base}
