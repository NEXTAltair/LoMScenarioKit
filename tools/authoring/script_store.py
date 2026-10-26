#!/usr/bin/env python3
"""Shared manuscript store and the agent's audited editing commands (stdlib only)."""
import argparse
import json
import sqlite3
import sys
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
import review_rules

ROOT = Path(__file__).resolve().parent
DEFAULT_DB = Path.cwd() / '.authoring' / 'scripts.sqlite3'
TYPES = {'dialogue', 'narration', 'direction', 'choice', 'label', 'jump', 'end'}


class Invalid(ValueError):
    pass


class Conflict(Exception):
    def __init__(self, revision):
        self.revision = revision
        super().__init__('別の編集が保存されています。最新版を確認してから保存してください。')


class RuleViolation(Invalid):
    def __init__(self, issues):
        self.issues = issues
        super().__init__(f'確定した表記ルールへの違反が{len(issues)}件あります。修正してから保存してください。')


def now():
    return datetime.now(timezone.utc).isoformat(timespec='milliseconds').replace('+00:00', 'Z')


def dump(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':'))


def empty_project():
    return dict(format='lom-event-scripts', version=1, updatedAt='', activeId='', events=[])


def string(value, label, nonempty=False, maximum=None):
    if not isinstance(value, str) or (nonempty and not value.strip()):
        raise Invalid(f'{label}は空でない文字列が必要です。' if nonempty else f'{label}は文字列が必要です。')
    if maximum and len(value) > maximum:
        raise Invalid(f'{label}は{maximum}文字以内にしてください。')
    return value


def validate(document):
    """Use the same v1 interchange contract as the browser, rejecting partial data."""
    if not isinstance(document, dict) or document.get('format') != 'lom-event-scripts' or type(document.get('version')) is not int or document['version'] != 1:
        raise Invalid('台本のJSON形式が不正です。')
    result = empty_project()
    result['updatedAt'] = string(document.get('updatedAt'), '保存日時')
    result['activeId'] = string(document.get('activeId'), '選択中の台本')
    events = document.get('events')
    if not isinstance(events, list) or len(events) > 1000:
        raise Invalid('台本一覧の形式または件数が不正です。')
    event_ids, count = set(), 0
    for event in events:
        if not isinstance(event, dict):
            raise Invalid('台本の形式が不正です。')
        clean = {k: string(event.get(k), k) for k in ('uid', 'sourceId', 'title', 'period', 'route', 'place', 'status', 'notes')}
        if not clean['uid'] or clean['uid'] in event_ids:
            raise Invalid('台本IDが空または重複しています。')
        event_ids.add(clean['uid'])
        if not clean['route'].strip() or clean['status'] not in {'draft', 'writing', 'review', 'done'}:
            raise Invalid('ルートまたは執筆状況が不正です。')
        clean['verdict'] = event.get('verdict', '')
        if clean['verdict'] not in {'', 'good', 'fix', 'hold', 'reject'}:
            raise Invalid('判定タグが不正です。')
        rows = event.get('rows')
        if not isinstance(rows, list):
            raise Invalid('行一覧の形式が不正です。')
        count += len(rows)
        if count > 30000:
            raise Invalid('行数の上限は30000行です。')
        clean['rows'], row_ids = [], set()
        for row in rows:
            if not isinstance(row, dict):
                raise Invalid('行の形式が不正です。')
            r = {k: string(row.get(k), k) for k in ('id', 'type', 'text')}
            if not r['id'] or r['id'] in row_ids or r['type'] not in TYPES:
                raise Invalid('行IDまたは種類が不正です。')
            row_ids.add(r['id'])
            if r['type'] == 'dialogue':
                r.update({k: string(row.get(k), k) for k in ('speaker', 'expression')})
            if r['type'] == 'jump':
                r['target'] = string(row.get('target'), '移動先')
            if r['type'] == 'choice':
                options = row.get('options')
                if not isinstance(options, list) or len(options) > 100:
                    raise Invalid('選択肢一覧の形式または件数が不正です。')
                r['options'], ids = [], set()
                for option in options:
                    if not isinstance(option, dict):
                        raise Invalid('選択肢の形式が不正です。')
                    o = {k: string(option.get(k), k) for k in ('id', 'text', 'target', 'condition')}
                    if not o['id'] or o['id'] in ids:
                        raise Invalid('選択肢IDが空または重複しています。')
                    ids.add(o['id'])
                    r['options'].append(o)
            clean['rows'].append(r)
        result['events'].append(clean)
    if result['activeId'] not in event_ids:
        result['activeId'] = result['events'][0]['uid'] if result['events'] else ''
    return result


FIELDS = dict(title='タイトル', sourceId='イベントID', period='時期', route='ルート', place='場所', status='執筆状況', notes='制作メモ', verdict='判定タグ', type='種類', text='本文', speaker='話者', expression='表情', target='移動先', options='選択肢')


