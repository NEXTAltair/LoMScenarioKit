import copy
import json
import subprocess
import sys
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from script_store import Conflict, Invalid, Store, empty_project
from script_server import EditorServer
from project import initialize


def manuscript():
    doc = empty_project()
    doc['activeId'] = 'e1'
    doc['events'] = [dict(uid='e1', sourceId='C_SOC_01', title='試験用', period='', route='common',
                          place='', status='draft', verdict='', notes='', rows=[dict(id='r1', type='dialogue', text='元の台詞', speaker='趙活', expression='')])]
    return doc


class StoreTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        initialize(self.temp.name, '試験')
        self.path = Path(self.temp.name) / '.authoring' / 'scripts.sqlite3'
        self.store = Store(self.path)
        self.first = self.store.save(manuscript(), 0, 'human', 'ユーザー')

    def test_revision_attribution_and_noop(self):
        doc = copy.deepcopy(self.first['document'])
        doc['updatedAt'] = 'ignored'
        self.assertEqual(self.store.save(doc, 1, 'human', 'ユーザー')['revision'], 1)
        doc['events'][0]['rows'][0]['text'] = '改稿'
        saved = self.store.save(doc, 1, 'agent', 'Codex', '台詞を調整')
        self.assertEqual(saved['revision'], 2)
        entry = self.store.entry(2)
        self.assertEqual(entry['actor'], 'agent')
        self.assertEqual(entry['detail']['changes'][0]['before'], '元の台詞')
        self.assertEqual(entry['detail']['changes'][0]['after'], '改稿')
        self.assertEqual([r['id'] for r in self.store.history(actor='human')], [1])
        with self.assertRaises(Conflict):
            self.store.save(manuscript(), 1, 'human', 'ユーザー')
        self.assertEqual(len(self.store.history()), 2)

    def test_concurrent_saves_have_one_winner(self):
        def save(name):
            doc = manuscript()
            doc['events'][0]['title'] = name
            try:
                return self.store.save(doc, 1, 'agent', name)['revision']
            except Conflict:
                return 'conflict'
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertCountEqual(list(pool.map(save, ['A', 'B'])), [2, 'conflict'])

    def test_comments_survive_deletion_and_restore_is_scoped(self):
        state = self.store.comment(1, 'e1', '語尾を確認', 'agent', 'Codex', 'r1')
        comment = state['comments'][0]
        self.assertEqual(state['revision'], 1)
        self.store.comment(1, 'e1', '確認した', 'human', 'ユーザー', parent_id=comment['id'])
        state = self.store.resolve(comment['id'], True, 'human', 'ユーザー')
        doc = state['document']
        doc['events'][0]['rows'] = []
        other = copy.deepcopy(manuscript()['events'][0]); other['uid'] = 'e2'; other['title'] = '別の台本'
        doc['events'].append(other)
        state = self.store.save(doc, 1, 'human', 'ユーザー')
        self.assertEqual(state['comments'][0]['quote'], '元の台詞')
        restored = self.store.restore(state['revision'], 1, 'e1', 'human', 'ユーザー')
        self.assertEqual(restored['document']['events'][0]['rows'][0]['text'], '元の台詞')
        self.assertEqual(restored['document']['events'][1], other)
        self.assertEqual(len(restored['comments']), 2)
        self.assertEqual(self.store.history()[0]['kind'], 'restore')
        self.assertEqual(len(self.store.history('e1')), 6)
        backup = self.path.with_name('backup.sqlite3')
        self.store.backup(backup)
        self.assertEqual(Store(backup).read(), restored)
        self.assertEqual(Store(backup).history(), self.store.history())

    def test_invalid_data_rolls_back(self):
        bad = manuscript(); bad['events'][0]['rows'] *= 2
        with self.assertRaises(Invalid):
            self.store.save(bad, 1, 'agent', 'Codex')
        with self.assertRaises(Invalid):
            self.store.comment(1, 'e1', 'test', 'agent', 'Codex', 'missing')
        with self.assertRaises(Invalid):
            self.store.save(manuscript(), True, 'human', 'ユーザー')
        self.assertEqual(self.store.read(), self.first)

    def test_deleted_manuscript_and_its_comments_can_be_recovered(self):
        self.store.comment(1, 'e1', '全体への添削', 'agent', 'Codex')
        deleted = self.store.save(empty_project(), 1, 'human', 'ユーザー')
        self.assertEqual(deleted['document']['events'], [])
        restored = self.store.restore(deleted['revision'], 1, 'e1', 'human', 'ユーザー')
        self.assertEqual(restored['document']['events'], self.first['document']['events'])
        self.assertEqual(restored['comments'][0]['text'], '全体への添削')

    def test_cli_checkout_save_and_comment(self):
        cli = [sys.executable, '-B', str(Path(__file__).resolve().parents[1] / 'author.py'), '--project', self.temp.name]
        checkout = self.path.with_name('edit.json')
        result = subprocess.run(cli + ['checkout', '--event', 'C_SOC_01'], check=True, capture_output=True, text=True)
        payload = json.loads(result.stdout)['data']; payload['event']['rows'][0]['text'] = 'CLIからの編集'
        checkout.write_text(json.dumps(payload))
        subprocess.run(cli + ['save', '--input', str(checkout), '--message', '改稿'], check=True, capture_output=True)
        self.assertEqual(self.store.history()[0]['actor'], 'agent')
        stale = subprocess.run(cli + ['save', '--input', str(checkout)], capture_output=True, text=True)
        self.assertEqual(stale.returncode, 3)
        self.assertEqual(json.loads(stale.stdout)['issues'][0]['code'], 'REVISION_CONFLICT')
        subprocess.run(cli + ['comment', '--event', 'e1', '--revision', '2', '--row', 'r1', '--text', '添削案'], check=True, capture_output=True)
        self.assertEqual(self.store.read()['comments'][0]['actor'], 'agent')

    def test_http_identity_conflicts_and_file_boundaries(self):
        server = EditorServer(('127.0.0.1', 0), self.store)
        worker = threading.Thread(target=server.serve_forever, daemon=True); worker.start()
        self.addCleanup(server.server_close); self.addCleanup(server.shutdown)
        url = f'http://127.0.0.1:{server.server_port}'
        def post(path, payload, origin=None):
            headers = {'Content-Type': 'application/json', 'X-Script-Editor': '1'}
            if origin: headers['Origin'] = origin
            return urlopen(Request(url + path, json.dumps(payload).encode(), headers), timeout=5)
        doc = manuscript(); doc['events'][0]['title'] = 'ブラウザ編集'
        with post('/api/save', dict(document=doc, revision=1, actor='agent')) as response:
            self.assertEqual(json.load(response)['revision'], 2)
        self.assertEqual(self.store.history()[0]['actor'], 'human')
        for origin, expected in [(None, 409), ('https://external.example', 403)]:
            with self.assertRaises(HTTPError) as error:
                post('/api/save', dict(document=doc, revision=1), origin)
            self.assertEqual(error.exception.code, expected)
            error.exception.close()
        for path in ('/manuscripts/scripts.sqlite3', '/.git/config', '/../AGENTS.md'):
            with self.assertRaises(HTTPError) as error: urlopen(url + path, timeout=5)
            self.assertEqual(error.exception.code, 404)
            error.exception.close()
        with self.assertRaises(HTTPError) as error:
            urlopen(Request(url + '/api/project', headers={'Host': 'external.example'}), timeout=5)
        self.assertEqual(error.exception.code, 403)
        error.exception.close()


if __name__ == '__main__':
    unittest.main()
