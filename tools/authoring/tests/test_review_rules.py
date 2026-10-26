import copy
import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from script_store import Store, Invalid, RuleViolation, Conflict
import review_rules
from project import initialize
from test_script_store import manuscript


class ReviewRulesTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        initialize(self.temp.name, '試験')
        self.path = Path(self.temp.name) / '.authoring' / 'scripts.sqlite3'
        self.store = Store(self.path)
        doc = manuscript(); doc['events'][0]['rows'][0]['text'] = '古い表記を使う。'
        self.store.save(doc, 0, 'human', 'ユーザー')
        posted = self.store.comment(1, 'e1', '古い表記は正しい表記へ。', 'human', 'ユーザー', 'r1')
        self.comment_id = posted['commentId']

    def rule(self, **extra):
        return dict(kind='literal', bad='古い表記', good='正しい表記', reason='ユーザーの表記指摘', scope='project',
                    eventUid='', speaker='', field='text', match='contains', status='active', commentId=self.comment_id, **extra)

    def test_literal_match_cases_shared_with_browser(self):
        cases = json.loads(Path(__file__).with_name('review-rule-cases.json').read_text())
        for case in cases:
            with self.subTest(case=case):
                self.assertEqual(review_rules.matches(case['text'], case), case['expected'])

    def test_confirmed_rule_blocks_agent_but_keeps_human_draft_writable(self):
        state = self.store.rule(self.rule(), 'human', 'ユーザー')
        self.assertEqual(state['revision'], 1)
        draft = copy.deepcopy(state['document']); draft['events'][0]['title'] = 'タイトルだけを改稿'
        with self.assertRaises(RuleViolation) as failure:
            self.store.save(draft, 1, 'agent', 'Codex')
        self.assertEqual(failure.exception.issues[0]['rowId'], 'r1')
        self.assertEqual(self.store.read(), state)
        human = self.store.save(draft, 1, 'human', 'ユーザー')
        draft['events'][0]['rows'][0]['text'] = '正しい表記を使う。'
        saved = self.store.save(draft, human['revision'], 'agent', 'Codex')
        self.assertEqual(review_rules.inspect(saved['document']['events'][0], saved['rules'])['issues'], [])

    def test_proposal_cannot_self_approve_and_latest_rule_is_used(self):
        state = self.store.rule(self.rule(), 'agent', 'Codex')
        rule = state['rules'][0]
        self.assertEqual(rule['status'], 'proposed')
        draft = copy.deepcopy(state['document']); draft['events'][0]['title'] = '作業中'
        state = self.store.save(draft, 1, 'agent', 'Codex')
        approved = dict(rule, status='active')
        with self.assertRaises(Invalid): self.store.rule(approved, 'agent', 'Codex', rule['id'], rule['version'])
        self.store.rule(approved, 'human', 'ユーザー', rule['id'], rule['version'])
        draft['events'][0]['title'] = '取得後にルールが追加された場合'
        with self.assertRaises(RuleViolation): self.store.save(draft, state['revision'], 'agent', 'Codex')
        with self.assertRaises(Conflict): self.store.rule(dict(rule, status='disabled'), 'human', 'ユーザー', rule['id'], rule['version'])
        self.assertEqual(self.store.history()[0]['kind'], 'rule')

    def test_resolved_comments_and_source_survive_row_deletion_and_backup(self):
        state = self.store.rule(self.rule(), 'human', 'ユーザー')
        rule = state['rules'][0]
        self.store.resolve(self.comment_id, True, 'human', 'ユーザー')
        doc = state['document']; doc['events'][0]['rows'] = []
        state = self.store.save(doc, 1, 'agent', 'Codex')
        ctx = review_rules.context(state, doc['events'][0])
        self.assertEqual(ctx['userComments'][0]['resolved'], 1)
        self.assertEqual(ctx['rules'][0]['id'], rule['id'])
        self.assertEqual(ctx['rules'][0]['source']['quote'], '古い表記を使う。')
        backup = self.path.with_name('backup.sqlite3'); self.store.backup(backup)
        self.assertEqual(Store(backup).read(), state)

    def test_scopes_fields_and_reminders(self):
        e = manuscript()['events'][0]
        e['rows'].append(dict(id='r2', type='dialogue', text='古い表記', speaker='霍月英', expression='古い表記'))
        e['rows'].append(dict(id='r3', type='choice', text='', options=[dict(id='o1', text='古い表記', condition='古い表記', target='')]))
        e['title'] = '古い表記'; e['notes'] = '古い表記'
        fields = self.rule(); fields.update(id='rule', scope='speaker', speaker='霍月英')
        self.assertEqual([x['rowId'] for x in review_rules.inspect(e, [fields])['issues']], ['r2'])
        fields.update(scope='event', eventUid='different')
        self.assertEqual(review_rules.inspect(e, [fields])['issues'], [])
        fields.update(scope='project', field='metadata')
        self.assertEqual([x['field'] for x in review_rules.inspect(e, [fields])['issues']], ['title'])
        fields['field'] = 'text'
        self.assertEqual([x['field'] for x in review_rules.inspect(e, [fields])['issues']], ['text', 'option'])
        fields.update(kind='reminder', reason='説明的な台詞を避ける')
        report = review_rules.inspect(e, [fields])
        self.assertEqual(report['issues'], []); self.assertEqual(len(report['reminders']), 1)

    def test_legacy_database_migration_preserves_manuscripts_comments_and_history(self):
        before, history = self.store.read(), self.store.history()
        with sqlite3.connect(self.path) as db:
            db.execute('DROP TABLE review_rules'); db.execute('PRAGMA user_version=1')
        reopened = Store(self.path)
        self.assertEqual(reopened.read(), before); self.assertEqual(reopened.history(), history)

    def test_cli_context_and_latest_save_rules(self):
        self.store.rule(self.rule(), 'human', 'ユーザー')
        self.store.resolve(self.comment_id, True, 'human', 'ユーザー')
        cli = [sys.executable, '-B', str(Path(__file__).resolve().parents[1] / 'author.py'), '--project', self.temp.name]
        result = subprocess.run(cli + ['checkout', '--event', 'e1'], check=True, capture_output=True, text=True)
        payload = json.loads(result.stdout)['data']
        self.assertEqual(len(payload['reviewContext']['userComments']), 1)
        self.assertTrue(payload['reviewContext']['userComments'][0]['resolved'])
        payload['event']['title'] = '改稿'
        path = self.path.with_name('edit.json'); path.write_text(json.dumps(payload))
        result = subprocess.run(cli + ['save', '--input', str(path)], capture_output=True, text=True)
        self.assertEqual(result.returncode, 4)
        self.assertEqual(json.loads(result.stdout)['issues'][0]['code'], 'REVIEW_RULE_VIOLATION')
        payload['event']['rows'][0]['text'] = '正しい表記を使う。'
        path.write_text(json.dumps(payload))
        subprocess.run(cli + ['save', '--input', str(path)], check=True, capture_output=True)

    def test_disable_rule_allows_revision_and_keeps_audit_history(self):
        state = self.store.rule(self.rule(), 'human', 'ユーザー'); rule = state['rules'][0]
        disabled = self.store.rule(dict(rule, status='disabled'), 'human', 'ユーザー', rule['id'], rule['version'])
        self.assertEqual(self.store.entry(disabled['cursor'])['detail']['before']['status'], 'active')
        self.assertEqual(self.store.entry(disabled['cursor'])['detail']['after']['status'], 'disabled')
        self.assertEqual(review_rules.context(disabled, disabled['document']['events'][0])['rules'][0]['status'], 'disabled')
        doc = disabled['document']; doc['events'][0]['title'] = '改稿'
        self.store.save(doc, 1, 'agent', 'Codex')

    def test_no_silent_rule_creation_from_ordinary_or_resolved_comments(self):
        self.store.resolve(self.comment_id, True, 'human', 'ユーザー')
        snapshot = self.store.read()
        other = manuscript()['events'][0]; other['uid'] = 'other'
        self.assertEqual(snapshot['rules'], [])
        self.assertEqual(len(review_rules.context(snapshot, other)['userComments']), 1)


if __name__ == '__main__': unittest.main()
