import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from sensed_resources import ResourceHistory


class ResourceTests(unittest.TestCase):
    def test_partial_scans_and_removal(self):
        h = ResourceHistory()
        scan = dict(bounds=dict(minX=0,minY=0,maxX=10,maxY=10), gameUpdate=1,
                    complete=True,truncated=False,unknownCells=0,items=[dict(kind='chest',typeId=21,
                    contentKey='Terraria/Containers',anchor=dict(x=4,y=4),bounds=dict(x=4,y=4,width=2,height=2),
                    status='incomplete')])
        self.assertEqual(h.observe(scan)['items'][0]['freshness'],'new')
        self.assertEqual(h.observe(scan)['items'][0]['freshness'],'known')
        partial = h.observe(dict(scan,items=[],truncated=True))
        self.assertEqual(partial['historical'][0]['state'],'needs_revalidation')
        empty = dict(scan,items=[],gameUpdate=3)
        self.assertEqual(len(h.observe(empty)['changes']),1)
        self.assertFalse(h.observe(empty)['changes'])
        self.assertEqual(h.observe(scan)['items'][0]['freshness'],'updated')
