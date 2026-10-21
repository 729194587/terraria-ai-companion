# LLM 与工具结果成本统计

运行 `python python/agent.py` 时，默认向 stderr 输出以 `Cost: ` 开头的单行 JSON。无需启用 Trace，不输出聊天、工具参数、观察内容、请求正文、密钥或 HTTP headers。可保存终端日志后筛选：

```powershell
python python/agent.py 2> .local/agent-cost.log
Get-Content .local/agent-cost.log | Where-Object { $_.StartsWith('Cost: ') }
```

- `llm_cost`：每次实际 HTTP 模型调用的请求 ID、耗时、完成／失败／取消状态及 API usage。输入、输出使用 Chat Completions 的 `prompt_tokens` / `completion_tokens`；缓存优先使用 `prompt_tokens_details.cached_tokens`，兼容 `prompt_cache_hit_tokens`。未返回或不合法的字段为 null，无法确认缓存时不计算非缓存输入。缓存是输入的子集，不能与输入相加。
- `tool_cost`：每个产生结果的工具调用名称与结果内容的 UTF-8 字节数，包含返回给模型的错误结果。执行中被取消且没有结果的工具不计入结果数。
- `model_context_cost`：每次准备调用模型时，列出上下文中的工具结果名称、UTF-8 字节数、此前提交次数及是否为完整观察。用 tool call ID 和实际内容摘要识别同一份结果；摘要只在内存使用。跨聊天仍保留的工具结果也会统计。完整观察被既有历史机制替换为过期提示后，不再算完整观察；过期提示本身仍是工具上下文。
- `chat_cost`：每个聊天任务完成、失败或取消时汇总。包含模型调用次数与累计耗时、工具结果数与字节数、累计提交及重复提交的工具／观察字节数。每项 Token 都提供 `known_sum`、`reported_requests`、`unreported_requests`；有缺失时合计仅代表已报告的部分，全部未知时为 null。调用次数包含失败和取消的请求，无法拿到 usage 的请求不会伪装成零成本。

记录带 UTC 时间和 Agent generation；`llm_cost` 另有客户端递增请求 ID。每个聊天任务的统计通过 ContextVar 隔离，被新聊天抢占的旧任务不会把 usage 累计到新任务。Token 仅使用 API 报告值，没有本地估算，没有换算货币价格。

字节数只计算发送给模型的工具 message `content`，不是 TCP 回执大小、完整 HTTP 请求大小或 Token 数。重复提交字节数是“该结果第二次及以后提交”的字节累计；准备／尝试提交不代表服务端一定收取了费用。不能据此把总输入 Token 精确归因于某份观察，也不能判断某份观察具体命中了多少缓存。API usage 的缓存数仅代表整次请求。

原有 `TERRARIA_AGENT_TRACE=1` 仍可启用详细 Trace，成本事件也会写入其中；详细 Trace 原有的聊天／完整上下文记录行为未改变。只分析成本时保持 Trace 关闭即可。该改动不改变工具预算、历史裁剪、模型参数或观察协议。