def changes(before, after):
    result = []
    old = {e['uid']: e for e in before['events']}
    new = {e['uid']: e for e in after['events']}
    for uid in dict.fromkeys([*old, *new]):
        a, b = old.get(uid), new.get(uid)
        event = b or a
        def add(field, left, right, row_id=''):
            result.append(dict(eventUid=uid, eventTitle=event['title'], sourceId=event['sourceId'], rowId=row_id, field=field, before=left, after=right))
        if a is None or b is None:
            add('台本を追加' if a is None else '台本を削除', a, b)
            continue
        for key in ('title', 'sourceId', 'period', 'route', 'place', 'status', 'notes', 'verdict'):
            if a.get(key, '') != b.get(key, ''):
                add(FIELDS[key], a.get(key, ''), b.get(key, ''))
        ar, br = {r['id']: r for r in a['rows']}, {r['id']: r for r in b['rows']}
        for rid in dict.fromkeys([*ar, *br]):
            x, y = ar.get(rid), br.get(rid)
            if x is None or y is None:
                add('行を追加' if x is None else '行を削除', x, y, rid)
            else:
                for key in dict.fromkeys([*x, *y]):
                    if key != 'id' and x.get(key) != y.get(key):
                        add(FIELDS.get(key, key), x.get(key), y.get(key), rid)
        common = set(ar) & set(br)
        if [r['id'] for r in a['rows'] if r['id'] in common] != [r['id'] for r in b['rows'] if r['id'] in common]:
            add('行の並び順', [r['id'] for r in a['rows']], [r['id'] for r in b['rows']])
    if list(old) != list(new) and not result:
        result.append(dict(eventUid='', eventTitle='', sourceId='', rowId='', field='台本の並び順', before=list(old), after=list(new)))
    return result


