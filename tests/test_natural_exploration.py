import unittest


class NaturalReportTests(unittest.TestCase):
    def test_unknown_without_worker_value_keeps_known_prior_completions(self):
        from native_natural_navigation import completed_segments
        calls=[dict(operation='navigate_to',result=dict(completed=True)),
               dict(operation='navigate_to',result=dict(completed=False,status='unknown'))]
        self.assertEqual(1,completed_segments('explore',{},calls))

    def test_report_keeps_confirmed_segment_count(self):
        from native_natural_navigation import completed_segments
        calls=[dict(operation='navigate_to',result=dict(completed=True)) for _ in range(3)]
        self.assertEqual(2,completed_segments('explore',dict(progress=dict(completedSegments=2)),calls))

if __name__=='__main__':unittest.main()
