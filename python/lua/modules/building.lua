local function validSurvey(q)
    local b = q.binding
    if not (q.valid and q.available and type(b) == "table" and type(b.actor) == "string"
        and type(q.worldSessionId) == "string" and type(q.actorGeneration) == "number"
        and type(q.actorSlot) == "number" and b.world == q.worldSessionId
        and b.generation == q.actorGeneration and b.slot == q.actorSlot) then return false end
    if type(q.data) ~= "table" or type(q.data.entries) ~= "table"
        or not math.tointeger(q.data.matched) or not math.tointeger(q.data.pending)
        or q.data.matched < 0 or q.data.pending < 0 or #q.data.entries == 0 then return false end
    local matched = 0
    for _, entry in ipairs(q.data.entries) do
        if type(entry) ~= "table" or type(entry.status) ~= "string" then return false end
        if entry.status == "already_matches" then matched = matched + 1 end
        if entry.status == "unknown_or_not_loaded" or entry.survey == "unknown_or_not_loaded" then return false end
    end
    return matched == q.data.matched and q.data.matched + q.data.pending == #q.data.entries
end

local function construct(blueprint, options)
    options = options or {}
    local ms = options.milliseconds or 1000
    assert(math.tointeger(ms) and ms >= 1 and ms <= 30000, "milliseconds must be 1..30000")
    local args = {blueprint=blueprint}
    local before = api("build_diff", args)
    local report = {status="unknown", before=before, task=json.null, after=json.null}
    if not validSurvey(before) then return report end
    if before.data.pending == 0 then
        report.status, report.after = "completed", before
        return report
    end
    local task = api("submit_task", {operation="build_place", arguments=args})
    if task.status == "running" and type(task.taskId) == "string" then
        task = api("await_task", {taskId=task.taskId, milliseconds=ms})
    end
    report.task, report.taskId, report.status = task, task.taskId, task.status
    if task.status == "running" or task.status == "unknown" then return report end
    local receipt = task.result
    if not task.valid or not receipt or not receipt.valid or receipt.operation ~= "build_place"
        or receipt.endEvidenceStatus ~= "known" or not receipt.endEvidence
        or receipt.worldSessionId ~= before.worldSessionId or receipt.endEvidence.actorGeneration ~= before.actorGeneration
        or receipt.actorSlot ~= before.actorSlot or receipt.actorId ~= before.binding.actor
        or not receipt.inputsReleased then
        if task.status == "completed" then report.status = "unknown" end
        return report
    end
    local after = api("build_diff", args)
    report.after = after
    if not validSurvey(after) or after.worldSessionId ~= before.worldSessionId or after.actorGeneration ~= before.actorGeneration
        or after.actorSlot ~= before.actorSlot or after.binding.actor ~= before.binding.actor then
        report.status = "unknown"
    elseif task.status == "completed" and (not task.completed or not receipt.completed or after.data.pending ~= 0) then
        report.status = "partial"
    elseif task.status == "failed" and receipt.reason == "build_partial" then
        report.status = "partial"
    end
    return report
end
return {construct=construct}
