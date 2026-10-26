import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import project
from script_store import Store


class AuthorTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / '作品'
        self.cli = [sys.executable, '-B', str(Path(__file__).resolve().parents[1] / 'author.py'), '--project', str(self.root)]

    def command(self, *args, code=0):
        result = subprocess.run(self.cli + list(args), capture_output=True, text=True)
        self.assertEqual(result.returncode, code, result.stdout + result.stderr)
        value = json.loads(result.stdout)
        self.assertEqual(value['schema_version'], 1)
        self.assertEqual(value['ok'], code == 0)
        self.assertIsInstance(value['next_actions'], list)
        self.assertEqual(result.stderr, '')
        return value

    def test_discovery_and_parse_errors_never_create_a_project(self):
        self.command('commands')
        self.assertEqual(self.command('describe', '--command', 'save')['data']['parameters']['input']['required'], True)
        self.command('save', '--help')
        self.command('save', code=2)
        self.command('--project', code=2)
        self.command('unknown', code=2)
        self.assertFalse(self.root.exists())

    def test_init_custom_routes_dry_run_and_conflict(self):
        self.command('init', '--title', '汎用テスト')
        self.command('init', '--title', '上書き', code=2)
        config = self.root / 'project.json'
        data = json.loads(config.read_text()); data['routes'] = {'northern': '北方編'}
        config.write_text(json.dumps(data))
        created = self.command('new', '--title', '出会い', '--revision', '0')['data']
        uid = created['event']['uid']
        self.assertEqual(created['event']['route'], 'northern')
        payload = self.command('checkout', '--event', uid)
        payload['data']['event']['rows'] = [dict(id='line1', type='narration', text='テスト原稿'), dict(id='end', type='end', text='')]
        path = self.root / 'edit.json'; path.write_text(json.dumps(payload))
        dry = self.command('save', '--input', str(path), '--dry-run')['data']
        self.assertFalse(dry['written'])
        self.assertEqual(self.command('show')['data']['revision'], created['revision'])
        saved = self.command('save', '--input', str(path))['data']
        conflict = self.command('save', '--input', str(path), code=3)
        self.assertEqual(conflict['issues'][0]['code'], 'REVISION_CONFLICT')
        self.assertIn('show', conflict['next_actions'][0]['argv'])
        self.command('verdict', '--event', uid, '--value', 'fix', '--revision', str(saved['revision']))
        self.assertEqual(self.command('checkout', '--event', uid)['data']['event']['verdict'], 'fix')
        self.command('check')

    def test_legacy_import_keeps_ids_and_default_verdict(self):
        self.command('init', '--title', '移行')
        legacy = dict(format='lom-event-scripts', version=1, updatedAt='', activeId='legacy', events=[dict(
            uid='legacy', sourceId='C_SOC_01', title='原稿', route='runaway', status='writing', period='', place='', notes='',
            rows=[dict(id='r1', type='narration', text='元の本文')])])
        path = self.root / 'legacy.json'; path.write_text(json.dumps(legacy))
        self.command('import', '--input', str(path), '--revision', '0')
        event = self.command('checkout', '--event', 'C_SOC_01')['data']['event']
        self.assertEqual(event['uid'], 'legacy')
        self.assertEqual(event['rows'][0]['id'], 'r1')
        self.assertEqual(event['route'], 'runaway')
        self.assertEqual(event['verdict'], '')
        self.command('export', '--output', str(self.root / 'backup.sqlite3'), '--format', 'backup')
        self.assertEqual(Store(self.root / 'backup.sqlite3').read()['document']['events'][0], event)

    def test_graph_and_check_report_missing_targets_without_evaluating_notes(self):
        event = project.new_event('分岐')
        event['rows'] = [dict(id='choice', type='choice', text='', options=[
            dict(id='o1', text='進む', target='missing', condition='好感度20以上'),
            dict(id='o2', text='戻る', target='', condition='')]), dict(id='end', type='end', text='')]
        graph = project.graph(event)
        self.assertFalse(graph['conditionsEvaluated'])
        self.assertTrue(graph['edges'][0]['missing'])
        self.assertEqual(graph['edges'][0]['conditionNote'], '好感度20以上')
        self.assertIn('TARGET_NOT_FOUND', [i['code'] for i in project.inspect(event)])

    def test_outline_search_headings_and_symlinks(self):
        self.command('init', '--title', '資料')
        (self.root / 'outline' / 'notes.md').write_text('# 設定\n本文に針路\n```\n# 見出しではない\n```\n## 次の場面\n')
        outside = Path(self.temp.name) / 'private.md'; outside.write_text('公開しない')
        (self.root / 'outline' / 'external.md').symlink_to(outside)
        docs = self.command('outline', '--query', '針路')['data']
        self.assertEqual(len(docs), 1)
        self.assertEqual([h['line'] for h in docs[0]['headings']], [1, 6])
        self.assertEqual(len(self.command('outline')['data']), 1)

    def test_invalid_explicit_target_does_not_break_choice_fallthrough(self):
        event = project.new_event('分岐')
        event['rows'] = [dict(id='choice', type='choice', text='', options=[
            dict(id='o1', text='誤った移動先', target='line', condition=''),
            dict(id='o2', text='続ける', target='', condition='')]),
            dict(id='line', type='narration', text='次の行')]
        graph = project.graph(event)
        self.assertEqual([edge['missing'] for edge in graph['edges']], [True, False])
        self.assertTrue(graph['nodes'][1]['reachable'])
        self.assertEqual([issue['code'] for issue in project.inspect(event)], ['TARGET_NOT_FOUND'])


if __name__ == '__main__':
    unittest.main()
