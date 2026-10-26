"""Project configuration, manuscript diagnostics and read-only outline indexing."""
import json
import re
import uuid
from pathlib import Path

from script_store import Invalid, Store


def configuration(root):
    root = Path(root).resolve()
    path = root / 'project.json'
    if not path.is_file():
        raise Invalid('project.json がありません。init で制作プロジェクトを作成してください。')
    value = json.loads(path.read_text(encoding='utf-8'))
    if not isinstance(value, dict) or value.get('format') != 'lom-authoring-project' or value.get('version') != 1:
        raise Invalid('制作プロジェクトの形式に対応していません。')
    if not isinstance(value.get('title'), str) or not value['title'].strip():
        raise Invalid('作品名を指定してください。')
    routes = value.get('routes')
    if not isinstance(routes, dict) or not routes or not all(isinstance(k, str) and k.strip() and isinstance(v, str) and v.strip() for k, v in routes.items()):
        raise Invalid('routes はルートIDと表示名の対応表にしてください。')
    if not isinstance(value.get('speakers', []), list) or not all(isinstance(s, str) for s in value.get('speakers', [])):
        raise Invalid('speakers は話者名の配列にしてください。')
    value['storageKey'] = 'lom-authoring:' + str(root)
    return value


def initialize(root, title):
    if not isinstance(title, str) or not title.strip():
        raise Invalid('作品名を指定してください。')
    root = Path(root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    if (root / 'project.json').exists() or (root / '.authoring').exists():
        raise Invalid('既存の制作プロジェクトは上書きしません。')
    config = dict(format='lom-authoring-project', version=1, title=title, routes={'common': '共通'}, speakers=[])
    # Exclusive creation prevents an accidental overwrite during concurrent init.
    with (root / 'project.json').open('x', encoding='utf-8') as stream:
        json.dump(config, stream, ensure_ascii=False, indent=2)
        stream.write('\n')
    (root / 'outline').mkdir(exist_ok=True)
    store = Store(root / '.authoring' / 'scripts.sqlite3')
    return dict(project=str(root), configuration=config, revision=store.read()['revision'])


def open_store(root):
    configuration(root)
    path = Path(root).resolve() / '.authoring' / 'scripts.sqlite3'
    if not path.is_file():
        raise Invalid('共有保存データがありません。バックアップを復元してください。')
    return Store(path)


def new_event(title, route='common', source_id=''):
    return dict(uid='e_' + uuid.uuid4().hex, sourceId=source_id, title=title, route=route,
                period='', place='', status='draft', verdict='', notes='', rows=[])


def outline(root, query=''):
    root = Path(root).resolve()
    folder = root / 'outline'
    documents = []
    if not folder.exists():
        return documents
    for path in sorted(folder.rglob('*.md')):
        # Only files under the project's outline directory are exposed to the browser.
        if not path.resolve().is_relative_to(folder.resolve()) or not path.resolve().is_relative_to(root):
            continue
        if path.stat().st_size > 2 * 1024 * 1024:
            continue
        body = path.read_text(encoding='utf-8')
        headings, fence = [], False
        for number, line in enumerate(body.splitlines(), 1):
            if re.match(r'^\s*(```|~~~)', line):
                fence = not fence
            match = re.match(r'^(#{1,6})\s+(.+?)\s*#*$', line)
            if match and not fence:
                headings.append(dict(level=len(match[1]), title=match[2], line=number))
        if query.casefold() not in (path.name + '\n' + body).casefold():
            continue
        documents.append(dict(id=path.relative_to(root).as_posix(), title=headings[0]['title'] if headings else path.stem,
                              headings=headings, markdown=body))
    return documents


def graph(event):
    rows = event['rows']
    labels = {r['id'] for r in rows if r['type'] == 'label'}
    nodes = [dict(id=r['id'], type=r['type'], text=r['text'], row=i + 1) for i, r in enumerate(rows)]
    edges = []
    for i, row in enumerate(rows):
        following = rows[i + 1]['id'] if i + 1 < len(rows) else None
        if row['type'] == 'end':
            continue
        targets = [(following, '', '', False)]
        if row['type'] == 'jump':
            targets = [(row['target'], '', '', True)]
        elif row['type'] == 'choice':
            targets = [(o['target'] or following, o['text'], o['condition'], bool(o['target'])) for o in row['options']]
        for target, text, condition, explicit in targets:
            if target is not None:
                edges.append(dict(source=row['id'], target=target, text=text, conditionNote=condition,
                                  missing=bool(explicit and target not in labels)))
    reachable, pending = set(), [rows[0]['id']] if rows else []
    while pending:
        node = pending.pop()
        if node in reachable:
            continue
        reachable.add(node)
        pending.extend(e['target'] for e in edges if e['source'] == node and not e['missing'])
    for node in nodes:
        node['reachable'] = node['id'] in reachable
    return dict(eventUid=event['uid'], nodes=nodes, edges=edges, conditionsEvaluated=False)


def inspect(event):
    issues = []
    def add(code, message, row='', field='', severity='error'):
        issues.append(dict(code=code, message=message, severity=severity, eventUid=event['uid'], rowId=row, field=field))
    if not event['title'].strip():
        add('TITLE_EMPTY', 'タイトルが空です。', field='title')
    if not event['rows']:
        add('SCRIPT_EMPTY', '台本に行がありません。', field='rows')
    for row in event['rows']:
        kind = row['type']
        if kind in ('dialogue', 'narration', 'direction', 'label') and not row['text'].strip():
            add('TEXT_EMPTY', '本文が空です。', row['id'], 'text')
        if kind == 'dialogue' and not row['speaker'].strip():
            add('SPEAKER_EMPTY', '話者が空です。', row['id'], 'speaker')
        if kind == 'choice':
            if len(row['options']) < 2:
                add('CHOICES_MISSING', '選択肢を2件以上指定してください。', row['id'], 'options')
            for option in row['options']:
                if not option['text'].strip():
                    add('OPTION_EMPTY', '選択肢の本文が空です。', row['id'], 'options/' + option['id'])
    flow = graph(event)
    for edge in flow['edges']:
        if edge['missing']:
            add('TARGET_NOT_FOUND', '移動先の場面目印がありません。', edge['source'], 'target')
    for node in flow['nodes']:
        if not node['reachable']:
            add('UNREACHABLE_ROW', '先頭から接続されていません。', node['id'], severity='warning')
    return issues