class Store:
    def __init__(self, path=DEFAULT_DB):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.connection() as db:
            version = db.execute('PRAGMA user_version').fetchone()[0]
            if version not in (0, 1, 2):
                raise Invalid('この保存データは新しいバージョンで作成されています。')
            db.executescript('''
                CREATE TABLE IF NOT EXISTS head (id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL, document TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS history (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT NOT NULL,
                    actor TEXT NOT NULL CHECK(actor IN ('human','agent')), name TEXT NOT NULL, kind TEXT NOT NULL,
                    message TEXT NOT NULL, event_uids TEXT NOT NULL, detail TEXT NOT NULL,
                    snapshot TEXT, parent_revision INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS comments (id TEXT PRIMARY KEY, event_uid TEXT NOT NULL, row_id TEXT NOT NULL,
                    parent_id TEXT NOT NULL, quote TEXT NOT NULL, text TEXT NOT NULL, actor TEXT NOT NULL,
                    name TEXT NOT NULL, timestamp TEXT NOT NULL, resolved INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS review_rules (id TEXT PRIMARY KEY, document TEXT NOT NULL);
                PRAGMA user_version=2;
            ''')
            db.execute('INSERT OR IGNORE INTO head VALUES (1,0,?)', (dump(empty_project()),))

    @contextmanager
    def connection(self, write=False):
        db = sqlite3.connect(self.path, timeout=10)
        db.row_factory = sqlite3.Row
        try:
            db.execute('BEGIN IMMEDIATE' if write else 'BEGIN')
            yield db
            db.commit()
        except BaseException:
            db.rollback()
            raise
        finally:
            db.close()

    def _snapshot(self, db):
        head = db.execute('SELECT * FROM head WHERE id=1').fetchone()
        return dict(revision=head['revision'], document=json.loads(head['document']),
                    comments=[dict(r) for r in db.execute('SELECT * FROM comments ORDER BY timestamp,id')],
                    rules=[json.loads(r['document']) for r in db.execute('SELECT document FROM review_rules ORDER BY rowid')],
                    cursor=db.execute('SELECT COALESCE(MAX(id),0) FROM history').fetchone()[0])

    def read(self):
        with self.connection() as db:
            return self._snapshot(db)

    def _expected(self, db, expected):
        revision = db.execute('SELECT revision FROM head WHERE id=1').fetchone()[0]
        if type(expected) is not int or expected < 0:
            raise Invalid('読み込んだ時点の更新番号が必要です。')
        if revision != expected:
            raise Conflict(revision)

    def _log(self, db, actor, name, kind, message, event_uids, detail, snapshot=None, parent=0):
        if actor not in ('human', 'agent'):
            raise Invalid('編集者の種類が不正です。')
        string(name, '編集者名', True, 100)
        string(message, '変更メモ', maximum=2000)
        cur = db.execute('INSERT INTO history (timestamp,actor,name,kind,message,event_uids,detail,snapshot,parent_revision) VALUES (?,?,?,?,?,?,?,?,?)',
                         (now(), actor, name, kind, message, dump(event_uids), dump(detail), dump(snapshot) if snapshot is not None else None, parent))
        return cur.lastrowid

    def _save(self, db, document, expected, actor, name, message='', kind='save'):
        self._expected(db, expected)
        document = validate(document)
        current = self._snapshot(db)
        diff = changes(current['document'], document)
        if not diff:
            return current
        document['updatedAt'] = now()
        ids = list(dict.fromkeys(c['eventUid'] for c in diff if c['eventUid']))
        if actor == 'agent':
            issues = [issue for event in document['events'] if event['uid'] in ids
                      for issue in review_rules.inspect(event, current['rules'])['issues']]
            if issues: raise RuleViolation(issues)
        message = message or f'台本を編集（{len(ids)}件・{len(diff)}箇所）'
        revision = self._log(db, actor, name, kind, message, ids, dict(changes=diff), document, expected)
        db.execute('UPDATE head SET revision=?,document=? WHERE id=1', (revision, dump(document)))
        return self._snapshot(db)

    def save(self, document, expected, actor, name, message=''):
        with self.connection(write=True) as db:
            return self._save(db, document, expected, actor, name, message)

    def rule(self, raw, actor, name, rule_id='', expected=None):
        """Agents may propose; only the human entry point adopts or changes rules."""
        try:
            fields = review_rules.definition(raw)
        except ValueError as error:
            raise Invalid(str(error)) from error
        with self.connection(write=True) as db:
            snapshot = self._snapshot(db)
            previous = next((r for r in snapshot['rules'] if r['id'] == rule_id), None)
            if rule_id and previous is None: raise Invalid('ルールが見つかりません。')
            if previous and (type(expected) is not int or previous['version'] != expected):
                raise Conflict(previous['version'])
            if actor == 'agent':
                if previous: raise Invalid('エージェントは確定ルールを書き換えられません。新しい案として提案してください。')
                fields['status'] = 'proposed'
            if fields['scope'] == 'event' and fields['status'] != 'disabled' and not any(e['uid'] == fields['eventUid'] for e in snapshot['document']['events']):
                raise Invalid('適用先の台本が見つかりません。')
            source = next((c for c in snapshot['comments'] if c['id'] == fields['commentId']), None)
            if fields['commentId'] and source is None: raise Invalid('元のコメントが見つかりません。')
            if previous and fields['commentId'] != previous['commentId']:
                raise Invalid('ルールの出典コメントは変更できません。別のルールとして作成してください。')
            for other in snapshot['rules']:
                if other['id'] == rule_id: continue
                if all(other[k] == fields[k] for k in review_rules.DEFINITION): return snapshot
                same = all(other[k] == fields[k] for k in ('scope', 'eventUid', 'speaker', 'field', 'match', 'bad'))
                if same and fields['kind'] == other['kind'] == 'literal' and fields['status'] == other['status'] == 'active' and fields['good'] != other['good']:
                    raise Invalid('同じNG表記に別の推奨表記が登録されています。既存ルールを編集するか無効にしてください。')
            if previous and all(previous[k] == fields[k] for k in review_rules.DEFINITION): return snapshot
            rule = dict(fields, id=rule_id or str(uuid.uuid4()),
                        createdAt=previous['createdAt'] if previous else now(),
                        createdBy=previous['createdBy'] if previous else actor,
                        createdName=previous['createdName'] if previous else name,
                        updatedAt=now(), updatedBy=actor, updatedName=name,
                        source=dict(source) if source else None)
            affected = [fields['eventUid']] if fields['scope'] == 'event' else []
            if source and source['event_uid'] not in affected: affected.append(source['event_uid'])
            detail = dict(before=previous, after=rule)
            rule['version'] = self._log(db, actor, name, 'rule', '再発防止ルールを更新' if previous else '再発防止ルールを登録', affected, detail, parent=snapshot['revision'])
            # Persist the same version in both the rule and its audit entry.
            db.execute('UPDATE history SET detail=? WHERE id=?', (dump(detail), rule['version']))
            db.execute('INSERT INTO review_rules VALUES (?,?) ON CONFLICT(id) DO UPDATE SET document=excluded.document', (rule['id'], dump(rule)))
            return self._snapshot(db)

    def history(self, event_uid='', actor='', before=0, limit=50):
        if actor and actor not in ('human', 'agent'):
            raise Invalid('編集者の絞り込みが不正です。')
        with self.connection() as db:
            query, params = 'SELECT id,timestamp,actor,name,kind,message,event_uids,parent_revision FROM history WHERE 1=1', []
            if actor:
                query += ' AND actor=?'; params.append(actor)
            if before:
                query += ' AND id<?'; params.append(before)
            query += ' ORDER BY id DESC'
            entries = []
            for row in db.execute(query, params):
                ids = json.loads(row['event_uids'])
                if event_uid and event_uid not in ids:
                    continue
                entries.append({k: row[k] for k in ('id', 'timestamp', 'actor', 'name', 'kind', 'message', 'parent_revision')})
                entries[-1]['eventUids'] = ids
                if len(entries) >= max(1, min(limit, 200)):
                    break
            return entries

    def entry(self, entry_id):
        with self.connection() as db:
            row = db.execute('SELECT * FROM history WHERE id=?', (entry_id,)).fetchone()
            if not row:
                raise Invalid('履歴が見つかりません。')
            result = dict(row)
            for key in ('detail', 'event_uids', 'snapshot'):
                result[key] = json.loads(result[key]) if result[key] is not None else None
            return result

    def comment(self, expected, event_uid, text, actor, name, row_id='', parent_id=''):
        string(text, 'コメント', True, 20000)
        with self.connection(write=True) as db:
            self._expected(db, expected)
            snapshot = self._snapshot(db)
            event = next((e for e in snapshot['document']['events'] if e['uid'] == event_uid), None)
            if event is None:
                raise Invalid('コメント先の台本が見つかりません。')
            quote = ''
            if parent_id:
                parent = db.execute('SELECT * FROM comments WHERE id=? AND event_uid=?', (parent_id, event_uid)).fetchone()
                if not parent or parent['parent_id']:
                    raise Invalid('返信先が見つかりません。')
                row_id, quote = parent['row_id'], parent['quote']
            elif row_id:
                row = next((r for r in event['rows'] if r['id'] == row_id), None)
                if row is None:
                    raise Invalid('コメント先の行が見つかりません。')
                quote = row['text']
            c = dict(id=str(uuid.uuid4()), event_uid=event_uid, row_id=row_id, parent_id=parent_id,
                     quote=quote, text=text, actor=actor, name=name, timestamp=now(), resolved=0)
            self._log(db, actor, name, 'comment', '添削コメントへの返信' if parent_id else '添削コメントを追加', [event_uid], dict(comment=c), parent=expected)
            db.execute('INSERT INTO comments (id,event_uid,row_id,parent_id,quote,text,actor,name,timestamp,resolved) VALUES (:id,:event_uid,:row_id,:parent_id,:quote,:text,:actor,:name,:timestamp,:resolved)', c)
            result = self._snapshot(db)
            result['commentId'] = c['id']
            return result

    def resolve(self, comment_id, resolved, actor, name):
        if type(resolved) is not bool:
            raise Invalid('解決状態は真偽値が必要です。')
        with self.connection(write=True) as db:
            c = db.execute('SELECT * FROM comments WHERE id=?', (comment_id,)).fetchone()
            if not c or c['parent_id']:
                raise Invalid('対象のコメントが見つかりません。')
            if bool(c['resolved']) != resolved:
                revision = db.execute('SELECT revision FROM head WHERE id=1').fetchone()[0]
                self._log(db, actor, name, 'resolve', 'コメントを解決済みに変更' if resolved else 'コメントを未解決に戻す', [c['event_uid']], dict(commentId=comment_id, resolved=resolved, text=c['text']), parent=revision)
                db.execute('UPDATE comments SET resolved=? WHERE id=?', (int(resolved), comment_id))
            return self._snapshot(db)

    def restore(self, expected, entry_id, event_uid, actor, name):
        with self.connection(write=True) as db:
            self._expected(db, expected)
            target = db.execute('SELECT snapshot FROM history WHERE id=?', (entry_id,)).fetchone()
            if not target or not target['snapshot']:
                raise Invalid('復元できる台本の履歴ではありません。')
            event = next((e for e in json.loads(target['snapshot'])['events'] if e['uid'] == event_uid), None)
            if event is None:
                raise Invalid('この時点には対象の台本がありません。')
            document = self._snapshot(db)['document']
            index = next((i for i, e in enumerate(document['events']) if e['uid'] == event_uid), None)
            if index is None:
                document['events'].append(event)
            else:
                document['events'][index] = event
            return self._save(db, document, expected, actor, name, f'履歴 #{entry_id} の台本を復元', 'restore')

    def backup(self, target):
        target = Path(target)
        if target.resolve() == self.path.resolve():
            raise Invalid('保存元とは別のファイルを指定してください。')
        with sqlite3.connect(self.path) as source, sqlite3.connect(target) as destination:
            source.backup(destination)


def identify(document, ref):
    exact = [e for e in document['events'] if e['uid'] == ref]
    matches = exact or [e for e in document['events'] if e['sourceId'] == ref]
    if len(matches) != 1:
        raise Invalid('台本を一意に特定できません。showでuidを確認してください。')
    return matches[0]
