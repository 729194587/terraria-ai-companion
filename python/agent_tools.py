"""Lua program execution boundary; worker cleanup and receipts have one owner."""
import time

import lua_agent


class ToolExecutor:
    def __init__(self, runtime_factory, bridge):
        self.runtime = runtime_factory(bridge)
        self.receipt = None
        self.actions = []
        self.elapsed = 0

    async def execute(self, source, *, chat_id, limits, allow_actions, on_result):
        started = time.monotonic()
        def observe(operation, arguments, reply):
            import atomic_operations
            if operation in atomic_operations.ACTIONS:
                self.actions.append(reply)
            on_result(operation, arguments, reply)
        try:
            self.receipt = await self.runtime.execute(source, chat_id=chat_id, limits=limits,
                allow_actions=allow_actions, on_result=observe)
            return lua_agent.summarize(self.receipt, self.actions)
        finally:
            # LuaRuntime raises cancellation only after shielded game cleanup.
            self.elapsed = time.monotonic()-started
            if self.receipt is None:
                self.receipt = self.runtime.last_result
