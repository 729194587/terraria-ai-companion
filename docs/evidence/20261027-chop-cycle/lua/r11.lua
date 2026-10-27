-- C：选中铜斧后 use_item（无目标）
local sel = api('select_hotbar_slot', {slot = 3})
local use = api('use_item', {})
return {
  selected = sel and {completed = sel.completed, reason = sel.reason},
  use = {valid = use.valid, completed = use.completed, status = use.status,
    reason = use.reason, evidence = use.evidence, endEvidenceStatus = use.endEvidenceStatus},
}
