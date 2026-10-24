"""Opt-in real configured LLM -> Agent -> Lua smoke; game backend is simulated.

This sends two to four requests to the configured provider and may incur cost.
No Bridge connection is opened and no Terraria world is modified.
"""
import argparse
import asyncio
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from agent import Agent
from llm import LLMClient
from test_agent import event, Game as PlayerGame
from test_lua_runtime import RuntimeGame
from test_runtime_inventory import snapshot


class SimulatedGame(RuntimeGame):
    def get_state(self):
        return PlayerGame.get_state(self)

    def command(self, cmd, **parameters):
        reply = super().command(cmd, **parameters)
        if cmd == 'query_atomic' and parameters['operation'] == 'inspect_inventory':
            inventory = snapshot()
            inventory.update(worldSessionId='world', gameUpdate=4 + len(self.sent))
            data = inventory['data']
            selected = (self.slot + 9) % 10
            data.update(selectedSlot=selected, selectedHotbarKey=self.slot, simulated=True)
            if parameters['arguments'].get('mode') != 'full':
                data['inventory'] = [data['inventory'][selected]]
                data.pop('equipment')
            reply['data'] = inventory
        return reply


async def smoke():
    game, client = SimulatedGame(), LLMClient()
    agent = Agent(game, client, max_model_calls=4)
    try:
        agent.receive(event(1, text='这是模拟后端联通测试。请用execute_lua完成三步：'
            'inspect_inventory(mode=selected)读取所选快捷键，select_hotbar_slot(slot=2)选快捷键2，'
            '再inspect_inventory(mode=selected)核对。只需操作快捷栏，不移动或使用物品；回复明确这是模拟结果。'))
        await agent.task
        programs = [call for message in agent.history for call in message.get('tool_calls', [])
                    if call['function']['name'] == 'execute_lua']
        result = {'backend': 'simulated_game', 'model': client.model, 'luaPrograms': len(programs),
                  'selectedHotbarKey': game.slot,
                  'actions': [params['operation'] for cmd, params in game.sent if cmd == 'start_atomic'],
                  'replies': [params['text'] for cmd, params in game.sent if cmd == 'send_chat']}
        print(json.dumps(result, ensure_ascii=False))
        return 0 if programs and game.slot == 2 and result['actions'] == ['select_hotbar_slot'] else 1
    finally:
        await agent.close()
        await client.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--live', action='store_true', help='Authorize requests to the configured model provider')
    if not parser.parse_args().live:
        parser.error('--live is required; this smoke uses the real configured provider')
    raise SystemExit(asyncio.run(smoke()))
