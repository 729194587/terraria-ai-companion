import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from agent_memory import Notebook, LIMIT


class NotebookTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.world, self.actor = '1'*32, '2'*32
        self.notes = Notebook(self.root, self.world, self.actor)
        self.turn = dict(text='这里是家', senderName='甲', senderId=0, eventId='first')

    def write(self, **extra):
        args = dict(command='remember', name='家', description='基地在东边')
        return self.notes.invoke({**args, **extra}, self.turn, 'session-1')

    def test_bound_scope_and_restart(self):
        bridge = Mock(supported_atomic_operations={'query_map'})
        bridge.command.return_value = {'data':{'available':True,'data':{
            'worldUid':self.world,'characterId':self.actor}}}
        self.write(quote='这里是家',content='靠近森林晶塔')
        reopened = Notebook.from_bridge(bridge,self.root)
        note = reopened.invoke(dict(command='recall',name='家'),{},'another-session')['note']
        self.assertEqual(('甲','这里是家','session-1'),tuple(note['origin'][k] for k in ('senderName','quote','session')))
        self.assertEqual('靠近森林晶塔',note['content'])
        self.assertEqual([],Notebook(self.root,self.world,'3'*32).index())
        self.assertEqual([],Notebook(self.root,'4'*32,self.actor).index())
        with self.assertRaises(ValueError):
            Notebook(self.root,'../old',self.actor)
        bridge.supported_atomic_operations = set()
        self.assertIsNone(Notebook.from_bridge(bridge,self.root))

    def test_revision_forget_and_provenance(self):
        self.write(quote='这里是家')
        with self.assertRaises(ValueError):
            self.write(quote='搬家了')
        with self.assertRaises(ValueError):
            self.write(senderId=7)
        self.write(description='上次在东边看到了箱子')
        self.assertEqual(len(self.notes.index()),1)
        recalled = self.notes.invoke({'command':'recall','name':'家'}, {}, 's')
        self.assertEqual('agent_note',recalled['note']['origin']['kind'])
        self.notes.invoke({'command':'forget','name':'家'}, {}, 's')
        self.assertEqual([],Notebook(self.root,self.world,self.actor).index())

    def test_full_index_allows_replacement(self):
        for i in range(LIMIT):
            self.write(name=str(i))
        self.assertEqual('notebook_full',self.write()['reason'])
        self.assertTrue(self.write(name='0')['valid'])
